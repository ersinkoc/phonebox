package net.zephracloud.phonebox.deskpanel

import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.webkit.WebViewCompat
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayInputStream
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.Executors
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocketFactory
import javax.net.ssl.X509TrustManager

/**
 * Connection to the PhoneBox agent on the PC.
 *
 * 1. USB: http://127.0.0.1:8765, tunnelled by "adb reverse" while the cable is plugged in.
 * 2. Wi-Fi / Tailscale: https://<pc-address>:<port>. The address list, a token and the SHA-256 fingerprint of the
 *    agent's self-signed certificate are fetched over USB ("pairing") and stored. Only that exact certificate is
 *    trusted (pinning), and every request carries the token.
 *
 * The phone remembers every computer it was paired with; one is active over Wi-Fi (the one last plugged in, or the one
 * picked in the panel's page overview). A USB cable always wins: the plugged-in computer is used and becomes active.
 */
class AgentLink(private val context: Context) {

    class Reply(val code: Int, val contentType: String?, val body: InputStream)

    enum class Route { USB, WIFI, NONE }

    @Volatile var route: Route = Route.NONE
        private set

    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    /** A computer this phone is paired with; the certificate fingerprint is its id. */
    internal class Pc(val id: String, val name: String, val hosts: List<String>, val port: Int, val token: String) {
        fun json(): JSONObject = JSONObject().put("id", id).put("name", name).put("hosts", JSONArray(hosts)).put("port", port).put("token", token)
        companion object {
            fun from(j: JSONObject) = Pc(j.getString("id"), j.optString("name"),
                j.getJSONArray("hosts").let { a -> (0 until a.length()).map { a.getString(it) } }, j.getInt("port"), j.getString("token"))
        }
    }

    @Volatile private var pcs: List<Pc> = loadPcs()
    @Volatile private var activeId: String? = prefs.getString("active", null)

    private fun loadPcs(): List<Pc> {
        // 0.6.x stored a single pairing in plain keys; move it into the list
        if (!prefs.contains("pcs") && prefs.getBoolean("enabled", false)) {
            val fp = prefs.getString("fingerprint", null)
            val token = prefs.getString("token", null)
            if (fp != null && token != null) {
                val pc = Pc(fp, prefs.getString("pc", "") ?: "", prefs.getString("hosts", "")!!.split(',').filter { it.isNotBlank() }, prefs.getInt("port", 8766), token)
                prefs.edit().clear().putString("pcs", JSONArray().put(pc.json()).toString()).putString("active", fp).apply()
            }
        }
        return try {
            val a = JSONArray(prefs.getString("pcs", "[]"))
            (0 until a.length()).mapNotNull { runCatching { Pc.from(a.getJSONObject(it)) }.getOrNull() }
        } catch (_: Exception) { emptyList() }
    }

    /**
     * Guards every read-modify-save of the computer list (USB re-pairing, a QR pairing, select, forget): each one
     * reads [pcs], filters it and saves it, and two at once would drop the other's change.
     */
    private val pcsLock = Any()

    private fun savePcs(list: List<Pc>, active: String?) {
        pcs = list
        activeId = active
        prefs.edit().putString("pcs", JSONArray().apply { list.forEach { put(it.json()) } }.toString()).putString("active", active).apply()
    }

    internal fun activePc(): Pc? = pcs.firstOrNull { it.id == activeId } ?: pcs.firstOrNull()
    internal fun getPinnedFactory(pin: String): SSLSocketFactory = pinnedFactory(pin)

    /** The paired computers for the panel: [{"id", "name", "active"}]. */
    fun pcsJson(): String {
        val active = activePc()?.id
        return JSONArray().apply { pcs.forEach { put(JSONObject().put("id", it.id).put("name", it.name).put("active", it.id == active)) } }.toString()
    }

    /** Makes another paired computer the one used over Wi-Fi. */
    fun select(id: String): Boolean = synchronized(pcsLock) {
        if (pcs.none { it.id == id }) return false
        savePcs(pcs, id)
        factory = null
        preferredHost = 0
        trackpadSocket.close()
        return true
    }

