package net.oxog.phonebox.deskpanel

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.app.AlertDialog
import android.app.Dialog
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.SurfaceTexture
import android.graphics.YuvImage
import android.graphics.drawable.GradientDrawable
import android.hardware.Camera
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Gravity
import android.view.MotionEvent
import android.view.Surface
import android.view.TextureView
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.abs

/**
 * Native consent and capture. No JavascriptInterface can approve or start a camera.
 *
 * Video goes over the USB cable only (see [AgentLink.cameraTarget]): up to 1920 × 1080 at 30 fps, JPEG-encoded on two
 * threads and sent by one uploader over a kept-alive connection, newest frame first. While sharing, a small floating
 * preview sits above the panel: the panel stays usable (swipe between pages as usual), the preview keeps the camera's
 * aspect ratio, can be dragged anywhere, and a tap switches it between small and large.
 */
@Suppress("DEPRECATION") // Camera's NV21 preview keeps the camera dependency-free on API 21+.
internal class PhoneCamera(private val activity: Activity, private val link: AgentLink) {
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    private val encoders = Executors.newFixedThreadPool(ENCODERS)
    private val uploader = Executors.newSingleThreadExecutor()
    @Volatile private var visible = false
    @Volatile private var generation = 0
    private var dialog: Dialog? = null
    private var overlay: View? = null
    private var camera: Camera? = null
    @Volatile private var target: AgentLink.CameraTarget? = null
    @Volatile private var request = ""
    @Volatile private var session = ""
    private var busy = false
    private var lastFrame = 0L
    private var lastAck = 0L
    private var captured = 0L
    private val encoding = AtomicInteger(0)
    private val latest = AtomicReference<Encoded?>(null)
    private val uploading = AtomicBoolean(false)
    @Volatile private var lastSent = 0L

    private class Encoded(val seq: Long, val gen: Int, val destination: AgentLink.CameraTarget, val id: String,
        val key: String, val jpeg: ByteArray, val rotation: Int)

    private val poll = object : Runnable {
        override fun run() {
            if (!visible) return
            if (target?.current() == false || (session.isNotEmpty() && SystemClock.elapsedRealtime() - lastAck > 3000)) stop()
            if (!busy && dialog == null && target == null) checkRequest()
            main.postDelayed(this, 1000)
        }
    }

    fun resume() { visible = true; main.removeCallbacks(poll); main.post(poll) }
    fun pause() { visible = false; main.removeCallbacks(poll); stop() }
    fun destroy() { pause(); worker.shutdown(); encoders.shutdown(); uploader.shutdown() }

    private fun checkRequest() {
        val destination = link.cameraTarget() ?: return // not on USB: a request from the computer simply expires
        val gen = generation
        busy = true
        worker.execute {
            val pending = runCatching {
                val reply = JSONObject(destination.call("poll"))
                if (reply.isNull("request")) "" else reply.getString("request")
            }.getOrDefault("")
            main.post {
                busy = false
                if (visible && gen == generation && destination.current() && pending.isNotEmpty()) {
                    target = destination; request = pending
                    confirm(destination, pending)
                }
            }
        }
    }

    private fun confirm(destination: AgentLink.CameraTarget, id: String) {
        val choices = (0 until Camera.getNumberOfCameras()).toList()
        if (choices.isEmpty()) { fail("No camera available"); return }
        val names = choices.map { index ->
            val info = Camera.CameraInfo(); Camera.getCameraInfo(index, info)
            if (info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT) "Front camera" else "Rear camera"
        }.toTypedArray()
        var selected = 0
        val consent = AlertDialog.Builder(activity)
            .setTitle("Share camera with ${destination.name}?")
            .setSingleChoiceItems(names, 0) { _, which -> selected = which }
            .setPositiveButton("Allow this session", null)
            .setNegativeButton("Deny") { _, _ -> stop() }
            .create()
        // Identity comes from the pinned destination, never from a remote HTML widget.
        val details = TextView(activity).apply {
            text = "Video only, over the USB cable, up to 1080p / 30 fps. This computer can record received video.\n" +
                "Certificate: ${destination.pin.take(16)}…\nSharing stops when you leave PhoneBox, lock the phone or unplug the cable."
            setPadding(24, 16, 24, 16)
        }
        consent.setView(details)
        consent.setOnCancelListener { stop() }
        dialog = consent
        consent.show()
        consent.getButton(AlertDialog.BUTTON_POSITIVE).apply {
            filterTouchesWhenObscured = true
            setOnClickListener {
                if (!visible || !destination.current()) { stop(); return@setOnClickListener }
                if (Build.VERSION.SDK_INT >= 23 && activity.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
                    // Permission UI may pause us. Never carry consent across that lifecycle transition.
                    stop(false)
                    activity.requestPermissions(arrayOf(Manifest.permission.CAMERA), PERMISSION_REQUEST)
                    return@setOnClickListener
                }
                dialog = null; consent.dismiss()
                approve(destination, id, choices[selected])
            }
        }
    }

