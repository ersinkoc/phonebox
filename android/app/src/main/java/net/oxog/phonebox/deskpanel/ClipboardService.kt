package net.oxog.phonebox.deskpanel

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import java.util.concurrent.Executors

/**
 * Holds the connection to the computer's bridge stream while Phone bridge › clipboard is on, and puts each text copied
 * on the computer on this phone's clipboard. A foreground service, because Android stops ordinary background work.
 * The text goes straight from the stream to the clipboard: it is not kept, logged or shown here.
 */
class ClipboardService : Service() {
    private var stream: AgentStream? = null
    /** The current stream's link, closed with it: each one holds a worker thread for its pairing refreshes. */
    private var link: AgentLink? = null
    /** A stream is wanted (between the first start and onDestroy), also while its link is still being built. */
    private var wanted = false
    private val main = Handler(Looper.getMainLooper())
    /** Builds each AgentLink off the main thread: it reads the pairing preferences and looks up the WebView package. */
    private val builder = Executors.newSingleThreadExecutor()
    /** Which stream is the current one: what an old stream still delivers, or has queued for the main thread, is dropped. */
    @Volatile private var generation = 0
    /** The active computer the last stream was built for: the event ids of one Mac mean nothing to another. */
    private var activePc: String? = null

    /**
     * The stream's AgentLink read the paired computers once, when it was built. Choosing or forgetting a computer in the
     * panel (or a new pairing) changes them without the panel pausing, and the stream would go on sharing with the old
     * computer, a forgotten one included. Android calls this on the main thread, only for a value that really changed.
     * Held in a field: SharedPreferences keeps its listeners weakly. Posted, never run inside the change: building the new
     * AgentLink can itself save (moving an old pairing), and a restart nested in restartStream would leave two streams.
     */
    private val pairingChanged = SharedPreferences.OnSharedPreferenceChangeListener { _, _ ->
        main.post { if (wanted && BridgePrefs.clipboardEnabled(this)) restartStream() }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        getSharedPreferences(AgentLink.PAIRING_PREFS, Context.MODE_PRIVATE).registerOnSharedPreferenceChangeListener(pairingChanged)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Android wants startForeground soon after startForegroundService, even when we are about to stop again
        try { goForeground() } catch (e: Exception) {
            BridgePrefs.lastError = e.javaClass.simpleName
            BridgePrefs.clipboardState = ClipboardState.STOPPED
            stopSelf()
            return START_NOT_STICKY
        }
        if (!BridgePrefs.clipboardEnabled(this)) {
            stopSelf()
            return START_NOT_STICKY
        }
        BridgePrefs.lastError = null
        // the panel calls this on every resume: a connected stream is kept (cutting it would lose what is copied meanwhile and cost
        // a new TLS handshake each time); one that is unreachable starts over at once instead of finishing a long back-off, but
        // a refusal that retries by itself once a minute is not poked on every resume (see BridgeClip.restartOnResume).
        // Pairing changes restart it through [pairingChanged].
        if (BridgeClip.restartOnResume(stream != null, BridgePrefs.clipboardState)) restartStream()
        return START_STICKY
    }