    fun forget(id: String) = synchronized(pcsLock) {
        val rest = pcs.filter { it.id != id }
        savePcs(rest, if (activeId == id) rest.firstOrNull()?.id else activeId)
        trackpadSocket.close()
    }
    private val appVersion = context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: ""
    // Identifies this phone to the agent (several phones can show different pages): a short hash of the app-scoped
    // Android id, and the model name.
    private val deviceId = MessageDigest.getInstance("SHA-256")
        .digest((Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown").toByteArray())
        .take(6).joinToString("") { "%02x".format(it) }
    private val deviceModel = "${Build.MANUFACTURER} ${Build.MODEL}".filter { it in ' '..'~' }.trim().take(40)
    private val webViewInfo = runCatching {
        WebViewCompat.getCurrentWebViewPackage(context)?.let { p ->
            "${p.packageName}/${p.versionName ?: ""}".filter { it in ' '..'~' }.trim().take(80)
        } ?: ""
    }.getOrDefault("")
    private val background = Executors.newSingleThreadExecutor()
    @Volatile private var lastPair = 0L
    @Volatile private var preferredHost = 0
    @Volatile private var factory: Pair<String, SSLSocketFactory>? = null

    /** Opens a long-lived GET (the live stream) over the best route; null when the PC is unreachable. */
    fun stream(path: String): Pair<Route, Reply>? {
        usb("GET", path, STREAM_READ_TIMEOUT)?.let { route = Route.USB; return Route.USB to it }
        wifi("GET", path, STREAM_READ_TIMEOUT)?.let { route = Route.WIFI; return Route.WIFI to it }
        return null
    }

    /** Forwards a request (method + path with query) to the agent over the best available route. */
    fun request(method: String, pathAndQuery: String): Reply = request(method, pathAndQuery, null, null)

    fun request(method: String, pathAndQuery: String, body: ByteArray?, contentType: String?): Reply {
        usb(method, pathAndQuery, 4000, body, contentType)?.let {
            route = Route.USB
            maybePair()
            return it
        }
        wifi(method, pathAndQuery, 4000, body, contentType)?.let {
            route = Route.WIFI
            return it
        }
        route = Route.NONE
        return Reply(503, "application/json", ByteArrayInputStream("""{"error":"offline"}""".toByteArray()))
    }

    val trackpadSocket = TrackpadSocket(this)

    private class TrackpadEvent(val type: String, var dx: Float, var dy: Float, var delta: Float)
    private var inFlight = false
    private val pendingEvents = java.util.ArrayDeque<TrackpadEvent>()
    private val httpTrackpadLock = Any()

    fun sendTrackpad(type: String, dx: Float, dy: Float, delta: Float) {
        if (route == Route.USB || !trackpadSocket.send(type, dx, dy, delta)) {
            coalesceHttpTrackpad(type, dx, dy, delta)
        }
    }

    private fun coalesceHttpTrackpad(type: String, dx: Float, dy: Float, delta: Float) {
        synchronized(httpTrackpadLock) {
            if (inFlight) {
                val last = pendingEvents.peekLast()
                if (last != null && last.type == type && (type == "move" || type == "scroll")) {
                    last.dx += dx
                    last.dy += dy
                    last.delta += delta
                } else {
                    pendingEvents.addLast(TrackpadEvent(type, dx, dy, delta))
                }
                return
            }
            inFlight = true
        }
        dispatchHttpTrackpad(type, dx, dy, delta)
    }

    private fun dispatchHttpTrackpad(type: String, dx: Float, dy: Float, delta: Float) {
        background.execute {
            try {
                val json = """{"type":"$type","dx":$dx,"dy":$dy,"delta":$delta}""".toByteArray(Charsets.UTF_8)
                val reply = request("POST", "/api/trackpad", body = json, contentType = "application/json")
                try { reply.body.close() } catch (_: Exception) {}
            } finally {
                var nextType = ""
                var nextDx = 0f
                var nextDy = 0f
                var nextDelta = 0f
                var hasNext = false
                synchronized(httpTrackpadLock) {
                    val next = pendingEvents.pollFirst()
                    if (next != null) {
                        nextType = next.type
                        nextDx = next.dx
                        nextDy = next.dy
                        nextDelta = next.delta
                        hasNext = true
                    } else {
                        inFlight = false
                    }
                }
                if (hasNext) {
                    dispatchHttpTrackpad(nextType, nextDx, nextDy, nextDelta)
                }
            }
        }
    }

    private fun usb(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? =
        usb(method, pathAndQuery, readTimeout, null, null)

    private fun usb(method: String, pathAndQuery: String, readTimeout: Int, body: ByteArray?, contentType: String?): Reply? = try {
        val c = URL("$USB_BASE$pathAndQuery").openConnection() as HttpURLConnection
        c.connectTimeout = 400 // refused instantly when no adb reverse tunnel exists
        send(c, method, readTimeout, body, contentType)
    } catch (e: Exception) {
        null
    }

    private fun wifi(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? =
        wifi(method, pathAndQuery, readTimeout, null, null)

    private fun wifi(method: String, pathAndQuery: String, readTimeout: Int, body: ByteArray?, contentType: String?): Reply? {
        val pc = activePc() ?: return null
        val hosts = pc.hosts
        val token = pc.token
        val pin = pc.id
        val port = pc.port
        if (hosts.isEmpty()) return null
        // Reuse one socket factory per pinned certificate so HTTPS connections are kept alive between requests.
        val f = factory?.takeIf { it.first == pin }?.second ?: pinnedFactory(pin).also { factory = pin to it }
        // Start with the address that worked last time.
        val order = hosts.indices.sortedBy { if (it == preferredHost) -1 else it }
        for (i in order) {
            try {
                val c = URL("https://${hosts[i]}:$port$pathAndQuery").openConnection() as HttpsURLConnection
                c.sslSocketFactory = f
                c.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true } // identity is the pinned certificate
                c.connectTimeout = 1500
                c.setRequestProperty("Authorization", "Bearer $token")
                val r = send(c, method, readTimeout, body, contentType)
                preferredHost = i
                return r
            } catch (e: Exception) {
                // try the next address
            }
        }
        return null
    }

    private fun send(c: HttpURLConnection, method: String, readTimeout: Int = 4000): Reply =
        send(c, method, readTimeout, null, null)

    private fun send(c: HttpURLConnection, method: String, readTimeout: Int, body: ByteArray?, contentType: String?): Reply {
        // every POST acts on the computer (a container restart, a Home Assistant switch, a macro): none may run twice
        val controlPost = method == "POST"
        c.instanceFollowRedirects = false
        c.requestMethod = method
        c.setRequestProperty("X-PhoneBox-App", appVersion) // lets the agent offer updates
        c.setRequestProperty("X-PhoneBox-Device", deviceId)
        c.setRequestProperty("X-PhoneBox-Model", deviceModel)
        if (webViewInfo.isNotEmpty()) c.setRequestProperty("X-PhoneBox-WebView", webViewInfo)
        // A workflow or service restart can take longer than a state poll.
        c.readTimeout = if (controlPost) 30000 else readTimeout
        c.useCaches = false
        if (method == "POST") {
            c.doOutput = true
            if (body != null) {
                if (contentType != null) c.setRequestProperty("Content-Type", contentType)
                c.setFixedLengthStreamingMode(body.size)
                c.outputStream.write(body)
            } else {
                c.setFixedLengthStreamingMode(0)
            }
            c.outputStream.close()
        }
        val code = try { c.responseCode } catch (e: java.io.IOException) {
            if (!controlPost) throw e
            // The command body was already submitted. Trying the Wi-Fi route now could run a macro or toggle twice.
            c.disconnect()
            return Reply(504, "application/json", ByteArrayInputStream("""{"ok":false,"error":"The command result is unknown; check the computer before retrying"}""".toByteArray()))
        }
        if (code !in 100..599 || code in 300..399) {
            try { (if (code >= 400) c.errorStream else c.inputStream)?.close() } catch (_: Exception) {}
            c.disconnect()
            return Reply(502, "application/json", ByteArrayInputStream("""{"error":"unexpected response"}""".toByteArray()))
        }
        val body = (if (code >= 400) c.errorStream else c.inputStream) ?: ByteArrayInputStream(ByteArray(0))
        return Reply(code, c.contentType, body)
    }

    /** While on USB, refresh the Wi-Fi pairing data about once a minute. */
    private fun maybePair() {
        val now = System.currentTimeMillis()
        if (now - lastPair < 60_000) return
        lastPair = now
        try {
            background.execute {
                try {
                    refreshPairing(URL("$USB_BASE/api/pair").openConnection() as HttpURLConnection)
                } catch (_: Exception) {
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
        }
    }

    private fun refreshPairing(c: HttpURLConnection) {
        // released on every way out, a header read that throws included
        try { readPairing(c) } finally { c.disconnect() }
    }

    private fun readPairing(c: HttpURLConnection) {
        c.instanceFollowRedirects = false
        c.connectTimeout = 800
        c.readTimeout = 3000
        val code = c.responseCode
        if (code != 200) {
            // a redirect or error body is not the agent's pairing answer: it must not add or forget a computer
            try { (if (code >= 400) c.errorStream else c.inputStream)?.close() } catch (_: Exception) {}
            return
        }
        val j = JSONObject(c.inputStream.bufferedReader().use { it.readText() })
        val name = j.optString("pc")
        if (j.optBoolean("enabled")) {
            // add or refresh this computer and make it the active one
            val hosts = j.getJSONArray("hosts")
            val pc = Pc(j.getString("fingerprint"), name, (0 until hosts.length()).map { hosts.getString(it) }, j.getInt("port"), j.getString("token"))
            synchronized(pcsLock) { if (!closed) savePcs(pcs.filter { it.id != pc.id && !(name.isNotEmpty() && it.name == name) } + pc, pc.id) }
        } else if (name.isNotEmpty()) {
            // Wi-Fi was turned off on this computer: forget its pairing, keep the others
            synchronized(pcsLock) {
                val rest = pcs.filter { it.name != name }
                if (!closed && rest.size != pcs.size) savePcs(rest, rest.firstOrNull { it.id == activeId }?.id ?: rest.firstOrNull()?.id)
            }
        }
    }

    /**
     * Set by [close] under [pcsLock]. shutdownNow() does not wait for a refresh already running; one that reaches the
     * lock after close() must not save this instance's list over a newer instance's change (e.g. a forgotten computer).
     */
    private var closed = false

    fun close() {
        synchronized(pcsLock) { closed = true }
        trackpadSocket.close()
        background.shutdownNow()
    }

    fun pairedPc(): String? = activePc()?.name

    /** Why a pairing failed, so the shell can say it in the phone's language instead of English inside a dialog. */
    sealed class PairFailure {
        /** No address answered: not the same network, or the firewall blocked PhoneBox. */
        data class Unreachable(val pc: String, val hosts: List<String>) : PairFailure()

        /** The agent refused the one-time code (it works once, for five minutes). */
        data class CodeRejected(val pc: String) : PairFailure()

        /** The agent answered, but with an HTTP status the pairing flow does not know. */
        data class BadAnswer(val pc: String, val code: Int) : PairFailure()

        /** The certificate is not the one in the QR code, so this is not the computer the user confirmed. */
        data class WrongCertificate(val pc: String) : PairFailure()

        fun message(context: Context): String = when (this) {
            is Unreachable -> context.getString(R.string.pair_failed_unreachable, pc, hosts.joinToString())
            is CodeRejected -> context.getString(R.string.pair_failed_code, pc)
            is BadAnswer -> context.getString(R.string.pair_failed_answer, pc, code)
            is WrongCertificate -> context.getString(R.string.pair_failed_certificate)
        }
    }

    /**
     * Pairing without USB: the computer's settings page shows a QR code with this link, and the phone trades the
     * one-time code for the Wi-Fi key. The certificate fingerprint in the link is pinned from the first connection,
     * as with USB pairing, so the key only ever goes to that computer.
     */
    class PairLink(val pc: String, val fingerprint: String, val port: Int, val hosts: List<String>, val code: String) {
        /**
         * The start of the certificate fingerprint, shown on the computer next to the QR code and in the phone's
         * confirmation: a link from anyone else (another computer, a message) shows a different code.
         */
        val check: String get() = fingerprint.take(8).uppercase().chunked(4).joinToString(" ")

        companion object {
            private val FINGERPRINT = Regex("^[0-9a-f]{64}$")
            private val CODE = Regex("^[A-Za-z0-9_-]{16,64}$")

            /**
             * Only the addresses the agent offers: private network (10/8, 172.16/12, 192.168/16) and Tailscale
             * (100.64/10) IPv4. A link pointing anywhere else is not a PhoneBox pairing link.
             */
            fun isLocalAddress(host: String): Boolean {
                val p = host.split('.')
                if (p.size != 4) return false
                val b = p.map { s -> s.toIntOrNull()?.takeIf { s.length in 1..3 && it in 0..255 } ?: return false }
                return b[0] == 10 || (b[0] == 172 && b[1] in 16..31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] in 64..127)
            }

            /**
             * Reads https://ersinkoc.github.io/phonebox/pair/#v=1&... (the QR code; the data is in the fragment so it
             * never reaches a web server) or phonebox://pair?v=1&... (the "Open in PhoneBox" button on that page).
             * Anything malformed returns null.
             */
            fun parse(uri: Uri?): PairLink? {
                if (uri == null) return null
                val raw = when {
                    uri.scheme == "https" && uri.host == "ersinkoc.github.io" && uri.path?.startsWith("/phonebox/pair") == true -> uri.encodedFragment
                    uri.scheme == "phonebox" && uri.host == "pair" -> uri.encodedQuery
                    else -> null
                } ?: return null
                val q = raw.split('&').mapNotNull { part ->
                    val i = part.indexOf('=')
                    if (i <= 0) null else part.substring(0, i) to Uri.decode(part.substring(i + 1))
                }.toMap()
                if (q["v"] != "1") return null
                val fp = q["fp"]?.lowercase()?.takeIf { FINGERPRINT.matches(it) } ?: return null
                val port = q["port"]?.toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
                val code = q["c"]?.takeIf { CODE.matches(it) } ?: return null
                val hosts = q["h"].orEmpty().split(',').map { it.trim() }.filter { isLocalAddress(it) }.take(8)
                if (hosts.isEmpty()) return null
                val pc = q["pc"].orEmpty().filter { it >= ' ' }.take(64).ifBlank { "your computer" }
                return PairLink(pc, fp, port, hosts, code)
            }
        }
    }

    /**
     * Trades the link's one-time code for the Wi-Fi key and makes that computer the active one. Blocks (network):
     * call it off the main thread. Returns null when paired, otherwise why it failed, which the shell shows in the
     * phone's language.
     */
    fun claim(link: PairLink): PairFailure? {
        val pinned = pinnedFactory(link.fingerprint)
        var problem: PairFailure = PairFailure.Unreachable(link.pc, link.hosts)
        for (host in link.hosts) {
            try {
                val c = URL("https://$host:${link.port}/api/pair/claim").openConnection() as HttpsURLConnection
                c.instanceFollowRedirects = false
                c.sslSocketFactory = pinned
                c.hostnameVerifier = javax.net.ssl.HostnameVerifier { _, _ -> true } // identity is the pinned certificate
                c.connectTimeout = 3000
                c.readTimeout = 8000
                c.requestMethod = "POST"
                c.doOutput = true
                c.setRequestProperty("Content-Type", "application/json")
                c.setRequestProperty("X-PhoneBox-Model", deviceModel)
                c.outputStream.use { it.write(JSONObject().put("code", link.code).toString().toByteArray()) }
                when (c.responseCode) {
                    200 -> {}
                    403 -> return PairFailure.CodeRejected(link.pc)
                    else -> { problem = PairFailure.BadAnswer(link.pc, c.responseCode); continue }
                }
                val j = JSONObject(c.inputStream.bufferedReader().readText())
                if (!j.optString("fingerprint").equals(link.fingerprint, ignoreCase = true)) return PairFailure.WrongCertificate(link.pc)
                val hosts = j.optJSONArray("hosts")?.let { a -> (0 until a.length()).map { a.getString(it) } }
                    ?.filter { PairLink.isLocalAddress(it) }?.takeIf { it.isNotEmpty() } ?: link.hosts
                // the name the user confirmed; only an entry for this very certificate is replaced, never one that
                // merely has the same name
                val pc = Pc(link.fingerprint.lowercase(), link.pc, hosts, j.optInt("port", link.port), j.getString("token"))
                synchronized(pcsLock) {
                    savePcs(pcs.filter { it.id != pc.id } + pc, pc.id)
                    factory = null
                    preferredHost = 0
                }
                return null
            } catch (e: javax.net.ssl.SSLException) {
                problem = PairFailure.WrongCertificate(link.pc)
            } catch (e: Exception) {
                // try the next address
            }
        }
        return problem
    }

    private fun pinnedFactory(pin: String): SSLSocketFactory {
        val tm = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = throw CertificateException()
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                val fp = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded).joinToString("") { "%02x".format(it) }
                if (!fp.equals(pin, ignoreCase = true)) throw CertificateException("certificate does not match the paired PC")
            }
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf()
        }
        return SSLContext.getInstance("TLS").apply { init(null, arrayOf(tm), SecureRandom()) }.socketFactory
    }

    companion object {
        private const val USB_BASE = "http://127.0.0.1:8765"
        /** The agent sends "state" every second, so a silent connection for this long is dead. */
        private const val STREAM_READ_TIMEOUT = 8000
    }
}

class TrackpadSocket(private val link: AgentLink) {
    @Volatile private var socket: java.net.Socket? = null
    @Volatile private var out: java.io.OutputStream? = null
    @Volatile private var connecting = false
    private val lock = Any()
    private val random = java.util.Random()

