package net.oxog.phonebox.deskpanel

import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.util.Base64
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors

/**
 * Reads what the phone itself is playing (Spotify, YouTube Music, a browser...) from Android's media sessions, so the panel's
 * Now playing widget can show it and its buttons can drive it when the computer plays nothing. Reading sessions needs the
 * PhoneBox notification listener to be allowed (Android's rule), which is why it runs inside [PhoneNotificationListener].
 * Nothing here leaves the phone: the song goes to the panel's page in this app, never to the computer. It is off until the
 * "Show the phone's music" switch is on.
 *
 * It is cheap by design, because the session callbacks run on the main thread (a player may update its state every second):
 * nothing is done while the panel is not on screen ([onChange] is null then), what the callbacks hand over is kept (no IPC
 * to read a session again), and the cover picture is scaled and compressed on a background thread, once per song.
 */
object PhoneMediaMonitor {
    private val main = Handler(Looper.getMainLooper())
    private val encoder = Executors.newSingleThreadExecutor()

    /** One active session with what Android last told us about it (from the callbacks, so it is never asked again). */
    private class Held(val controller: MediaController) {
        var state: PlaybackState? = null
        var metadata: MediaMetadata? = null
        lateinit var callback: MediaController.Callback
    }

    // all of these change on the main thread only (onMain); the volatile ones are also read from the page's JavaScript thread
    private var context: Context? = null
    private var manager: MediaSessionManager? = null
    private var sessionsListener: MediaSessionManager.OnActiveSessionsChangedListener? = null
    private var held: List<Held> = emptyList()
    private class Reading(val track: PhoneTrack?, val at: Long)
    @Volatile private var reading = Reading(null, 0)
    private val labels = HashMap<String, String>()
    private var artKey = ""
    private var artHadBitmap = false
    private var artSeq = 0
    private var coverGen = 0
    @Volatile private var artData: String? = null
    @Volatile private var chosen: MediaController? = null
    @Volatile private var artId = 0

    /**
     * Called on the main thread with the song as JSON ("null" for none) whenever the panel should update. Set only while the
     * panel is on screen: setting it reads the sessions once, to catch up with what happened meanwhile.
     */
    @Volatile var onChange: ((String) -> Unit)? = null
        set(value) {
            field = value
            if (value != null) onMain { refresh() }
        }

    /** The song now, as the panel's media object, or "null". */
    fun current(): String {
        val r = reading // once: the track and the moment it was read belong together
        return PhoneMediaModel.json(PhoneMediaModel.advance(r.track, r.at, SystemClock.elapsedRealtime())) // its position as of now, not as of the last event
    }

    /** The cover picture of song [id] as a data URL, or "" (it is gone or never existed). */
    fun art(id: Int): String = if (id != 0 && id == artId) artData.orEmpty() else ""

    private fun onMain(block: () -> Unit) { if (Looper.myLooper() == main.looper) block() else main.post(block) }

    /** Starts or stops to match the phone's switch and whether Android has bound the notification listener. Safe to call often. */
    fun sync(app: Context) {
        val wanted = BridgePrefs.phoneMediaEnabled(app) && PhoneNotificationListener.connected
        val component = ComponentName(app, PhoneNotificationListener::class.java)
        onMain { if (wanted) start(app.applicationContext, component) else stop() }
    }

    private fun start(app: Context, component: ComponentName) {
        if (context != null && sessionsListener != null) return // already running
        stop()
        try {
            val m = app.getSystemService(Context.MEDIA_SESSION_SERVICE) as? MediaSessionManager ?: return
            val listener = MediaSessionManager.OnActiveSessionsChangedListener { adopt(it.orEmpty()) }
            m.addOnActiveSessionsChangedListener(listener, component, main)
            context = app
            manager = m
            sessionsListener = listener
            adopt(m.getActiveSessions(component))
        } catch (_: SecurityException) {
            stop() // notification access is not allowed (yet)
        } catch (_: Exception) {
            stop()
        }
    }