    /** A new link and stream each time, so they use the computers paired now. Main thread only. */
    private fun restartStream() {
        stopStream()
        wanted = true
        val mine = ++generation
        BridgePrefs.clipboardState = ClipboardState.WAITING
        val app = applicationContext
        try {
            builder.execute {
                val fresh = try { AgentLink(app) } catch (e: Exception) {
                    // the screen says so (with the class name) instead of waiting for ever
                    main.post { if (mine == generation) { BridgePrefs.lastError = e.javaClass.simpleName; BridgePrefs.clipboardState = ClipboardState.STOPPED } }
                    return@execute
                }
                val active = app.getSharedPreferences(AgentLink.PAIRING_PREFS, Context.MODE_PRIVATE).getString("active", null)
                main.post {
                    // a newer restart, or onDestroy, came first: this link is not used
                    if (mine != generation || !wanted) closeLater(fresh) else startStream(mine, fresh, active)
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {} // onDestroy shut the builder down
    }

    private fun startStream(mine: Int, fresh: AgentLink, active: String?) {
        // another computer was chosen or paired: its seq numbers are its own, so it hears only new events
        if (active != activePc) eventIds.clear()
        activePc = active
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        link = fresh
        // the bridge stream only pings every 15 s, so a read timeout of 40 s (the panel's 8 s would cut it and reconnect, losing
        // copies and the battery); failures back off from 2 s to a minute instead of hammering an absent Mac all day. It stays on
        // Wi-Fi (no reconnect every minute to look for USB) and resumes from the last event id, so copies made on the Mac while
        // it reconnects are replayed by the agent instead of lost.
        stream = AgentStream(
            fresh, "/api/bridge/stream", readTimeoutMs = 40_000,
            retryMinMs = 2_000, retryMaxMs = 60_000, refusedRetryMs = 60_000,
            onFailure = { code -> if (mine == generation) BridgePrefs.clipboardState = BridgeClip.stateAfterFailure(code) },
            rotateOnWifi = false,
            eventIds = eventIds,
        ) { event, data ->
            if (mine != generation) return@AgentStream
            when (event) {
                "open" -> BridgePrefs.clipboardState = ClipboardState.RUNNING
                "close" -> BridgePrefs.clipboardState = ClipboardState.WAITING
                else -> BridgeClip.parseEvent(event, data)?.let { text ->
                    main.post { if (mine == generation && BridgePrefs.clipboardEnabled(this)) clipboard.setPrimaryClip(ClipData.newPlainText("PhoneBox", text)) }
                }
            }
        }.also { it.start() }
    }

    /** Stops the stream and closes its link. */
    private fun stopStream() {
        stream?.stop()
        stream = null
        link?.let { closeLater(it) }
        link = null
    }

    /** Frees the link's worker thread and trackpad socket; on the builder's thread, like building, while there is one. */
    private fun closeLater(old: AgentLink) {
        val close = Runnable { try { old.close() } catch (_: Exception) {} }
        try { builder.execute(close) } catch (_: java.util.concurrent.RejectedExecutionException) { close.run() }
    }

    private fun goForeground() {
        val notification = notification()
        if (Build.VERSION.SDK_INT >= 34) startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        else startForeground(NOTIFICATION_ID, notification)
    }

    private fun notification(): Notification {
        val immutable = if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0
        val open = PendingIntent.getActivity(this, 0, Intent(this, BridgeActivity::class.java), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        val builder = if (Build.VERSION.SDK_INT >= 26) {
            val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            manager.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.bridge_notification_channel), NotificationManager.IMPORTANCE_LOW))
            Notification.Builder(this, CHANNEL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this).setPriority(Notification.PRIORITY_MIN)
        }
        val send = PendingIntent.getActivity(this, 1, Intent(this, ReadClipboardActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), immutable or PendingIntent.FLAG_UPDATE_CURRENT)
        @Suppress("DEPRECATION") // the Icon-based Action builder is API 23, the app installs on 21
        builder.addAction(R.drawable.ic_tile, getString(R.string.bridge_send_to_mac), send)
        return builder.setSmallIcon(R.drawable.ic_tile)
            .setContentTitle(getString(R.string.bridge_notification_title))
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    override fun onDestroy() {
        getSharedPreferences(AgentLink.PAIRING_PREFS, Context.MODE_PRIVATE).unregisterOnSharedPreferenceChangeListener(pairingChanged)
        generation++
        wanted = false
        stopStream()
        // a new start hears only new events: a replay could bring back what was copied on the Mac while the phone's switch was off
        eventIds.clear()
        builder.shutdown() // a link still being built is closed by its post, which sees !wanted
        BridgePrefs.clipboardState = ClipboardState.STOPPED
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "bridge"
        private const val NOTIFICATION_ID = 7

        /**
         * The last bridge event id this process received, sent as Last-Event-ID when a stream connects again (also the new
         * stream after a pairing change or a reconnect from the screen), so the agent replays the copies made in between.
         * Cleared when the service stops, and never saved: a new start hears only new events, as the agent expects without
         * the header.
         */
        private val eventIds = AgentStream.EventIds()

        /** Starts or stops the service to match the phone's switch. Safe to call often; a start reconnects a stream that is not connected. */
        fun sync(context: Context) {
            val app = context.applicationContext
            val intent = Intent(app, ClipboardService::class.java)
            val on = BridgePrefs.clipboardEnabled(app)
            when (BridgeClip.syncAction(on, BridgePrefs.live(app))) {
                // for everyone who never turned the bridge on, and after the clean-up, "off" is nothing at all: no component is
                // looked at and no service is stopped, on any of the panel's resumes
                BridgeClip.SyncAction.NOTHING -> return
                BridgeClip.SyncAction.CLEAN_UP -> {
                    app.stopService(intent) // nothing happens when it is not running
                    BridgeEntryPoints.setEnabled(app, false)
                    BridgePrefs.setLive(app, false)
                    return
                }
                BridgeClip.SyncAction.START -> {
                    // "Send to Mac" shows in other apps' menus only while the bridge is on (a no-op when nothing changes)
                    BridgeEntryPoints.setEnabled(app, true)
                    if (!BridgePrefs.live(app)) BridgePrefs.setLive(app, true)
                }
            }
            try {
                if (Build.VERSION.SDK_INT >= 26) app.startForegroundService(intent) else app.startService(intent)
            } catch (e: Exception) { // Android 12+ refuses a foreground start from the background
                BridgePrefs.lastError = e.javaClass.simpleName
            }
        }
    }
}
