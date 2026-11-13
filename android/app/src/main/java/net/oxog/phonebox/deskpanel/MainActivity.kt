package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.Activity
import android.app.ActivityManager
import android.content.ComponentName
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.BatteryManager
import android.os.SystemClock
import android.os.Bundle
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.text.format.DateFormat
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.webkit.JavascriptInterface
import android.webkit.ConsoleMessage
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebSettings
import android.webkit.WebView
import android.webkit.WebChromeClient
import android.webkit.WebViewClient
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import org.json.JSONObject

/**
 * Full-screen desk panel. The UI is plain HTML/JS bundled in assets/www.
 * The UI calls "/agent/..." URLs; they are intercepted here and forwarded to the PhoneBox agent on the PC,
 * over USB (adb reverse) or, once paired, over Wi-Fi / Tailscale (see AgentLink). The WebView itself therefore
 * never makes local-network requests (no CORS, mixed-content or local-network-access issues).
 */
class MainActivity : Activity(), SensorEventListener {

    private lateinit var web: WebView
    private lateinit var link: AgentLink
    private lateinit var stream: AgentStream
    @Volatile private var volumeKeysFlipPages = true
    private val sensors by lazy { getSystemService(SensorManager::class.java) }
    private var lastLux = -1f
    private var lastLuxAt = 0L
    private var near: Boolean? = null

    private val assetLoader by lazy {
        WebViewAssetLoader.Builder()
            .addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(this))
            .build()
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        link = AgentLink(this)
        stream = AgentStream(link) { event, data ->
            val js = "window.onAgentStream && window.onAgentStream(${JSONObject.quote(event)}, ${JSONObject.quote(data)})"
            runOnUiThread { web.evaluateJavascript(js, null) }
        }

        web = WebView(this)
        web.setBackgroundColor(0xFF000000.toInt())
        setContentView(web)

