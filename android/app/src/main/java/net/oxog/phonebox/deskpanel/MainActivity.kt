package net.oxog.phonebox.deskpanel

import android.annotation.SuppressLint
import android.app.Activity
import android.app.ActivityManager
import android.content.ComponentName
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import android.os.PowerManager
import android.os.Looper
import android.os.Handler
import android.content.Context
import android.content.BroadcastReceiver
import android.app.KeyguardManager
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


    // --------------------------------------------------------------------------- power standby
    //
    // A desk panel fed by one computer should stop holding the screen once that computer is gone. The stock app
    // only ever adds FLAG_KEEP_SCREEN_ON in onCreate, so the panel stays lit forever - on a phone that is nothing
    // but a panel, that is a battery problem. Here the hold is released when the computer is judged absent and
    // retaken the moment it answers again, which lights the panel again without a touch. Android's own screen-off
    // timeout does the actual sleeping: a normal app cannot force the display off, it can only stop insisting
    // that it stays on.
    //
    // display.standby = "off" | "power" | "data", where off is exactly today's behaviour and the default, so no
    // existing desk changes. "power" watches the USB charge state; "data" watches whether the agent still
    // answers, which is the criterion that survives two common setups: a port whose charge is capped at the
    // PMIC (a charging ceiling) while it still powers and syncs the phone, and a board that keeps 5 V alive
    // after shutdown (ErP off) - in the first case the panel would sleep while the computer runs, in the
    // second it never sleeps at all. The USB_STATE edge never gates the sleep (measured: some devices never
    // roll it back when their port loses power); it only speeds the wake, about a second after the host
    // kernel enumerates the phone, beside the agent's own KEYCODE_WAKEUP on reconnect.

    @Volatile private var standbyMode = "off"
    @Volatile private var standbyGraceMs = 30_000L
    @Volatile private var standbyAgentDataAt = 0L   // elapsedRealtime of the agent's last answer, either transport
    @Volatile private var standbyUsbLink = false    // an enumerated USB data link proves that computer is running
    @Volatile private var standbyPlugged = true     // USB charge state: the "power" criterion, and probe timing
    @Volatile private var standbyHostAlive = true   // what standby acts on; reconciled by standbyTick
    @Volatile private var standbyFromAgent = false  // have we read display.standby from the agent yet?
    @Volatile private var standbyLostSince = 0L     // elapsedRealtime of the first absence; 0 while alive
    @Volatile private var standbyDestroyed = false  // onDestroy ran: late callbacks must not reschedule or probe
    private val standbyHealthPollMs = 2_000L        // while the computer answers
    private val standbyPollMs = 15_000L             // once it went quiet, until it comes back
    private val standbySilenceMs = 12_000L          // this long with no answer at all => not reachable
    private val standbyHandler = Handler(Looper.getMainLooper())
    private val standbyProbes = Executors.newSingleThreadExecutor()
    private var standbyPowerReceiver: BroadcastReceiver? = null
    private var standbyUsbReceiver: BroadcastReceiver? = null
    private var standbyScreenReceiver: BroadcastReceiver? = null

    /** The panel may hold the display on while it believes the computer is running, or while standby is off. */
    private fun standbyApplyKeepAwake() {
        if (standbyMode == "off" || standbyHostAlive) window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        else window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        // The new window attributes have to be pushed at WindowManager again: a static page never relayouts on its
        // own, so after clearFlags the old keep-awake wakelock would linger and the screen would never sleep
        // (measured on Android 11). One layout request is enough - the traversal follows.
        window.decorView.requestLayout()
    }

    /** Take the criterion from whoever knows it: the panel pushes it when the setting changes, and the app reads
     *  it from the agent itself after a cold start (see standbyLoadSetting). Same entry point either way. */
    private fun applyStandbySetting(mode: String, graceSec: Int) {
        if (standbyDestroyed) return
        val m = when (mode.trim().lowercase()) { "power" -> "power"; "data" -> "data"; else -> "off" }
        val g = graceSec.coerceIn(3, 600)
        if (m == standbyMode && g * 1000L == standbyGraceMs) return
        standbyMode = m
        standbyGraceMs = g * 1000L
        standbyFromAgent = true
        Log.i("PhoneBox", "power standby: mode=$standbyMode grace=${g}s")
        // Both callers are background threads - the settings-read executor at cold start and the JS bridge
        // when the panel pushes display settings - and everything from here on touches the window, so hop
        // to the main looper before anything View-related (measured: CalledFromWrongThreadException).
        standbyHandler.post {
            standbyHostAlive = true
            standbyLostSince = 0L
            standbyApplyKeepAwake()
            standbyHandler.removeCallbacks(standbyTick)
            standbyHandler.post(standbyTick)
        }
    }

    /** A charged-to-100 % phone can report EXTRA_PLUGGED=0 while EXTRA_STATUS still says CHARGING/FULL, so the
     *  status counts as "cable in" too; otherwise a fully charged panel would sleep during normal use. */
    private fun standbyPluggedFrom(sticky: Intent?): Boolean {
        val st = sticky?.getIntExtra(BatteryManager.EXTRA_STATUS, BatteryManager.BATTERY_STATUS_UNKNOWN)
            ?: BatteryManager.BATTERY_STATUS_UNKNOWN
        return (sticky?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0) != 0 ||
            st == BatteryManager.BATTERY_STATUS_CHARGING || st == BatteryManager.BATTERY_STATUS_FULL
    }

    /** The phone's own view of its USB data link: an enumerated (configured) link means that host is running,
     *  worth knowing even when the Wi-Fi pairing is stale. Sticky, so reading it costs one binder call. */
    private fun standbyReadUsbLink(): Boolean = try {
        val u = registerReceiver(null, IntentFilter("android.hardware.usb.action.USB_STATE"))
        u != null && (u.getBooleanExtra("configured", false) || u.getBooleanExtra("connected", false))
    } catch (e: Exception) {
        false
    }

    /**
     * Both criteria ride sticky broadcasts, which a context-registered receiver is delivered and which can also be
     * read on demand to settle the initial state (a reboot into an unpowered hub must not light the panel). The
     * USB_STATE edge matters more than it looks: it fires as soon as the host kernel enumerates the device, about
     * a second after boot and long before any service is up, so the panel lights on the event instead of waiting
     * for the next probe.
     */
    private fun standbyRegisterReceivers() {
        val battery = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                val now = standbyPluggedFrom(intent)
                if (now == standbyPlugged) return
                standbyPlugged = now
                Log.i("PhoneBox", "power standby: USB charge -> $now")
                standbyHandler.removeCallbacks(standbyTick)
                standbyHandler.post(standbyTick)     // an edge is worth an immediate look, either way round
            }
        }
        val usb = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                val up = intent?.getBooleanExtra("configured", false) == true ||
                    intent?.getBooleanExtra("connected", false) == true
                if (up == standbyUsbLink) return
                standbyUsbLink = up
                Log.i("PhoneBox", "power standby: USB data link -> $up")
                standbyHandler.removeCallbacks(standbyTick)
                standbyHandler.post(standbyTick)
            }
        }
        val screen = object : BroadcastReceiver() {
            override fun onReceive(context: Context?, intent: Intent?) {
                if (standbyMode == "off") return
                // The display just lit - wake key, touch, charger, whatever. A swipe keyguard left standing
                // would hide a panel that already considers the computer up, and on at least one ROM the
                // activity is not paused while the screen sleeps, so this broadcast, not onResume, is the
                // reliable moment. Take the lock screen and look at the criterion right now.
                try {
                    (getSystemService(KEYGUARD_SERVICE) as? KeyguardManager)?.requestDismissKeyguard(this@MainActivity, null)
                } catch (e: Exception) {
                    Unit
                }
                standbyHandler.removeCallbacks(standbyTick)
                standbyHandler.post(standbyTick)
            }
        }
        standbyScreenReceiver = screen
        standbyPowerReceiver = battery
        standbyUsbReceiver = usb
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(battery, IntentFilter(Intent.ACTION_BATTERY_CHANGED), Context.RECEIVER_NOT_EXPORTED)
            registerReceiver(usb, IntentFilter("android.hardware.usb.action.USB_STATE"), Context.RECEIVER_NOT_EXPORTED)
            registerReceiver(screen, IntentFilter(Intent.ACTION_SCREEN_ON), Context.RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(battery, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            registerReceiver(usb, IntentFilter("android.hardware.usb.action.USB_STATE"))
            registerReceiver(screen, IntentFilter(Intent.ACTION_SCREEN_ON))
        }
        standbyPlugged = standbyPluggedFrom(registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)))
        standbyUsbLink = standbyReadUsbLink()
    }

    /**
     * Read our own setting from the agent once. The panel pushes display settings only on change, so a cold app
     * start would otherwise never hear about it; and after a host reboot the panel's own polls are exactly what
     * cannot run yet. A reply also proves the computer is up, so it feeds the criterion at the same time.
     */
    private fun standbyLoadSetting() {
        standbyExecute {
            try {
                val r = link.request("GET", "/api/config")
                if (r.code >= 400) { r.body.close(); return@standbyExecute }
                val body = r.body.reader().use { it.readText() }
                val d = org.json.JSONObject(body).optJSONObject("config")?.optJSONObject("display") ?: return@standbyExecute
                standbyAgentDataAt = SystemClock.elapsedRealtime()
                standbyFromAgent = true
                applyStandbySetting(d.optString("standby", "off"), d.optInt("standbyGraceSec", 30))
            } catch (e: Exception) {
                Log.w("PhoneBox", "power standby: setting read failed: " + e)
            }
        }
    }

    /**
     * Decide, on a clock that cannot jump (elapsedRealtime survives the re-syncs a phone does after boot): this
     * long without any answer means the computer is not reachable, so release the
     * screen - but only after the grace period, which is what keeps a host reboot or a Wi-Fi hiccup from blacking
     * the panel. Coming back online retakes the hold and wakes the panel by itself.
     */
    private fun standbyReconcile() {
        val absent = when (standbyMode) {
            "power" -> !standbyPlugged
            // measured: some devices never roll USB_STATE/battery back after their hub port loses VBUS (3T on
            // Lineage 18.1 kept reporting connected=true, configured=true and USB powered=true for a full
            // minute), so the data criterion must not wait for the link to drop. The link only ever speeds the
            // wake up - the enumeration edge lights the panel in about a second instead of on the next poll.
            "data" -> SystemClock.elapsedRealtime() - standbyAgentDataAt >= standbySilenceMs
            else -> false
        }
        if (!absent) {
            standbyLostSince = 0L
            if (!standbyHostAlive) {
                standbyHostAlive = true
                Log.i("PhoneBox", "power standby: the computer is back -> holding the screen")
                standbyApplyKeepAwake()
                standbyWakePanel()
            }
            return
        }
        if (standbyLostSince == 0L) {
            standbyLostSince = SystemClock.elapsedRealtime()
            return
        }
        if (standbyHostAlive && SystemClock.elapsedRealtime() - standbyLostSince >= standbyGraceMs) {
            standbyHostAlive = false
            Log.i("PhoneBox", "power standby: $standbyMode says the computer is gone -> releasing the screen")
            standbyApplyKeepAwake()   // releases the hold; the system timeout then sleeps the display
        }
    }

    /** Watch the computer: fast while it answers, slower once it has gone quiet. */
    private val standbyTick = object : Runnable {
        override fun run() {
            if (standbyDestroyed) return
            if (!standbyFromAgent) standbyLoadSetting()
            if (standbyMode != "off") {
                standbyUsbLink = standbyReadUsbLink()
                standbyReconcile()
                if (!standbyHostAlive) standbyProbe()   // the page's own polls stop while the WebView idles; this does not
            }
            standbyHandler.postDelayed(this, if (standbyHostAlive) standbyHealthPollMs else standbyPollMs)
        }
    }

    /** Proof that the computer answers: stamp the clock and, when the verdict keys off data (mode "data"),
     *  do not wait for the next fallback tick either - reconcile right away, so the keyguard that the
     *  agent's KEYCODE_WAKEUP left standing is dismissed within a second instead of within standbyPollMs.
     *  Mode "power" keys off the charge state: an answer proves nothing there, and posting anyway
     *  would feed a probe-tick-probe hot loop whenever the agent answers over Wi-Fi with the cable out. */
    private fun markHostAnswer() {
        if (standbyDestroyed) return
        standbyAgentDataAt = SystemClock.elapsedRealtime()
        if (!standbyHostAlive && standbyMode == "data") {
            standbyHandler.removeCallbacks(standbyTick)
            standbyHandler.post(standbyTick)
        }
    }

    /** Fire-and-forget submission for the standby executor: a teardown may land between the destroyed
     *  check and execute(), and the RejectedExecutionException must not reach a handler thread. */
    private fun standbyExecute(task: Runnable) {
        if (standbyDestroyed) return
        try {
            standbyProbes.execute(task)
        } catch (e: RejectedExecutionException) {
            Unit
        }
    }

    /** Ask the agent directly, off the UI thread. The page's own polls stop while the WebView idles; this does
     *  not need a CPU wakelock to keep going: holding one would take WAKE_LOCK, a permission this feature
     *  deliberately does not add (upstream ships without it), so while the device idles these probes simply
     *  land in the doze maintenance windows - good enough as a fallback, because the prompt path is the agent
     *  itself, which sends KEYCODE_WAKEUP when it re-establishes the adb reverse rule after a reconnect. */
    private fun standbyProbe() {
        standbyExecute {
            try {
                val reply = link.request("GET", "/api/state")
                reply.body.use { }  // an unread, unclosed body strands the HttpURLConnection
                if (reply.code < 400) markHostAnswer()
            } catch (e: Exception) {
                Unit
            }
        }
    }

    /** Put the panel back in front and take whatever swipe keyguard sits on top of it; harmless when it is
     *  already showing. The prompt display wake belongs to the agent (KEYCODE_WAKEUP when the reverse rule is
     *  re-established after the computer returns) and to the plug-in itself. Lighting the display from here
     *  would need WAKE_LOCK, which this feature deliberately does not add - so the attempt is best effort:
     *  without the permission acquire() throws, we log it and carry on, the reorder and keyguard work below
     *  still run, and with the screen already lit they do their whole job. */
    private fun standbyWakePanel() {
        val pm = getSystemService(PowerManager::class.java)
        try {
            @Suppress("DEPRECATION")
            if (pm != null && !pm.isInteractive)
                pm.newWakeLock(
                    PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP,
                    "PhoneBox:standby",
                ).acquire(3_000L)
        } catch (e: SecurityException) {
            Log.i("PhoneBox", "power standby: no WAKE_LOCK, the agent lights the screen instead")
        }
        startActivity(Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP))
        // A swipe-to-unlock keyguard (the default on several ROMs, isSecure=false) would otherwise keep the woken
        // panel behind a lock screen nobody is standing there to swipe. DISABLE_KEYGUARD is a normal permission.
        try {
            (getSystemService(KEYGUARD_SERVICE) as? KeyguardManager)?.requestDismissKeyguard(this, null)
        } catch (e: Exception) {
            Log.w("PhoneBox", "keyguard dismiss failed: " + e)
        }
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        standbyHandler.postDelayed(standbyTick, standbyHealthPollMs)
        standbyRegisterReceivers()      // sticky reads settle the state; standby stays off until the agent says otherwise
        link = AgentLink(this)
        stream = AgentStream(link) { event, data ->
            markHostAnswer()   // a beat on the live stream is proof of life too
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
        if (r.code < 400) markHostAnswer()
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
        standbyHandler.removeCallbacks(standbyTick)
        standbyHandler.post(standbyTick)   // the panel is in front again: look now
        // A swipe keyguard can still be sitting in front of us when the display lights for a reason other
        // than our own recovery: the panel may have re-declared the computer alive from inside a doze
        // maintenance window (measured 11-13 14:17:30), so a wake key that arrives later brings up a lock
        // screen no hostAlive transition would ever dismiss. Asking on every resume is free - the call is
        // a no-op while there is no keyguard, and an app cannot dismiss a secure (PIN) one anyway.
        if (standbyMode != "off") try {
            (getSystemService(KEYGUARD_SERVICE) as? KeyguardManager)?.requestDismissKeyguard(this, null)
        } catch (e: Exception) {
            Unit
        }
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

    override fun onDestroy() {
        standbyDestroyed = true
        standbyHandler.removeCallbacks(standbyTick)
        standbyPowerReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyUsbReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyScreenReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyProbes.shutdownNow()
        super.onDestroy()
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
        fun setStandby(key: String, mode: String, graceSec: Int) {
            if (!ok(key)) return
            applyStandbySetting(mode, graceSec)
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