    fun send(type: String, dx: Float, dy: Float, delta: Float): Boolean {
        synchronized(lock) {
            val s = socket
            val o = out
            if (s != null && o != null && !s.isClosed) {
                try {
                    val json = """{"type":"$type","dx":$dx,"dy":$dy,"delta":$delta}"""
                    val payload = json.toByteArray(Charsets.UTF_8)
                    if (payload.size > 125) return false
                    val mask = ByteArray(4).also { random.nextBytes(it) }
                    val frame = ByteArray(6 + payload.size)
                    frame[0] = 0x81.toByte()
                    frame[1] = (0x80 or payload.size).toByte()
                    frame[2] = mask[0]
                    frame[3] = mask[1]
                    frame[4] = mask[2]
                    frame[5] = mask[3]
                    for (i in payload.indices) {
                        frame[6 + i] = (payload[i].toInt() xor mask[i % 4].toInt()).toByte()
                    }
                    o.write(frame)
                    o.flush()
                    return true
                } catch (_: Exception) {
                    close()
                }
            }
        }
        ensureConnected()
        return false
    }

    @Volatile private var lastAttempt = 0L

    fun ensureConnected() {
        if (connecting || link.route == AgentLink.Route.USB) return
        val now = System.currentTimeMillis()
        if (now - lastAttempt < 3000L) return
        val pc = link.activePc() ?: return
        connecting = true
        lastAttempt = now
        Thread({
            try {
                connect(pc)
            } finally {
                connecting = false
            }
        }, "trackpad-ws").start()
    }