    private fun approve(destination: AgentLink.CameraTarget, id: String, cameraId: Int) {
        val gen = generation
        busy = true
        worker.execute {
            val key = runCatching { JSONObject(destination.call("approve", id)).getString("session") }.getOrNull()
            main.post {
                busy = false
                if (!visible || gen != generation || !destination.current()) {
                    if (key != null && !worker.isShutdown) worker.execute { runCatching { destination.call("end", id, key) } }
                } else if (key == null) fail("Camera request expired. Request it again on the computer.")
                else { session = key; lastAck = SystemClock.elapsedRealtime(); showPreview(cameraId) }
            }
        }
    }

    // ---------------------------------------------------------------- preview overlay

    /** Keeps the camera picture's aspect ratio: the width is chosen, the height follows. Never stretches the video. */
    private class AspectFrame(context: Context) : FrameLayout(context) {
        var aspect = 16f / 9f
            set(value) { if (value > 0f && value != field) { field = value; requestLayout() } }
        override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
            val width = MeasureSpec.getSize(widthMeasureSpec)
            val height = (width / aspect).toInt()
            super.onMeasure(MeasureSpec.makeMeasureSpec(width, MeasureSpec.EXACTLY), MeasureSpec.makeMeasureSpec(height, MeasureSpec.EXACTLY))
        }
    }

    private fun dp(value: Int) = (value * activity.resources.displayMetrics.density).toInt()

    @SuppressLint("ClickableViewAccessibility")
    private fun showPreview(cameraId: Int) {
        val root = activity.findViewById<ViewGroup>(android.R.id.content) ?: run { fail("Cannot show the camera preview"); return }
        val texture = TextureView(activity)
        val frame = AspectFrame(activity).apply { addView(texture, FrameLayout.LayoutParams(-1, -1)) }
        val label = TextView(activity).apply {
            text = "● LIVE"
            setTextColor(Color.WHITE); textSize = 11f
            setPadding(dp(6), dp(2), dp(6), dp(2))
            background = GradientDrawable().apply { cornerRadius = dp(8).toFloat(); setColor(0xCCD32F2F.toInt()) }
        }
        frame.addView(label, FrameLayout.LayoutParams(-2, -2, Gravity.TOP or Gravity.START).apply { setMargins(dp(6), dp(6), 0, 0) })
        val info = TextView(activity).apply {
            text = "→ ${target?.name ?: "computer"} · USB"
            setTextColor(0xFFDDDDDD.toInt()); textSize = 11f; maxLines = 1
            ellipsize = android.text.TextUtils.TruncateAt.END
        }
        val stopButton = TextView(activity).apply {
            text = "Stop"
            setTextColor(Color.WHITE); textSize = 12f; gravity = Gravity.CENTER
            setPadding(dp(12), dp(4), dp(12), dp(4))
            background = GradientDrawable().apply { cornerRadius = dp(12).toFloat(); setColor(0xFFD32F2F.toInt()) }
            filterTouchesWhenObscured = true
            contentDescription = "Stop sharing the camera"
            setOnClickListener { stop() }
        }
        val bar = LinearLayout(activity).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(8), dp(6), dp(6), dp(6))
            addView(info, LinearLayout.LayoutParams(0, -2, 1f))
            addView(stopButton, LinearLayout.LayoutParams(-2, -2))
        }
        val card = LinearLayout(activity).apply {
            orientation = LinearLayout.VERTICAL
            background = GradientDrawable().apply { cornerRadius = dp(14).toFloat(); setColor(0xF0181818.toInt()) }
            setPadding(dp(4), dp(4), dp(4), 0)
            clipToOutline = true
            elevation = dp(8).toFloat()
            addView(frame, LinearLayout.LayoutParams(-1, -2))
            addView(bar, LinearLayout.LayoutParams(-1, -2))
        }
        var large = false
        fun cardWidth(): Int {
            val screen = root.width.takeIf { it > 0 } ?: activity.resources.displayMetrics.widthPixels
            return (screen * if (large) 0.5f else 0.26f).toInt().coerceAtLeast(dp(160))
        }
        fun keepInside() {
            val maxX = (root.width - card.width - dp(12)).toFloat().coerceAtLeast(0f)
            val maxY = (root.height - card.height - dp(12)).toFloat().coerceAtLeast(0f)
            card.translationX = card.translationX.coerceIn(dp(12).toFloat().coerceAtMost(maxX), maxX)
            card.translationY = card.translationY.coerceIn(dp(12).toFloat().coerceAtMost(maxY), maxY)
        }
        // Drag to move; a tap toggles small / large. Touches outside the card still reach the panel underneath.
        val slop = ViewConfiguration.get(activity).scaledTouchSlop
        var downX = 0f; var downY = 0f; var startX = 0f; var startY = 0f; var dragging = false
        frame.setOnTouchListener { _, event ->
            when (event.actionMasked) {
                MotionEvent.ACTION_DOWN -> { downX = event.rawX; downY = event.rawY; startX = card.translationX; startY = card.translationY; dragging = false }
                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - downX; val dy = event.rawY - downY
                    if (!dragging && (abs(dx) > slop || abs(dy) > slop)) dragging = true
                    if (dragging) { card.translationX = startX + dx; card.translationY = startY + dy; keepInside() }
                }
                MotionEvent.ACTION_UP -> if (!dragging) {
                    large = !large
                    card.layoutParams = card.layoutParams.apply { width = cardWidth() }
                    card.post { keepInside() }
                }
            }
            true
        }
        card.contentDescription = "Camera preview. Drag to move, tap to resize."
        overlay = card
        root.addView(card, FrameLayout.LayoutParams(cardWidth(), -2, Gravity.TOP or Gravity.START))
        card.post {
            card.translationX = (root.width - card.width - dp(12)).toFloat()
            card.translationY = dp(12).toFloat()
            keepInside()
        }

        texture.surfaceTextureListener = object : TextureView.SurfaceTextureListener {
            override fun onSurfaceTextureAvailable(surface: SurfaceTexture, width: Int, height: Int) {
                if (!visible || overlay !== card || target?.current() != true || session.isEmpty()) return
                try { open(cameraId, surface, frame, label) }
                catch (_: Exception) { fail("Cannot open the camera. Check its permission and other camera apps.") }
            }
            override fun onSurfaceTextureSizeChanged(surface: SurfaceTexture, width: Int, height: Int) { }
            override fun onSurfaceTextureDestroyed(surface: SurfaceTexture): Boolean { if (overlay === card) stop(); return true }
            override fun onSurfaceTextureUpdated(surface: SurfaceTexture) { }
        }
    }

    // ---------------------------------------------------------------- capture

    private fun open(cameraId: Int, surface: SurfaceTexture, frame: AspectFrame, label: TextView) {
        val opened = Camera.open(cameraId)
        camera = opened
        val parameters = opened.parameters
        // Largest size up to 1920 × 1080, 16:9 first: a webcam picture without letterboxing.
        val size = parameters.supportedPreviewSizes.filter { it.width <= MAX_WIDTH && it.height <= MAX_HEIGHT }
            .maxWithOrNull(compareBy<Camera.Size>({ it.width * 9 == it.height * 16 }, { it.width * it.height }))
            ?: throw IllegalStateException("No supported preview size")
        parameters.setPreviewSize(size.width, size.height)
        parameters.previewFormat = ImageFormat.NV21
        // A range that reaches 30 fps; a fixed 30 if offered, else the one with the highest floor.
        parameters.supportedPreviewFpsRange?.filter { it[1] >= TARGET_FPS * 1000 && it[0] <= TARGET_FPS * 1000 }
            ?.maxWithOrNull(compareBy<IntArray>({ -it[1] }, { it[0] }))
            ?.let { parameters.setPreviewFpsRange(it[0], it[1]) }
        if (parameters.supportedFocusModes?.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO) == true)
            parameters.focusMode = Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
        // Video mode gives a steadier 30 fps on many phones; a few refuse it together with these settings.
        parameters.setRecordingHint(true)
        try { opened.parameters = parameters }
        catch (_: RuntimeException) { parameters.setRecordingHint(false); opened.parameters = parameters }

        val info = Camera.CameraInfo(); Camera.getCameraInfo(cameraId, info)
        val degrees = when (activity.windowManager.defaultDisplay.rotation) {
            Surface.ROTATION_90 -> 90; Surface.ROTATION_180 -> 180; Surface.ROTATION_270 -> 270; else -> 0
        }
        val front = info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT
        val angle = if (front) (info.orientation + degrees) % 360 else (info.orientation - degrees + 360) % 360
        val display = if (front) (360 - angle) % 360 else angle
        opened.setDisplayOrientation(display)
        frame.aspect = if (display % 180 == 0) size.width.toFloat() / size.height else size.height.toFloat() / size.width
        label.text = "● LIVE · ${minOf(size.width, size.height)}p"

        val bytes = size.width * size.height * ImageFormat.getBitsPerPixel(ImageFormat.NV21) / 8
        repeat(ENCODERS + 2) { opened.addCallbackBuffer(ByteArray(bytes)) }
        opened.setPreviewCallbackWithBuffer { data, cam -> onFrame(data, cam, size.width, size.height, angle) }
        opened.setErrorCallback { _, _ -> fail("Camera stopped") }
        opened.setPreviewTexture(surface)
        opened.startPreview()
    }

    /** Runs on the main thread. The camera's buffer goes to an encoder and comes back to the camera when encoded. */
    private fun onFrame(bytes: ByteArray?, cam: Camera, width: Int, height: Int, rotation: Int) {
        if (bytes == null) return
        val destination = target
        val key = session
        val now = SystemClock.elapsedRealtime()
        if (destination == null || key.isEmpty() || !visible || cam !== camera ||
            now - lastFrame < FRAME_INTERVAL_MS || encoding.get() >= ENCODERS) { if (cam === camera) cam.addCallbackBuffer(bytes); return }
        lastFrame = now
        encoding.incrementAndGet()
        val seq = ++captured
        val id = request
        val gen = generation
        try {
            encoders.execute {
                var jpeg: ByteArray? = null
                try {
                    val output = ByteArrayOutputStream(256 * 1024)
                    YuvImage(bytes, ImageFormat.NV21, width, height, null).compressToJpeg(Rect(0, 0, width, height), JPEG_QUALITY, output)
                    jpeg = output.toByteArray()
                } catch (_: Exception) { }
                finally {
                    encoding.decrementAndGet()
                    main.post { if (cam === camera) cam.addCallbackBuffer(bytes) }
                }
                if (jpeg != null && gen == generation) publish(Encoded(seq, gen, destination, id, key, jpeg, rotation))
            }
        } catch (_: Exception) { encoding.decrementAndGet(); cam.addCallbackBuffer(bytes) } // executor shut down
    }

    /** Keeps only the newest encoded frame; one uploader sends it, so a slow upload drops frames instead of queueing them. */
    private fun publish(frame: Encoded) {
        while (true) {
            val old = latest.get()
            if (old != null && old.seq >= frame.seq) return
            if (latest.compareAndSet(old, frame)) break
        }
        drain()
    }

    private fun drain() {
        if (!uploading.compareAndSet(false, true)) return
        try {
            uploader.execute {
                try {
                    while (true) {
                        val next = latest.getAndSet(null) ?: break
                        if (next.seq <= lastSent || next.gen != generation || !visible) continue
                        lastSent = next.seq
                        try {
                            if (!next.destination.current()) throw java.io.IOException("Camera destination changed")
                            next.destination.call("frame", next.id, next.key, next.jpeg, next.rotation)
                            main.post { if (generation == next.gen) lastAck = SystemClock.elapsedRealtime() }
                        } catch (_: Exception) {
                            main.post { if (generation == next.gen) fail("Camera connection ended") }
                            break
                        }
                    }
                } finally {
                    uploading.set(false)
                    if (latest.get() != null) drain()
                }
            }
        } catch (_: Exception) { uploading.set(false) } // executor shut down
    }

    private fun fail(message: String) { stop(); Toast.makeText(activity, message, Toast.LENGTH_LONG).show() }

    fun stop(sendEnd: Boolean = true) {
        ++generation
        val destination = target; val id = request; val key = session
        target = null; request = ""; session = ""
        latest.set(null)
        camera?.let { runCatching { it.setPreviewCallbackWithBuffer(null); it.stopPreview() }; runCatching { it.release() } }
        camera = null
        val previous = dialog; dialog = null; previous?.dismiss()
        val shown = overlay; overlay = null
        (shown?.parent as? ViewGroup)?.removeView(shown)
        destination?.cancel()
        if (sendEnd && destination != null && id.isNotEmpty() && !worker.isShutdown)
            worker.execute { runCatching { destination.call("end", id, key) } }
    }

    companion object {
        const val PERMISSION_REQUEST = 7201
        private const val MAX_WIDTH = 1920
        private const val MAX_HEIGHT = 1080
        private const val TARGET_FPS = 30
        private const val FRAME_INTERVAL_MS = 30L // just under 1000 / 30, so camera timing jitter does not drop frames
        private const val ENCODERS = 2
        private const val JPEG_QUALITY = 70
    }
}