    fun stop() = onMain {
        try { sessionsListener?.let { manager?.removeOnActiveSessionsChangedListener(it) } } catch (_: Exception) {}
        sessionsListener = null
        manager = null
        context = null
        release()
        chosen = null
        val hadSong = reading.track != null
        reading = Reading(null, 0)
        coverGen++ // a cover still being encoded is dropped
        artKey = ""; artHadBitmap = false; artData = null; artId = 0
        if (hadSong) publish("null") // the page is told only when it had a song to forget
    }

    private fun release() {
        for (h in held) try { h.controller.unregisterCallback(h.callback) } catch (_: Exception) {}
        held = emptyList()
    }

    private fun adopt(controllers: List<MediaController>) {
        release()
        held = controllers.map { c ->
            val h = Held(c)
            // read once now; from here on the callbacks carry what changed
            try { h.state = c.playbackState; h.metadata = c.metadata } catch (_: Exception) {}
            h.callback = object : MediaController.Callback() {
                override fun onPlaybackStateChanged(state: PlaybackState?) { h.state = state; refresh() }
                override fun onMetadataChanged(metadata: MediaMetadata?) { h.metadata = metadata; refresh() }
                override fun onSessionDestroyed() { h.state = null; h.metadata = null; refresh() }
            }
            try { c.registerCallback(h.callback, main) } catch (_: Exception) {}
            h
        }
        refresh()
    }

