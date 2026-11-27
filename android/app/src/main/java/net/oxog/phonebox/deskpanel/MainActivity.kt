package net.oxog.phonebox.deskpanel
import android.annotation.SuppressLint
import android.app.Activity
import android.app.ActivityManager
import android.app.AlertDialog
import android.content.ComponentName
import android.content.Context
import android.content.BroadcastReceiver
import android.app.KeyguardManager
import android.content.Intent
import android.content.IntentFilter
import android.content.res.Configuration
import android.content.pm.ActivityInfo
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.Handler
import android.os.Looper
import android.os.BatteryManager
import android.os.SystemClock
import android.os.Bundle
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.text.format.DateFormat
import android.util.Log
import android.view.Gravity
import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.TextView
import android.widget.Toast
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
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import java.util.Locale

/**
 * Full-screen desk panel. The UI is plain HTML/JS bundled in assets/www.
 * The UI calls "/agent/..." URLs; they are intercepted here and forwarded to the PhoneBox agent on the PC,
 * over USB (adb reverse) or, once paired, over Wi-Fi / Tailscale (see AgentLink). The WebView itself therefore
 * never makes local-network requests for the panel (no CORS, mixed-content or local-network-access issues); only a web
 * page that a Custom HTML widget shows by address loads from the network, in its own frame and origin.
 */
class MainActivity : Activity(), SensorEventListener {

    private lateinit var web: WebView
    /** False when Android could not start its WebView (#385): the activity then only shows why. */
    private val hasWeb get() = ::web.isInitialized
    private val panelLanguageTag = Regex("auto|[a-z]{2}(-[A-Za-z]{2,4})?")
    private val panelPreferences by lazy { getSharedPreferences("panel-ui", MODE_PRIVATE) }

    private fun pairingContext(): Context {
        val tag = panelPreferences.getString("language", null) ?: return this
        return createConfigurationContext(Configuration(resources.configuration).apply {
            setLocale(Locale.forLanguageTag(tag))
        })
    }
    // Set first in onDestroy: dictation and stream callbacks already queued on the UI thread then skip the destroyed WebView.
    private var destroyed = false
    private val dictation by lazy { Dictation(this) { script -> if (!destroyed) web.evaluateJavascript(script, null) } }
    private lateinit var link: AgentLink
    private lateinit var stream: AgentStream
    @Volatile private var resumed = false
    @Volatile private var volumeKeysFlipPages = true
    private val sensors by lazy { getSystemService(Context.SENSOR_SERVICE) as SensorManager? }
    private var lastLux = -1f
    private var lastLuxAt = 0L
    private var near: Boolean? = null

