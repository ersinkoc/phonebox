package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.app.Dialog
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.graphics.Rect
import android.graphics.YuvImage
import android.hardware.Camera
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import android.widget.Toast
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/** Native consent and capture. No JavascriptInterface can approve or start a camera. */
@Suppress("DEPRECATION") // Camera's NV21 preview keeps the first version dependency-free on API 21+.
internal class PhoneCamera(private val activity: Activity, private val link: AgentLink) {
    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor()
    @Volatile private var visible = false
    @Volatile private var generation = 0
    private var dialog: Dialog? = null
    private var camera: Camera? = null
    private var target: AgentLink.CameraTarget? = null
    private var request = ""
    private var session = ""
    private var busy = false
    private val sending = AtomicBoolean(false)
    private var lastFrame = 0L
    private var lastAck = 0L

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
    fun destroy() { pause(); worker.shutdown() }

    private fun checkRequest() {
        val destination = link.cameraTarget() ?: return
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
            text = "Video only. This computer can record received video.\nCertificate: ${destination.pin.take(16)}…\nSharing stops when you leave PhoneBox or lock the phone."
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

    private fun showPreview(cameraId: Int) {
        val preview = SurfaceView(activity)
        val content = LinearLayout(activity).apply { orientation = LinearLayout.VERTICAL }
        content.addView(TextView(activity).apply {
            text = "LIVE → ${target?.name}\nVideo only · up to 720p / 10 fps"
            setPadding(20, 12, 20, 12)
        })
        content.addView(preview, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
        content.addView(Button(activity).apply {
            text = "Stop sharing"; filterTouchesWhenObscured = true; setOnClickListener { stop() }
        })
        val live = Dialog(activity).apply {
            setContentView(content)
            setOnCancelListener { stop() }
            window?.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
        dialog = live
        preview.holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(holder: SurfaceHolder) {
                if (!visible || dialog !== live || target?.current() != true || session.isEmpty()) return
                try {
                    val opened = Camera.open(cameraId)
                    camera = opened
                    val parameters = opened.parameters
                    val size = parameters.supportedPreviewSizes.filter { it.width <= 1280 && it.height <= 720 }
                        .maxByOrNull { it.width * it.height } ?: throw IllegalStateException("No supported preview size")
                    parameters.setPreviewSize(size.width, size.height)
                    parameters.previewFormat = ImageFormat.NV21
                    if (parameters.supportedFocusModes?.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO) == true)
                        parameters.focusMode = Camera.Parameters.FOCUS_MODE_CONTINUOUS_VIDEO
                    opened.parameters = parameters
                    val info = Camera.CameraInfo(); Camera.getCameraInfo(cameraId, info)
                    val degrees = when (activity.windowManager.defaultDisplay.rotation) {
                        Surface.ROTATION_90 -> 90; Surface.ROTATION_180 -> 180; Surface.ROTATION_270 -> 270; else -> 0
                    }
                    val front = info.facing == Camera.CameraInfo.CAMERA_FACING_FRONT
                    val angle = if (front) (info.orientation + degrees) % 360 else (info.orientation - degrees + 360) % 360
                    opened.setDisplayOrientation(if (front) (360 - angle) % 360 else angle)
                    opened.setPreviewDisplay(holder)
                    opened.setPreviewCallback { bytes, _ -> onFrame(bytes, size.width, size.height, angle) }
                    opened.setErrorCallback { _, _ -> fail("Camera stopped") }
                    opened.startPreview()
                } catch (_: Exception) { fail("Cannot open the camera. Check its permission and other camera apps.") }
            }
            override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) { }
            override fun surfaceDestroyed(holder: SurfaceHolder) { if (dialog === live) stop() }
        })
        live.show()
        live.window?.setLayout(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
    }

    private fun onFrame(bytes: ByteArray?, width: Int, height: Int, rotation: Int) {
        val destination = target ?: return
        val key = session
        val id = request
        val gen = generation
        val now = SystemClock.elapsedRealtime()
        if (bytes == null || key.isEmpty() || !visible || now - lastFrame < 100 || !sending.compareAndSet(false, true)) return
        lastFrame = now
        val copy = bytes.copyOf() // camera owns the callback buffer
        worker.execute {
            try {
                val output = ByteArrayOutputStream()
                YuvImage(copy, ImageFormat.NV21, width, height, null).compressToJpeg(Rect(0, 0, width, height), 65, output)
                if (visible && generation == gen && destination.current()) {
                    destination.call("frame", id, key, output.toByteArray(), rotation)
                    main.post { if (generation == gen) lastAck = SystemClock.elapsedRealtime() }
                }
            } catch (_: Exception) { main.post { if (generation == gen) fail("Camera connection ended") } }
            finally { sending.set(false) }
        }
    }

    private fun fail(message: String) { stop(); Toast.makeText(activity, message, Toast.LENGTH_LONG).show() }

    fun stop(sendEnd: Boolean = true) {
        ++generation
        val destination = target; val id = request; val key = session
        target = null; request = ""; session = ""
        camera?.let { runCatching { it.setPreviewCallback(null); it.stopPreview() }; runCatching { it.release() } }
        camera = null
        val previous = dialog; dialog = null; previous?.dismiss()
        destination?.cancel()
        if (sendEnd && destination != null && id.isNotEmpty() && !worker.isShutdown)
            worker.execute { runCatching { destination.call("end", id, key) } }
    }

    companion object { const val PERMISSION_REQUEST = 7201 }
}