        with(web.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            cacheMode = WebSettings.LOAD_NO_CACHE
            textZoom = 100
        }
        web.isVerticalScrollBarEnabled = false
        web.isHorizontalScrollBarEnabled = false
        web.webChromeClient = object : WebChromeClient() {
            override fun onConsoleMessage(message: ConsoleMessage): Boolean {
                if (message.messageLevel() == ConsoleMessage.MessageLevel.ERROR)
                    Log.e("PhoneBoxWeb", "${message.sourceId()}:${message.lineNumber()} ${message.message()}")
                return true
            }
        }
        web.webViewClient = object : WebViewClient() {
            override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse? {
                val url = request.url
                if (url.host == ASSET_HOST && url.path?.startsWith("/agent/") == true)
                    return if (fromPanel(request)) proxy(request) else FORBIDDEN()
                val r = assetLoader.shouldInterceptRequest(url)
                // the panel is made of ES modules, which need a JavaScript MIME type
                if (r != null && url.path?.endsWith(".js") == true) r.mimeType = "text/javascript"
                return r
            }

            // Old WebView without document-start scripts: hand the bridge key to the main frame only (evaluateJavascript
            // never runs in iframes), and only when it shows the panel. Bridge calls before it arrives get nothing.
            override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) = giveKey(view, url)
            override fun onPageFinished(view: WebView, url: String?) = giveKey(view, url)
        }
        if (documentStartKey)
            WebViewCompat.addDocumentStartJavaScript(web, "window.__phoneboxKey='$bridgeKey';", setOf("https://$ASSET_HOST"))
        web.addJavascriptInterface(Bridge(), "Android")
        // Lets you debug the panel from the computer via chrome://inspect.
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)

        web.loadUrl("https://$ASSET_HOST/assets/www/index.html")
        hideSystemUi()
    }

    /**
     * Only the panel itself may reach the agent. Every frame of the WebView comes through here, including the sandboxed
     * iframe of a Custom HTML widget, which is meant to talk to the panel through postMessage only. The panel sends
     * X-PhoneBox-Panel on its requests; a sandboxed frame cannot add it without a CORS preflight, which is refused.
     * Images (album art, process icons) cannot carry headers, but they are read-only GETs.
     */
    private fun fromPanel(req: WebResourceRequest): Boolean =
        req.requestHeaders.keys.any { it.equals(PANEL_HEADER, ignoreCase = true) } ||
            (req.method == "GET" && req.url.path in READ_ONLY_IMAGES)

    private fun proxy(req: WebResourceRequest): WebResourceResponse {
        val path = req.url.encodedPath!!.removePrefix("/agent")
        val query = req.url.encodedQuery?.let { "?$it" } ?: ""
        val r = link.request(req.method, path + query)
        val ct = r.contentType ?: "application/octet-stream"
        val mime = ct.substringBefore(';').trim()
        val charset = if (ct.contains("charset=")) ct.substringAfter("charset=").trim() else null
        return WebResourceResponse(mime, charset, r.code, if (r.code < 400) "OK" else "Error", NO_STORE, r.body)
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemUi()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemUi() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            window.setDecorFitsSystemWindows(false)
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or
                View.SYSTEM_UI_FLAG_LAYOUT_STABLE or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_FULLSCREEN
        }
    }

    /** Volume up / down flip to the previous / next page (unless turned off in the settings). */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val key = when (event.keyCode) {
            KeyEvent.KEYCODE_VOLUME_UP -> "prev"
            KeyEvent.KEYCODE_VOLUME_DOWN -> "next"
            else -> null
        }
        if (key == null || !volumeKeysFlipPages) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0)
            web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('$key')", null)
        return true
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // Kiosk behaviour: Back does not leave the app, it returns to the first page.
        web.evaluateJavascript("window.onAndroidBack && window.onAndroidBack()", null)
    }

    override fun onResume() {
        super.onResume()
        web.onResume()
        stream.start()
        hideSystemUi()
        sensors?.getDefaultSensor(Sensor.TYPE_LIGHT)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
        sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
    }

    override fun onPause() {
        sensors?.unregisterListener(this)
        stream.stop()
        web.onPause()
        super.onPause()
    }

    /**
     * Room light (lux) and proximity for the panel: brightness that follows the room, the night screen when the room
     * goes dark, and waking the night screen with a wave of the hand. Light is passed on when it changes noticeably.
     */
    override fun onSensorChanged(e: SensorEvent) {
        when (e.sensor.type) {
            Sensor.TYPE_LIGHT -> {
                val lux = e.values[0]
                val now = SystemClock.elapsedRealtime()
                val jump = lastLux < 0 || kotlin.math.abs(lux - lastLux) > maxOf(3f, lastLux * 0.15f)
                if (!jump && now - lastLuxAt < 10_000) return
                if (now - lastLuxAt < 700) return
                lastLux = lux; lastLuxAt = now
                web.evaluateJavascript("window.onAndroidSensor && window.onAndroidSensor('light', $lux)", null)
            }
            Sensor.TYPE_PROXIMITY -> {
                val isNear = e.values[0] < minOf(5f, e.sensor.maximumRange)
                if (isNear == near) return
                near = isNear
                web.evaluateJavascript("window.onAndroidSensor && window.onAndroidSensor('proximity', $isNear)", null)
            }
        }
    }

    override fun onAccuracyChanged(sensor: Sensor, accuracy: Int) {}

    /**
     * Key for the bridge: addJavascriptInterface puts window.Android into EVERY frame of the WebView, including the
     * sandboxed iframe of a Custom HTML widget. Each call therefore carries this key, which only frames of the panel's
     * own origin receive (a document-start script with an origin rule; the sandboxed iframe has an opaque origin).
     * Calls without it do nothing (fail closed). A WebView without document-start scripts gets the key through
     * evaluateJavascript, which only reaches the main frame (see giveKey).
     */
    private val bridgeKey = java.util.UUID.randomUUID().toString()
    private val documentStartKey by lazy { WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT) }

    private fun giveKey(view: WebView, url: String?) {
        if (!documentStartKey && url?.startsWith("https://$ASSET_HOST/") == true)
            view.evaluateJavascript("window.__phoneboxKey='$bridgeKey';", null)
    }

    inner class Bridge {
        private fun ok(key: String) = key == bridgeKey

        /** Screen brightness 0..1; a negative value returns control to the system setting. */
        @JavascriptInterface
        fun setBrightness(key: String, value: Float) {
            if (!ok(key)) return
            runOnUiThread {
                val lp = window.attributes
                lp.screenBrightness =
                    if (value < 0f) WindowManager.LayoutParams.BRIGHTNESS_OVERRIDE_NONE else value.coerceIn(0.01f, 1f)
                window.attributes = lp
            }
        }

        @JavascriptInterface
        fun haptic(key: String) {
            if (!ok(key)) return
            val v = getSystemService(Vibrator::class.java) ?: return
            if (v.hasVibrator()) v.vibrate(VibrationEffect.createOneShot(14, VibrationEffect.DEFAULT_AMPLITUDE))
        }

        /** Notification vibration; pattern is a "wait,vibrate,wait,vibrate..." list of milliseconds. */
        @JavascriptInterface
        fun buzz(key: String, pattern: String) {
            if (!ok(key)) return
            val v = getSystemService(Vibrator::class.java) ?: return
            val timings = pattern.split(',').mapNotNull { it.trim().toLongOrNull() }.toLongArray()
            if (v.hasVibrator() && timings.isNotEmpty()) v.vibrate(VibrationEffect.createWaveform(timings, -1))
        }

        @JavascriptInterface
        fun battery(key: String): String {
            if (!ok(key)) return "{}"
            val i = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            val level = i?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
            val scale = i?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
            val pluggedType = i?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
            val temp = (i?.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) ?: 0) / 10.0
            val source = when (pluggedType) {
                BatteryManager.BATTERY_PLUGGED_USB -> "usb"
                BatteryManager.BATTERY_PLUGGED_AC -> "ac"
                BatteryManager.BATTERY_PLUGGED_WIRELESS -> "wireless"
                else -> ""
            }
            // The phone's own view of its USB data link (a sticky system broadcast): lets the agent tell a cable that
            // only charges from a host that lost the phone.
            val usb = try {
                registerReceiver(null, IntentFilter("android.hardware.usb.action.USB_STATE"))?.let { u ->
                    JSONObject()
                        .put("connected", u.getBooleanExtra("connected", false))
                        .put("configured", u.getBooleanExtra("configured", false))
                        .put("adb", u.getBooleanExtra("adb", false))
                }
            } catch (_: Exception) { null }
            return JSONObject()
                .put("level", if (level >= 0) level * 100 / scale else -1)
                .put("plugged", pluggedType != 0)
                .put("temp", temp)
                .put("source", source)
                .put("usb", usb ?: JSONObject.NULL)
                .toString()
        }

        /** "usb", "wifi" or "none": how the last agent request was delivered. */
        @JavascriptInterface
        fun transport(key: String): String = if (ok(key)) link.route.name.lowercase() else ""

        /** Whether the volume keys flip pages (true) or change the phone's volume (false). */
        @JavascriptInterface
        fun setVolumeKeys(key: String, enabled: Boolean) { if (ok(key)) volumeKeysFlipPages = enabled }

        /** Whether the phone is set to 24-hour time in Android settings. */
        @JavascriptInterface
        fun is24Hour(key: String): Boolean = if (ok(key)) DateFormat.is24HourFormat(this@MainActivity) else true

        /** Which sensors the phone has: {"light": true, "proximity": true}. */
        @JavascriptInterface
        fun sensors(key: String): String = if (!ok(key)) "{}" else JSONObject()
            .put("light", sensors?.getDefaultSensor(Sensor.TYPE_LIGHT) != null)
            .put("proximity", sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY) != null)
            .toString()

        /**
         * Screen pinning (lock task without a device owner): Home and Recents stay on the panel until it is unpinned
         * (Back + Overview held together, or turning the setting off). Android asks once to confirm.
         */
        @JavascriptInterface
        fun setPinned(key: String, on: Boolean) {
            if (!ok(key)) return
            runOnUiThread {
                val pinned = getSystemService(ActivityManager::class.java).lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                try { if (on && !pinned) startLockTask() else if (!on && pinned) stopLockTask() } catch (_: Exception) {}
            }
        }

        /** The computers this phone is paired with (for switching over Wi-Fi). */
        @JavascriptInterface
        fun pcs(key: String): String = if (ok(key)) link.pcsJson() else "[]"

        /** Switches to another paired computer and reloads the panel from it. */
        @JavascriptInterface
        fun selectPc(key: String, id: String) {
            if (!ok(key) || !link.select(id)) return
            runOnUiThread { stream.stop(); stream.start(); web.reload() }
        }

        @JavascriptInterface
        fun forgetPc(key: String, id: String) { if (ok(key)) link.forget(id) }

        /** "landscape" (default), "portrait" or "auto" (follow the phone). */
        @JavascriptInterface
        fun setOrientation(key: String, mode: String) {
            if (!ok(key)) return
            runOnUiThread {
                val want = when (mode) {
                    "portrait" -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                    "auto" -> ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
                    else -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                }
                if (requestedOrientation != want) requestedOrientation = want
            }
        }

        /** Offers the panel as a home app (see HomeAlias in the manifest); the agent can then make it the default. */
        @JavascriptInterface
        fun setHomeScreen(key: String, on: Boolean) {
            if (!ok(key)) return
            val alias = ComponentName(this@MainActivity, "$packageName.HomeAlias")
            val want = if (on) PackageManager.COMPONENT_ENABLED_STATE_ENABLED else PackageManager.COMPONENT_ENABLED_STATE_DISABLED
            if (packageManager.getComponentEnabledSetting(alias) != want)
                packageManager.setComponentEnabledSetting(alias, want, PackageManager.DONT_KILL_APP)
        }

        @JavascriptInterface
        fun version(key: String): String = if (ok(key)) packageManager.getPackageInfo(packageName, 0).versionName ?: "" else ""
    }

    companion object {
        private const val ASSET_HOST = "appassets.androidplatform.net"
        private val NO_STORE = mapOf("Cache-Control" to "no-store")
        private const val PANEL_HEADER = "X-PhoneBox-Panel"
        private val READ_ONLY_IMAGES = setOf("/agent/api/art", "/agent/api/procs/icon")
        private fun FORBIDDEN() = WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", NO_STORE, java.io.ByteArrayInputStream(ByteArray(0)))
    }
}