    private val assetLoader by lazy {
        WebViewAssetLoader.Builder()
            // The panel is served over http: a web page shown by address (Custom HTML widget) may be plain http on the
            // home network, and inside an https panel WebView would block its images as mixed content. The panel never
            // goes to the network (the asset loader answers), uses no API that needs a secure context, and the agent
            // calls are still guarded by the key only the panel receives.
            .setHttpAllowed(true)
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
    @Volatile private var standbyAgentWakeAt = 0L   // elapsedRealtime of the last panel start the agent marked as its standby wake; 0 = none
    private val standbyAgentWakeWindowMs = 15_000L  // how long such a mark lets this app dismiss the keyguard
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

    /** Take the criterion from whoever knows it: the panel pushes it when the setting changes */
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

            if (standbyMode == "off") {
                // Standby is disabled: no timer and no standby receivers.
                standbyHandler.removeCallbacks(standbyTick)
                standbyUnregisterReceivers()
            } else {
                // Standby is enabled: start monitoring.
                standbyStartMonitoring()
            }
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
        if (standbyPowerReceiver != null ||
            standbyUsbReceiver != null ||
            standbyScreenReceiver != null) return
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
                // The display went dark: the agent's wake is over, so the next screen-on must not
                // dismiss a swipe lock on its account.
                if (intent?.action == Intent.ACTION_SCREEN_OFF) { standbyAgentWakeAt = 0L; return }
                if (standbyMode == "off") return
                standbyDismissKeyguard()
                standbyHandler.removeCallbacks(standbyTick)
                standbyHandler.post(standbyTick)
            }
        }
        standbyScreenReceiver = screen
        standbyPowerReceiver = battery
        standbyUsbReceiver = usb
        // SCREEN_OFF ends the agent's wake window (see onReceive above), so the receiver needs both actions.
        val screenFilter = IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_SCREEN_OFF)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(battery, IntentFilter(Intent.ACTION_BATTERY_CHANGED), Context.RECEIVER_NOT_EXPORTED)
            registerReceiver(usb, IntentFilter("android.hardware.usb.action.USB_STATE"), Context.RECEIVER_NOT_EXPORTED)
            registerReceiver(screen, screenFilter, Context.RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(battery, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
            registerReceiver(usb, IntentFilter("android.hardware.usb.action.USB_STATE"))
            registerReceiver(screen, screenFilter)
        }
        standbyPlugged = standbyPluggedFrom(registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)))
        standbyUsbLink = standbyReadUsbLink()
    }

    private fun standbyUnregisterReceivers() {
        standbyPowerReceiver?.let {
            runCatching { unregisterReceiver(it) }
        }
        standbyUsbReceiver?.let {
            runCatching { unregisterReceiver(it) }
        }
        standbyScreenReceiver?.let {
            runCatching { unregisterReceiver(it) }
        }

        standbyPowerReceiver = null
        standbyUsbReceiver = null
        standbyScreenReceiver = null
    }

    private fun standbyStartMonitoring() {
        if (standbyDestroyed || standbyMode == "off") return

        standbyRegisterReceivers()

        standbyHandler.removeCallbacks(standbyTick)
        standbyHandler.post(standbyTick)
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
            if (standbyDestroyed || standbyMode == "off") return

            standbyUsbLink = standbyReadUsbLink()
            standbyReconcile()

            if (!standbyHostAlive) standbyProbe()

            standbyHandler.postDelayed(
                this,
                if (standbyHostAlive) standbyHealthPollMs else standbyPollMs
            )
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

    /** The agent starts the panel with agentWake=true right after the wake key and wm dismiss-keyguard it sends
     *  when the computer is back (AdbReverse.LightPanel; standby on, named phone only). That mark is the only
     *  thing that lets this app touch the keyguard: no mark, no dismissal. */
    private fun standbyNoteAgentWake(i: Intent?) {
        if (i?.getBooleanExtra("agentWake", false) != true) return
        standbyAgentWakeAt = SystemClock.elapsedRealtime()
        standbyDismissKeyguard()   // the display is usually lit already, so no screen-on edge will follow
    }

    private fun standbyMayDismissKeyguard(): Boolean {
        val at = standbyAgentWakeAt
        return standbyMode != "off" && standbyHostAlive && at != 0L &&
            SystemClock.elapsedRealtime() - at < standbyAgentWakeWindowMs
    }

    /** Every keyguard dismissal in this file goes through here. The mark is not used up on purpose: a dismissal
     *  while the display is still off does not take, so the next screen-on or resume inside the window may try
     *  again. A PIN or pattern lock is never dismissed by this - Android still asks. */
    private fun standbyDismissKeyguard() {
        if (!standbyMayDismissKeyguard()) return
        // requestDismissKeyguard needs API 26; below that the agent's own wm dismiss-keyguard is all there is
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        try {
            (getSystemService(KEYGUARD_SERVICE) as? KeyguardManager)?.requestDismissKeyguard(this, null)
        } catch (e: Exception) {
            Log.w("PhoneBox", "keyguard dismiss failed: " + e)
        }
    }


    /** Put the panel back in front and take whatever swipe keyguard sits on top of it; harmless when it is
    *  already showing. Lighting the display belongs to the agent (KEYCODE_WAKEUP when the reverse rule is
    *  re-established after the computer returns) and to the plug-in itself; this app holds no WAKE_LOCK. */
    private fun standbyWakePanel() {
        startActivity(Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP))
        // A swipe-to-unlock keyguard would otherwise keep the woken panel behind a lock screen nobody is
        // standing there to swipe - but only after a wake the agent started.
        standbyDismissKeyguard()
    }

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        link = AgentLink(this)
        stream = AgentStream(link) { event, data ->
            markHostAnswer()   // a beat on the live stream is proof of life too
            val js = "window.onAgentStream && window.onAgentStream(${JSONObject.quote(event)}, ${JSONObject.quote(data)})"
            runOnUiThread { if (!destroyed) web.evaluateJavascript(js, null) }
        }

        web = try { WebView(this) } catch (e: Exception) {
            // Some Android 5.1 vendor systems cannot start any WebView update ("Invalid reflection", #385): say so
            // instead of crashing at every launch.
            Log.e("PhoneBox", "WebView failed to start", e)
            setContentView(TextView(this).apply {
                text = getString(R.string.webview_failed)
                setTextColor(0xFFFFFFFF.toInt())
                setBackgroundColor(0xFF000000.toInt())
                textSize = 20f
                gravity = Gravity.CENTER
                setPadding(48, 48, 48, 48)
            })
            return
        }
        web.setBackgroundColor(0xFF000000.toInt())
        setContentView(web)

        with(web.settings) {
            javaScriptEnabled = true
            domStorageEnabled = true
            mediaPlaybackRequiresUserGesture = false
            cacheMode = WebSettings.LOAD_NO_CACHE
            textZoom = 100
            // the panel is http, so a plain http page in a frame is not mixed content; this only matters for an
            // https page that loads http parts
            mixedContentMode = WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE
        }
        web.isVerticalScrollBarEnabled = false
        web.isHorizontalScrollBarEnabled = false
        web.isHapticFeedbackEnabled = false
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
                // A panel page only as the app's own page, never inside a frame: a widget's page (allow-same-origin)
                // could otherwise open a copy of the panel in a frame of its own, which the asset loader answers
                // without the network, and that copy would run in the panel's origin.
                if (r != null && !request.isForMainFrame && r.mimeType in DOCUMENT_TYPES) return FORBIDDEN()
                // The panel's own host never goes to the network: over plain http someone on the network could answer
                // it, and such a page would run in the panel's origin (with the bridge key).
                if (r == null && url.host == ASSET_HOST) return FORBIDDEN()
                return r
            }

            // Old WebView without document-start scripts: hand the bridge key to the main frame only (evaluateJavascript
            // never runs in iframes), and only when it shows the panel. Bridge calls before it arrives get nothing.
            override fun onPageStarted(view: WebView, url: String?, favicon: android.graphics.Bitmap?) = giveKey(view, url)
            override fun onPageFinished(view: WebView, url: String?) {
                if (storageMove != 0) moveStorageStep(view, url) else giveKey(view, url)
            }
        }
        if (documentStartKey)
            WebViewCompat.addDocumentStartJavaScript(web, panelBootstrap(), setOf(PANEL_ORIGIN))
        web.addJavascriptInterface(Bridge(), "Android")
        // Lets you debug the panel from the computer via chrome://inspect.
        WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)

        if (panelPreferences.getBoolean(STORAGE_MOVED, false)) loadPanel() else moveStorage()
        hideSystemUi()
        if (savedInstanceState == null) standbyNoteAgentWake(intent)   // not again after a configuration change
        handlePairingLink(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        standbyNoteAgentWake(intent)
        handlePairingLink(intent)
    }

    /** A pairing link from the computer's QR code (Settings › Wi-Fi › Pair a phone over Wi-Fi): ask, then pair. */
    private fun handlePairingLink(intent: Intent?) {
        val pairing = AgentLink.PairLink.parse(intent?.data) ?: return
        intent?.data = null // not again after a configuration change
        val context = pairingContext()
        AlertDialog.Builder(this)
            .setTitle(context.getString(R.string.pair_title))
            .setMessage(context.getString(R.string.pair_message, pairing.pc, pairing.hosts.joinToString(), pairing.port, pairing.check))
            .setPositiveButton(context.getString(R.string.pair_positive)) { _, _ -> pair(pairing, context) }
            .setNegativeButton(context.getString(R.string.pair_negative), null)
            .setOnDismissListener { hideSystemUi() }
            .show()
            .pairingButtons(context)
    }

    /** Dialog buttons as written: the default all-caps follows the phone's language ("PAİR" on a Turkish phone). */
    private fun AlertDialog.pairingButtons(context: Context): AlertDialog = apply {
        window?.decorView?.layoutDirection = context.resources.configuration.layoutDirection
        listOf(AlertDialog.BUTTON_POSITIVE, AlertDialog.BUTTON_NEGATIVE).forEach { getButton(it)?.isAllCaps = false }
    }

    private fun pair(pairing: AgentLink.PairLink, context: Context) {
        Toast.makeText(context, context.getString(R.string.pair_progress, pairing.pc), Toast.LENGTH_SHORT).show()
        Thread {
            val problem = link.claim(pairing)
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread // the pairing is stored; there is no window to show it in
                if (problem == null) {
                    Toast.makeText(context, context.getString(R.string.pair_done, pairing.pc), Toast.LENGTH_LONG).show()
                    stream.stop(); if (resumed) stream.start(); if (hasWeb) web.reload()
                } else {
                    AlertDialog.Builder(this).setTitle(context.getString(R.string.pair_failed_title)).setMessage(problem.message(context))
                        .setPositiveButton(context.getString(R.string.ok), null).setOnDismissListener { hideSystemUi() }.show().pairingButtons(context)
                }
            }
        }.start()
    }

    /**
     * Only the panel itself may reach the agent. Every frame of the WebView comes through here, including the sandboxed
     * iframe of a Custom HTML widget, which is meant to talk to the panel through postMessage only. The panel sends
     * X-PhoneBox-Panel on its requests; a sandboxed frame cannot add it without a CORS preflight, which is refused.
     * Images (album art, shortcut icons) cannot carry headers, but they are read-only GETs. Process icons are fetched
     * with the header, since whether one loads tells which programs run on the computer.
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
        val status = if (r.code in 100..599 && r.code !in 300..399) r.code else 502
        // The agent's own protection headers go along: a frame can open an image route as a document, and an uploaded
        // SVG icon would then run its script in the panel's origin without the CSP sandbox. The image routes need no
        // panel header, so they get a sandbox even if the agent sends none.
        val headers = NO_STORE.toMutableMap()
        r.headers.forEach { (name, value) -> if (name.lowercase() in PASSED_HEADERS) headers[name] = value }
        if (req.url.path in READ_ONLY_IMAGES) {
            if (headers.keys.none { it.equals("Content-Security-Policy", ignoreCase = true) })
                headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; sandbox"
            if (headers.keys.none { it.equals("X-Content-Type-Options", ignoreCase = true) })
                headers["X-Content-Type-Options"] = "nosniff"
        }
        return WebResourceResponse(mime, charset, status, if (status < 400) "OK" else "Error", headers, r.body)
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

    /**
     * The panel is made for touch, so a press of the main mouse button is handed to the page as a finger: a click taps,
     * a drag swipes between pages, a long press holds. A mouse shared from the computer (Input Leaf, Deskflow) sends its
     * presses as ACTION_DOWN / ACTION_UP only, without the ACTION_BUTTON_PRESS / RELEASE the WebView makes mouse clicks
     * from, so the page saw nothing at all (#425). The right and middle buttons, hovering and the wheel stay mouse events.
     */
    private var mouseAsFinger = false

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (Build.VERSION.SDK_INT < 23 || event.getToolType(0) != MotionEvent.TOOL_TYPE_MOUSE)
            return super.dispatchTouchEvent(event)
        if (event.actionMasked == MotionEvent.ACTION_DOWN)
            mouseAsFinger = event.buttonState and (MotionEvent.BUTTON_SECONDARY or MotionEvent.BUTTON_TERTIARY) == 0
        if (!mouseAsFinger) return super.dispatchTouchEvent(event)
        try {
            return sendAsFinger(event, event.action)
        } finally {
            if (event.actionMasked == MotionEvent.ACTION_UP || event.actionMasked == MotionEvent.ACTION_CANCEL) mouseAsFinger = false
        }
    }

    /** The mouse event as a finger on the touch screen, with [action], handed to the page. */
    private fun sendAsFinger(event: MotionEvent, action: Int): Boolean {
        val n = event.pointerCount
        val props = Array(n) { i ->
            MotionEvent.PointerProperties().also { event.getPointerProperties(i, it); it.toolType = MotionEvent.TOOL_TYPE_FINGER }
        }
        val coords = Array(n) { i -> MotionEvent.PointerCoords().also { event.getPointerCoords(i, it) } }
        val finger = MotionEvent.obtain(event.downTime, event.eventTime, action, n, props, coords, event.metaState, 0,
            event.xPrecision, event.yPrecision, event.deviceId, event.edgeFlags, InputDevice.SOURCE_TOUCHSCREEN, event.flags)
        try {
            return super.dispatchTouchEvent(finger)
        } finally {
            finger.recycle()
        }
    }

    /**
     * A real mouse also reports its main button as ACTION_BUTTON_PRESS / RELEASE. While its press goes in as a finger
     * (above), those are dropped; released while another button is still held, the finger is lifted there, since no
     * ACTION_UP comes until every button is up. A main button pressed while another one was already held was not
     * turned into a finger, so its button events go to the page as they are.
     */
    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (Build.VERSION.SDK_INT >= 23 && mouseAsFinger && event.getToolType(0) == MotionEvent.TOOL_TYPE_MOUSE &&
            (event.actionMasked == MotionEvent.ACTION_BUTTON_PRESS || event.actionMasked == MotionEvent.ACTION_BUTTON_RELEASE) &&
            event.actionButton == MotionEvent.BUTTON_PRIMARY
        ) {
            if (event.actionMasked == MotionEvent.ACTION_BUTTON_RELEASE && event.buttonState != 0) {
                mouseAsFinger = false
                sendAsFinger(event, MotionEvent.ACTION_UP)
            }
            return true
        }
        return super.dispatchGenericMotionEvent(event)
    }

    /** Volume up / down flip to the previous / next page (unless turned off in the settings). */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val key = when (event.keyCode) {
            KeyEvent.KEYCODE_VOLUME_UP -> "prev"
            KeyEvent.KEYCODE_VOLUME_DOWN -> "next"
            else -> null
        }
        if (key == null || !volumeKeysFlipPages || !hasWeb) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0)
            web.evaluateJavascript("window.onAndroidKey && window.onAndroidKey('$key')", null)
        return true
    }

    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        // Kiosk behaviour: Back does not leave the app, it returns to the first page.
        if (!hasWeb) return super.onBackPressed() // nothing to return to without the panel
        web.evaluateJavascript("window.onAndroidBack && window.onAndroidBack()", null)
    }

    override fun onResume() {
        super.onResume()
        ClipboardService.sync(applicationContext) // Phone bridge: starts, stops or reconnects it to match the switch
        if (!hasWeb) return
        resumed = true
        if (standbyMode != "off") {
            standbyHandler.removeCallbacks(standbyTick)
            standbyHandler.post(standbyTick)
        }
        // The agent's wake can reach us here before the lock screen is gone; dismiss it, but only inside a
        // wake the agent started (standbyDismissKeyguard checks), never on a plain resume.
        standbyDismissKeyguard()
        web.onResume()
        web.evaluateJavascript("window.onAppPause && window.onAppPause(false)", null) // the panel catches up at once
        stream.start()
        // the phone's own music for the Now playing widget: told about every change while the panel is on screen, and
        // handed what is playing now (the page also asks once when it starts)
        PhoneMediaMonitor.onChange = { json -> pushPhoneMedia(json) }
        // always, whatever the switch says: turning it off on the Phone bridge screen (this activity paused, so no onChange) published
        // "null" to nobody, and the panel would keep showing the song. The page ignores a null after a null, so it costs no redraw.
        pushPhoneMedia(if (BridgePrefs.phoneMediaEnabled(applicationContext)) PhoneMediaMonitor.current() else "null")
        hideSystemUi()
        sensors?.getDefaultSensor(Sensor.TYPE_LIGHT)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
        sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY)?.let { sensors?.registerListener(this, it, SensorManager.SENSOR_DELAY_NORMAL) }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == Dictation.PERMISSION_REQUEST)
            dictation.onPermission(grantResults.firstOrNull() == PackageManager.PERMISSION_GRANTED)
    }

    private fun pushPhoneMedia(json: String) {
        if (destroyed || !hasWeb) return
        web.evaluateJavascript("window.onPhoneMedia && window.onPhoneMedia(${JSONObject.quote(json)})", null)
    }

    override fun onPause() {
        resumed = false
        PhoneMediaMonitor.onChange = null
        dictation.cancel(0) // the microphone is never left open behind another app or a dark screen
        sensors?.unregisterListener(this)
        stream.stop()
        // WebView.onPause() leaves JavaScript running: tell the panel to stop polling and refreshing while nobody sees it
        if (hasWeb) {
            web.evaluateJavascript("window.onAppPause && window.onAppPause(true)", null)
            web.onPause()
        }
        super.onPause()
    }

    override fun onDestroy() {
        destroyed = true
        resumed = false
        PhoneMediaMonitor.onChange = null
        standbyDestroyed = true
        standbyHandler.removeCallbacks(standbyTick)
        standbyPowerReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyUsbReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyScreenReceiver?.let { runCatching { unregisterReceiver(it) } }
        standbyProbes.shutdownNow()
        dictation.cancel(0)
        sensors?.unregisterListener(this)
        stream.stop()
        link.trackpadSocket.close()
        link.close()
        if (hasWeb) {
            (web.parent as? ViewGroup)?.removeView(web) // a WebView must be detached before destroy()
            web.destroy()
        }
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

    private fun panelBootstrap(): String {
        val configuration = resources.configuration
        val languages = JSONArray()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            val locales = configuration.locales
            for (index in 0 until locales.size()) languages.put(locales[index].toLanguageTag())
        } else {
            @Suppress("DEPRECATION")
            languages.put(configuration.locale.toLanguageTag())
        }
        // only the panel itself, never a frame: a widget's frame keeps the panel's origin with allow-same-origin if its
        // page navigates to the panel's own address, and the origin rule alone would give that frame the key too
        return "if(window.top===window){window.__phoneboxKey='$bridgeKey';window.__phoneboxLanguages=$languages;}"
    }

    private fun loadPanel() = web.loadUrl("$PANEL_ORIGIN/assets/www/index.html")

    // The panel used to be served from https://$ASSET_HOST. Its localStorage (the settings kept for an offline start,
    // counters, timers, checklists) belongs to that origin, so it is copied over once, before the panel first starts on
    // http: read on an empty page of the old origin, written on one of the new, keeping anything already there. After a
    // failed attempt the old entries not yet copied win: what the new origin has for them was written after that failure
    // (a counter that started over), and the old value must not be lost. Entries already copied are listed under
    // MOVED_KEYS in the new origin and are never written again, so a change made since is kept.
    private var storageMove = 0 // 1: reading the old origin, 2: writing to the new one, 0: done
    private var movedStorage = "null"

    private fun moveStorage() {
        storageMove = 1
        web.loadUrl("https://$ASSET_HOST/assets/www/storage-move.html")
    }

    private fun moveStorageStep(view: WebView, url: String?) {
        if (storageMove == 1 && url?.startsWith("https://$ASSET_HOST/") == true) {
            storageMove = 2
            view.evaluateJavascript("JSON.stringify(Object.fromEntries(Object.keys(localStorage).map((k) => [k, localStorage.getItem(k)])))") { saved ->
                if (destroyed) return@evaluateJavascript
                movedStorage = saved ?: "null" // a JSON string literal holding the JSON of the entries, or null
                view.loadUrl("$PANEL_ORIGIN/assets/www/storage-move.html")
            }
        } else if (storageMove == 2 && url?.startsWith("$PANEL_ORIGIN/") == true) {
            storageMove = 0
            val overwrite = panelPreferences.getBoolean(STORAGE_MOVE_FAILED, false)
            val write = "(() => { try { const old = JSON.parse($movedStorage) || {};" +
                " const done = new Set(JSON.parse(localStorage.getItem('$MOVED_KEYS') || '[]'));" +
                " for (const [k, v] of Object.entries(old)) {" +
                "  if (k === '$MOVED_KEYS' || done.has(k)) continue;" +
                "  if ($overwrite || localStorage.getItem(k) === null) localStorage.setItem(k, v);" +
                "  done.add(k); localStorage.setItem('$MOVED_KEYS', JSON.stringify([...done])); }" +
                " localStorage.removeItem('$MOVED_KEYS'); return true; } catch (_) { return false; } })()"
            view.evaluateJavascript(write) { written ->
                if (destroyed) return@evaluateJavascript
                // done only when the old entries were read and all written; otherwise the next start tries again, and
                // then the old entries overwrite what the new origin got meanwhile
                if (movedStorage != "null" && written == "true") panelPreferences.edit().putBoolean(STORAGE_MOVED, true).apply()
                else panelPreferences.edit().putBoolean(STORAGE_MOVE_FAILED, true).apply()
                loadPanel()
            }
        }
    }

    private fun giveKey(view: WebView, url: String?) {
        if (!documentStartKey && url?.startsWith("$PANEL_ORIGIN/") == true)
            view.evaluateJavascript(panelBootstrap(), null)
    }

    inner class Bridge {
        private fun ok(key: String) = key == bridgeKey

        /** The phone's-music calls do nothing while its switch is off, keyed or not. */
        private fun phoneMediaOn() = BridgePrefs.phoneMediaEnabled(applicationContext)

        private val validTrackpadTypes = setOf("move", "click", "rightClick", "mousedown", "mouseup", "scroll", "hscroll") +
            // multi-finger gestures: three or four fingers swiping, four or five spreading or pinching (the agent follows the Mac's settings)
            listOf("swipe3up", "swipe3down", "swipe3left", "swipe3right", "swipe4up", "swipe4down", "swipe4left", "swipe4right", "spread", "pinch")

        /** The song the phone itself plays, as the Now playing widget's media object, or "null" (see [PhoneMediaMonitor]). */
        @JavascriptInterface
        fun phoneMedia(key: String): String = if (ok(key) && phoneMediaOn()) PhoneMediaMonitor.current() else "null"

        /** The cover picture of that song as a data URL, or "". */
        @JavascriptInterface
        fun phoneMediaArt(key: String, id: Int): String = if (ok(key) && phoneMediaOn()) PhoneMediaMonitor.art(id) else ""

        /** The phone's media volume and mute as the agent's audio state ("null" when it cannot be read). */
        @JavascriptInterface
        fun phoneAudio(key: String): String = if (ok(key) && phoneMediaOn()) PhoneMediaMonitor.audioJson(applicationContext) else "null"

        @JavascriptInterface
        fun phoneVolume(key: String, percent: Int): Boolean = ok(key) && phoneMediaOn() && PhoneMediaMonitor.setVolume(applicationContext, percent)

        @JavascriptInterface
        fun phoneMute(key: String): Boolean = ok(key) && phoneMediaOn() && PhoneMediaMonitor.toggleMute(applicationContext)

        /** A button of the Now playing widget pressed while the phone's song is shown: playpause, next, prev or seek (to [ms]). */
        @JavascriptInterface
        fun phoneMediaControl(key: String, action: String, ms: Double): Boolean =
            ok(key) && phoneMediaOn() && PhoneMediaMonitor.control(action, if (ms.isFinite()) ms.toLong() else 0L)

        @JavascriptInterface
        fun trackpad(key: String, type: String, dx: Float, dy: Float, delta: Float) {
            if (!ok(key) || type !in validTrackpadTypes) return
            val cleanDx = if (dx.isFinite()) dx else 0f
            val cleanDy = if (dy.isFinite()) dy else 0f
            val cleanDelta = if (delta.isFinite()) delta else 0f
            link.sendTrackpad(type, cleanDx, cleanDy, cleanDelta)
        }

        // The WebView receives the saved agent preference. Remember it for pairing dialogs before the next connection.
        @JavascriptInterface
        fun setLanguage(key: String, tag: String) {
            if (!ok(key) || !panelLanguageTag.matches(tag)) return
            val language = when (tag) {
                "auto" -> null
                "zh" -> "zh-CN"
                "zh-Hant" -> "zh-TW"
                "pt" -> "pt-PT"
                else -> tag
            }
            if (panelPreferences.getString("language", null) != language)
                panelPreferences.edit().putString("language", language).apply()
        }

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
            val v = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator? ?: return
            if (!v.hasVibrator()) return
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) v.vibrate(VibrationEffect.createOneShot(14, VibrationEffect.DEFAULT_AMPLITUDE))
            else @Suppress("DEPRECATION") v.vibrate(14)
        }

        /** Notification vibration; pattern is a "wait,vibrate,wait,vibrate..." list of milliseconds. */
        @JavascriptInterface
        fun buzz(key: String, pattern: String) {
            if (!ok(key)) return
            val v = getSystemService(Context.VIBRATOR_SERVICE) as Vibrator? ?: return
            val timings = pattern.split(',').mapNotNull { it.trim().toLongOrNull() }.toLongArray()
            if (!v.hasVibrator() || timings.isEmpty()) return
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) v.vibrate(VibrationEffect.createWaveform(timings, -1))
            else @Suppress("DEPRECATION") v.vibrate(timings, -1)
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

        /** Whether the phone is set to 24-hour time (Settings › System › Date & time). */
        @JavascriptInterface
        fun is24Hour(key: String): Boolean = if (ok(key)) DateFormat.is24HourFormat(this@MainActivity) else true

        /** Which sensors the phone has: {"light": true, "proximity": true}. */
        @JavascriptInterface
        fun sensors(key: String): String = if (!ok(key)) "{}" else JSONObject()
            .put("light", sensors?.getDefaultSensor(Sensor.TYPE_LIGHT) != null)
            .put("proximity", sensors?.getDefaultSensor(Sensor.TYPE_PROXIMITY) != null)
            .toString()

        /** The panel's overview button: opens the phone's own Phone bridge screen. */
        @JavascriptInterface
        fun openBridge(key: String) {
            if (!ok(key)) return
            runOnUiThread { startActivity(Intent(this@MainActivity, BridgeActivity::class.java)) }
        }

        /**
         * Screen pinning (lock task without a device owner): Home and Recents stay on the panel until it is unpinned
         * (Back + Overview held together, or turning the setting off). Android asks once to confirm.
         */
        @JavascriptInterface
        fun setPinned(key: String, on: Boolean) {
            if (!ok(key)) return
            runOnUiThread {
                val am = getSystemService(Context.ACTIVITY_SERVICE) as ActivityManager
                val pinned = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) am.lockTaskModeState != ActivityManager.LOCK_TASK_MODE_NONE
                    else @Suppress("DEPRECATION") am.isInLockTaskMode
                try { if (on && !pinned) startLockTask() else if (!on && pinned) stopLockTask() } catch (_: Exception) {}
            }
        }

        /** The computers this phone is paired with (for switching over Wi-Fi). */
        @JavascriptInterface
        fun pcs(key: String): String = if (ok(key)) link.pcsJson() else "[]"

        /** Switches to another paired computer and reloads the panel from it. */
        @JavascriptInterface
        fun selectPc(key: String, id: String) {
            if (!ok(key) || !PhoneNotificationListener.changeComputer { link.select(id) }) return
            bridgeFollowsComputer()
            runOnUiThread {
                if (isFinishing || isDestroyed) return@runOnUiThread
                stream.stop(); if (resumed) stream.start(); if (hasWeb) web.reload()
            }
        }

        @JavascriptInterface
        fun forgetPc(key: String, id: String) {
            if (!ok(key)) return
            PhoneNotificationListener.changeComputer { link.forget(id); true }
            bridgeFollowsComputer()
        }

        /**
         * The phone bridge keeps links of its own (the clipboard service's stream, the notification listener's cached Mac
         * switch): they would stay with the old computer, even a forgotten one, until the service restarted.
         */
        private fun bridgeFollowsComputer() {
            ClipboardService.sync(applicationContext) // restarts the stream with a new link when the clipboard switch is on
        }

        /** True when the Wake button can work: the active computer's MACs are known and the phone is on a local network. */
        @JavascriptInterface
        fun canWake(key: String): Boolean = ok(key) && link.canWake()

        /**
         * Sends a Wake-on-LAN packet to the paired computer [id]. Takes a computer's id and never a MAC address, so a
         * page cannot aim the packet at an arbitrary machine. False when nothing could be sent.
         */
        @JavascriptInterface
        fun wakePc(key: String, id: String): Boolean = ok(key) && link.wake(id)

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

        /** Dictation widget: true when the phone has a speech recognizer. */
        @JavascriptInterface
        fun canListen(key: String): Boolean = ok(key) && dictation.available()

        /**
         * Dictation widget: listens once ([lang] a BCP 47 tag or "") and returns the session's id; results arrive
         * through window.onAndroidSpeech(kind, text, id).
         */
        @JavascriptInterface
        fun listen(key: String, lang: String, offline: Boolean): Int =
            if (!ok(key)) 0 else dictation.start(lang.take(20).filter { it.isLetterOrDigit() || it == '-' }, offline)

        @JavascriptInterface
        fun stopListening(key: String, id: Int) { if (ok(key) && id > 0) dictation.stop(id) }

        @JavascriptInterface
        fun cancelListening(key: String, id: Int) { if (ok(key) && id > 0) dictation.cancel(id) }
    }

    companion object {
        private const val ASSET_HOST = "appassets.androidplatform.net"
        private const val PANEL_ORIGIN = "http://$ASSET_HOST"
        private const val STORAGE_MOVED = "storage-moved-to-http"
        private const val STORAGE_MOVE_FAILED = "storage-move-to-http-failed"
        private const val MOVED_KEYS = "phonebox-storage-move-done"
        private val NO_STORE = mapOf("Cache-Control" to "no-store")
        private const val PANEL_HEADER = "X-PhoneBox-Panel"
        private val READ_ONLY_IMAGES = setOf("/agent/api/art", "/agent/api/controls/radio-logo", "/agent/api/apps/icon", "/agent/api/discord/img", "/agent/api/shortcuts/icon",
            "/agent/api/steam/cover", "/agent/api/wallpaper")
        private val PASSED_HEADERS = setOf("content-security-policy", "x-content-type-options")
        private val DOCUMENT_TYPES = setOf("text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml")
        private fun FORBIDDEN() = WebResourceResponse("text/plain", "utf-8", 403, "Forbidden", NO_STORE, java.io.ByteArrayInputStream(ByteArray(0)))
    }
}