    private fun b64(b: ByteArray): String {
        val t = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
        val sb = java.lang.StringBuilder()
        var i = 0
        while (i < b.size) {
            val b0 = b[i++].toInt() and 0xFF
            if (i < b.size) {
                val b1 = b[i++].toInt() and 0xFF
                if (i < b.size) {
                    val b2 = b[i++].toInt() and 0xFF
                    sb.append(t[b0 ushr 2])
                    sb.append(t[((b0 and 3) shl 4) or (b1 ushr 4)])
                    sb.append(t[((b1 and 0xF) shl 2) or (b2 ushr 6)])
                    sb.append(t[b2 and 0x3F])
                } else {
                    sb.append(t[b0 ushr 2])
                    sb.append(t[((b0 and 3) shl 4) or (b1 ushr 4)])
                    sb.append(t[(b1 and 0xF) shl 2])
                    sb.append('=')
                }
            } else {
                sb.append(t[b0 ushr 2])
                sb.append(t[(b0 and 3) shl 4])
                sb.append("==")
            }
        }
        return sb.toString()
    }

    private fun connect(pc: AgentLink.Pc) {
        val hosts = pc.hosts
        val port = pc.port
        val token = pc.token
        val pin = pc.id
        val f = link.getPinnedFactory(pin)

        for (host in hosts) {
            var raw: java.net.Socket? = null
            try {
                raw = f.createSocket()
                raw.tcpNoDelay = true
                raw.connect(java.net.InetSocketAddress(host, port), 2000)
                val s = raw as javax.net.ssl.SSLSocket
                s.soTimeout = 3000
                s.startHandshake()

                val nonce = ByteArray(16).also { random.nextBytes(it) }
                val secKey = b64(nonce)
                val sha = java.security.MessageDigest.getInstance("SHA-1").digest((secKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray(Charsets.US_ASCII))
                val expectedAccept = b64(sha)

                val writer = java.io.BufferedWriter(java.io.OutputStreamWriter(s.outputStream, Charsets.US_ASCII))
                writer.write("GET /api/trackpad HTTP/1.1\r\n")
                writer.write("Host: $host:$port\r\n")
                writer.write("Upgrade: websocket\r\n")
                writer.write("Connection: Upgrade\r\n")
                writer.write("Sec-WebSocket-Key: $secKey\r\n")
                writer.write("Sec-WebSocket-Version: 13\r\n")
                writer.write("Authorization: Bearer $token\r\n\r\n")
                writer.flush()

                val reader = java.io.BufferedReader(java.io.InputStreamReader(s.inputStream, Charsets.US_ASCII))
                val status = reader.readLine() ?: throw java.io.IOException("No status")
                if (!status.contains("101")) throw java.io.IOException("Upgrade failed: $status")

                var hasAccept = false
                while (true) {
                    val line = reader.readLine() ?: break
                    if (line.isEmpty()) break
                    if (line.startsWith("Sec-WebSocket-Accept:", ignoreCase = true) && line.substringAfter(':').trim() == expectedAccept) {
                        hasAccept = true
                    }
                }
                if (!hasAccept) throw java.io.IOException("Sec-WebSocket-Accept mismatch")
                s.soTimeout = 0

                synchronized(lock) {
                    if (link.activePc()?.id != pin) {
                        try { s.close() } catch (_: Exception) {}
                        return
                    }
                    socket = s
                    out = s.outputStream
                }
                Thread({
                    try {
                        val input = s.inputStream
                        val buf = ByteArray(64)
                        while (input.read(buf) != -1) { /* ignore incoming websocket frames */ }
                    } catch (_: Exception) {}
                    close(s)
                }, "trackpad-ws-in").start()
                return
            } catch (_: Exception) {
                try { raw?.close() } catch (_: Exception) {}
            }
        }
    }

    fun close(target: java.net.Socket? = null) {
        val s = socket
        if (target == null || s === target) {
            socket = null
            out = null
            try { s?.close() } catch (_: Exception) {}
        }
    }
}