    private fun titleOf(md: MediaMetadata?): String =
        PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: md?.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE))

    private fun refresh() {
        val app = context ?: return
        if (onChange == null) return // the panel is not on screen: nothing is worked out (it catches up when it comes back)
        val facts = held.map { h ->
            val state = h.state?.state ?: 0
            SessionFacts(h.controller.packageName, PhoneMediaModel.isPlaying(state), PhoneMediaModel.isPaused(state), titleOf(h.metadata).isNotEmpty())
        }
        val at = PhoneMediaModel.pick(app.packageName, facts)
        val picked = if (at < 0) null else held[at]
        chosen = picked?.controller
        val track = picked?.let { try { build(app, it) } catch (_: Exception) { null } }
        val now = SystemClock.elapsedRealtime()
        val before = reading
        if (!PhoneMediaModel.worthSending(before.track, before.at, track, now)) return
        reading = Reading(track, now)
        publish(PhoneMediaModel.json(track))
    }

    private fun publish(next: String) {
        onChange?.invoke(next)
    }

    private fun build(app: Context, h: Held): PhoneTrack {
        val md = h.metadata
        val st = h.state
        val title = titleOf(md)
        val artist = PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: md?.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST)
            ?: md?.getString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE))
        val album = PhoneMediaModel.cut(md?.getString(MediaMetadata.METADATA_KEY_ALBUM))
        val state = st?.state ?: 0
        val playing = PhoneMediaModel.isPlaying(state)
        val dur = PhoneMediaModel.duration(md?.getLong(MediaMetadata.METADATA_KEY_DURATION) ?: 0)
        val known = st != null && st.position >= 0
        val pos = PhoneMediaModel.position(st?.position ?: -1, st?.lastPositionUpdateTime ?: 0, SystemClock.elapsedRealtime(),
            st?.playbackSpeed ?: 1f, playing, dur)
        val canSeek = dur > 0 && known && (st!!.actions and PlaybackState.ACTION_SEEK_TO) != 0L
        val pkg = h.controller.packageName
        return PhoneTrack(pkg, label(app, pkg), title, artist, album, playing, pos, dur, canSeek,
            PhoneMediaModel.rate(st?.playbackSpeed ?: 1f), coverFor(pkg, title, artist, album, md))
    }

    /**
     * The id of the cover picture for this song (0 while there is none). The picture is scaled and compressed on a background
     * thread, once per song (and again only if it arrives after the first metadata); when it is ready the song is worked out
     * again, which tells the page the new id.
     */
    private fun coverFor(pkg: String, title: String, artist: String, album: String, md: MediaMetadata?): Int {
        val key = "$pkg|$title|$artist|$album"
        val bitmap = try {
            md?.getBitmap(MediaMetadata.METADATA_KEY_ART) ?: md?.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART)
                ?: md?.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
        } catch (_: Exception) { null }
        if (key == artKey && (artHadBitmap || bitmap == null)) return artId // this song's cover is done, pending, or there is none
        coverGen++
        val gen = coverGen
        artKey = key
        artHadBitmap = bitmap != null
        artData = null
        artId = 0
        if (bitmap == null) return 0
        encoder.execute {
            val data = encode(bitmap)
            main.post {
                if (gen != coverGen || key != artKey) return@post // another song since
                artData = data
                artId = if (data == null) 0 else PhoneMediaModel.ART_ID_BASE + ++artSeq
                if (artId != 0) refresh()
            }
        }
        return 0
    }

    /** A small JPEG (at most 256 px) as a data URL: the panel shows it in a card a few centimetres wide. Runs off the main thread.
     * Null (no cover) when the bitmap cannot be encoded: compress() reports some failures by returning false with nothing written. */
    private fun encode(src: Bitmap): String? {
        if (src.isRecycled || src.width <= 0 || src.height <= 0) return null
        val made = ArrayList<Bitmap>(2) // bitmaps made here; src belongs to the media session
        return try {
            var bmp = src
            // A hardware bitmap cannot be drawn into a software canvas (scaling it fails before Android 9): copy it first.
            if (Build.VERSION.SDK_INT >= 26 && bmp.config == Bitmap.Config.HARDWARE)
                bmp = bmp.copy(Bitmap.Config.ARGB_8888, false)?.also { made += it } ?: return null
            val side = maxOf(bmp.width, bmp.height)
            if (side > 256) {
                val scaled = Bitmap.createScaledBitmap(bmp, maxOf(1, bmp.width * 256 / side), maxOf(1, bmp.height * 256 / side), true)
                if (scaled !== bmp && scaled !== src) made += scaled
                bmp = scaled
            }
            val out = ByteArrayOutputStream()
            if (!bmp.compress(Bitmap.CompressFormat.JPEG, 80, out) || out.size() == 0) null
            else "data:image/jpeg;base64," + Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
        } catch (_: Exception) { null } finally { made.forEach { it.recycle() } }
    }

    /** The app's name, asked of the package manager once per app. */
    private fun label(app: Context, pkg: String): String = labels.getOrPut(pkg) {
        try { app.packageManager.getApplicationLabel(app.packageManager.getApplicationInfo(pkg, 0)).toString() } catch (_: Exception) { pkg }
    }

    private fun audio(app: Context) = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    /** The phone's media volume and mute, in the shape of the agent's audio state: what the widget shows while the phone's song is shown. */
    fun audioJson(app: Context): String = try {
        val am = audio(app)
        val volume = am.getStreamVolume(AudioManager.STREAM_MUSIC)
        val muted = if (Build.VERSION.SDK_INT >= 23) am.isStreamMute(AudioManager.STREAM_MUSIC) else volume == 0
        PhoneMediaModel.audioJson(volume, am.getStreamMaxVolume(AudioManager.STREAM_MUSIC), muted)
    } catch (_: Exception) { "null" }

    /** Sets the phone's media volume to [percent] (0-100), without the system's volume panel. */
    fun setVolume(app: Context, percent: Int): Boolean = try {
        val am = audio(app)
        am.setStreamVolume(AudioManager.STREAM_MUSIC, PhoneMediaModel.volumeStep(percent, am.getStreamMaxVolume(AudioManager.STREAM_MUSIC)), 0)
        true
    } catch (_: Exception) { false }

    /** Mutes or unmutes the phone's media (Android 6 and later; older systems have no such call that works for an app). */
    fun toggleMute(app: Context): Boolean = try {
        if (Build.VERSION.SDK_INT < 23) false
        else { audio(app).adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_TOGGLE_MUTE, 0); true }
    } catch (_: Exception) { false }

    /** A button of the Now playing widget pressed while the phone's song is shown. False when nothing is there to control. */
    fun control(action: String, ms: Long): Boolean {
        if (!PhoneMediaModel.validAction(action)) return false
        val c = chosen ?: return false
        return try {
            val t = c.transportControls
            when (action) {
                "playpause" -> if (PhoneMediaModel.isPlaying(c.playbackState?.state ?: 0)) t.pause() else t.play()
                "next" -> t.skipToNext()
                "prev" -> t.skipToPrevious()
                "seek" -> t.seekTo(PhoneMediaModel.seekTarget(ms, reading.track?.durMs ?: 0))
            }
            true
        } catch (_: Exception) { false }
    }
}
