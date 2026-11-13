package net.oxog.phonebox.deskpanel

import android.content.Context
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
class AgentLink(context: Context) {

    class Reply(val code: Int, val contentType: String?, val body: InputStream)

    enum class Route { USB, WIFI, NONE }

    @Volatile var route: Route = Route.NONE
        private set

    private val prefs = context.getSharedPreferences("pairing", Context.MODE_PRIVATE)

    /** A computer this phone is paired with; the certificate fingerprint is its id. */
    private class Pc(val id: String, val name: String, val hosts: List<String>, val port: Int, val token: String) {
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

    private fun savePcs(list: List<Pc>, active: String?) {
        pcs = list
        activeId = active
        prefs.edit().putString("pcs", JSONArray().apply { list.forEach { put(it.json()) } }.toString()).putString("active", active).apply()
    }

    private fun activePc(): Pc? = pcs.firstOrNull { it.id == activeId } ?: pcs.firstOrNull()

    /** The paired computers for the panel: [{"id", "name", "active"}]. */
    fun pcsJson(): String {
        val active = activePc()?.id
        return JSONArray().apply { pcs.forEach { put(JSONObject().put("id", it.id).put("name", it.name).put("active", it.id == active)) } }.toString()
    }

    /** Makes another paired computer the one used over Wi-Fi. */
    fun select(id: String): Boolean {
        if (pcs.none { it.id == id }) return false
        savePcs(pcs, id)
        factory = null
        preferredHost = 0
        return true
    }

    fun forget(id: String) {
        val rest = pcs.filter { it.id != id }
        savePcs(rest, if (activeId == id) rest.firstOrNull()?.id else activeId)
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
    fun request(method: String, pathAndQuery: String): Reply {
        usb(method, pathAndQuery)?.let {
            route = Route.USB
            maybePair()
            return it
        }
        wifi(method, pathAndQuery)?.let {
            route = Route.WIFI
            return it
        }
        route = Route.NONE
        return Reply(503, "application/json", ByteArrayInputStream("""{"error":"offline"}""".toByteArray()))
    }

    private fun usb(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? = try {
        val c = URL("$USB_BASE$pathAndQuery").openConnection() as HttpURLConnection
        c.connectTimeout = 400 // refused instantly when no adb reverse tunnel exists
        send(c, method, readTimeout)
    } catch (e: Exception) {
        null
    }

    private fun wifi(method: String, pathAndQuery: String, readTimeout: Int = 4000): Reply? {
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
                val r = send(c, method, readTimeout)
                preferredHost = i
                return r
            } catch (e: Exception) {
                // try the next address
            }
        }
        return null
    }

    private fun send(c: HttpURLConnection, method: String, readTimeout: Int = 4000): Reply {
        // every POST acts on the computer (a container restart, a Home Assistant switch, a macro): none may run twice
        val controlPost = method == "POST"
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
            c.setFixedLengthStreamingMode(0)
            c.outputStream.close()
        }
        val code = try { c.responseCode } catch (e: java.io.IOException) {
            if (!controlPost) throw e
            // The command body was already submitted. Trying the Wi-Fi route now could run a macro or toggle twice.
            c.disconnect()
            return Reply(504, "application/json", ByteArrayInputStream("""{"ok":false,"error":"The command result is unknown; check the computer before retrying"}""".toByteArray()))
        }
        val body = (if (code >= 400) c.errorStream else c.inputStream) ?: ByteArrayInputStream(ByteArray(0))
        return Reply(code, c.contentType, body)
    }

    /** While on USB, refresh the Wi-Fi pairing data about once a minute. */
    private fun maybePair() {
        val now = System.currentTimeMillis()
        if (now - lastPair < 60_000) return
        lastPair = now
        background.execute {
            try {
                val c = URL("$USB_BASE/api/pair").openConnection() as HttpURLConnection
                c.connectTimeout = 800
                c.readTimeout = 3000
                val j = JSONObject(c.inputStream.bufferedReader().readText())
                val name = j.optString("pc")
                if (j.optBoolean("enabled")) {
                    // add or refresh this computer and make it the active one
                    val hosts = j.getJSONArray("hosts")
                    val pc = Pc(j.getString("fingerprint"), name, (0 until hosts.length()).map { hosts.getString(it) }, j.getInt("port"), j.getString("token"))
                    savePcs(pcs.filter { it.id != pc.id && !(name.isNotEmpty() && it.name == name) } + pc, pc.id)
                } else if (name.isNotEmpty()) {
                    // Wi-Fi was turned off on this computer: forget its pairing, keep the others
                    val rest = pcs.filter { it.name != name }
                    if (rest.size != pcs.size) savePcs(rest, rest.firstOrNull { it.id == activeId }?.id ?: rest.firstOrNull()?.id)
                }
            } catch (_: Exception) {
            }
        }
    }

    fun pairedPc(): String? = activePc()?.name

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
