package net.oxog.phonebox.deskpanel

import android.content.Context
import android.content.ContextWrapper
import android.content.SharedPreferences
import android.util.Base64
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.io.Closeable
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.Socket
import java.security.KeyFactory
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.CertificateFactory
import java.security.spec.PKCS8EncodedKeySpec
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket

/** Real platform HTTPS/pooling test: JVM lambda allocation does not reproduce Android's SAM desugaring. */
@RunWith(AndroidJUnit4::class)
class TlsConnectionReuseTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test fun repeatedWifiRequestsReuseConnections() {
        LocalAgent().use { server ->
            val link = linkTo(server, server.fingerprint)
            try {
                repeat(10) { readReply(link) }
                val before = descriptorCount()
                repeat(200) { readReply(link) }
                val after = descriptorCount()
                assertTrue("Opened ${server.connections.get()} TLS connections for 210 requests",
                    server.connections.get() <= 3)
                assertTrue("Descriptors grew from $before to $after", after <= before + 20)
            } finally { link.close() }
        }
    }

    @Test fun sharedVerifierStillRejectsAnUnpinnedCertificate() {
        LocalAgent().use { server ->
            val link = linkTo(server, "0".repeat(64))
            try { assertNull(wifi(link)) } finally { link.close() }
        }
    }

    private fun descriptorCount(): Int = File("/proc/self/fd").list()!!.size

    private fun readReply(link: AgentLink) {
        val reply = checkNotNull(wifi(link)) { "Local TLS agent was unreachable" }
        assertEquals(200, reply.code)
        assertEquals("ok", reply.body.bufferedReader().use { it.readText() })
    }

    // Bypass USB routing: a developer may have their real agent connected through adb reverse.
    private fun wifi(link: AgentLink): AgentLink.Reply? = AgentLink::class.java
        .getDeclaredMethod("wifi", String::class.java, String::class.java, Int::class.javaPrimitiveType)
        .apply { isAccessible = true }
        .invoke(link, "GET", "/api/state", 4000) as AgentLink.Reply?

    private fun linkTo(server: LocalAgent, fingerprint: String): AgentLink {
        // Keep the developer's actual pairing preferences untouched.
        val testContext = object : ContextWrapper(context) {
            override fun getSharedPreferences(name: String, mode: Int): SharedPreferences =
                super.getSharedPreferences("tls-reuse-test-$name", mode)
        }
        val pc = JSONObject().put("id", fingerprint).put("name", "Test agent")
            .put("hosts", JSONArray().put("127.0.0.1")).put("port", server.port).put("token", "test-only")
        testContext.getSharedPreferences("pairing", Context.MODE_PRIVATE).edit().clear()
            .putString("pcs", JSONArray().put(pc).toString()).putString("active", fingerprint).commit()
        return AgentLink(testContext)
    }

    private class LocalAgent : Closeable {
        val connections = AtomicInteger()
        val fingerprint: String
        private val server: SSLServerSocket
        private val sockets = CopyOnWriteArrayList<Socket>()
        private val workers = Executors.newCachedThreadPool { task ->
            Thread(task, "test-tls-agent").apply { isDaemon = true }
        }
        val port get() = server.localPort

        init {
            val assets = InstrumentationRegistry.getInstrumentation().context.assets
            val cert = assets.open("tls-test-cert.pem").use {
                CertificateFactory.getInstance("X.509").generateCertificate(it)
            }
            fingerprint = MessageDigest.getInstance("SHA-256").digest(cert.encoded)
                .joinToString("") { "%02x".format(it.toInt() and 0xff) }
            // Public test fixture only; never used by the app or a real agent.
            val pem = assets.open("tls-test-key.pem").bufferedReader().use { it.readText() }
                .lineSequence().filterNot { it.startsWith("---") }.joinToString("")
            val key = KeyFactory.getInstance("RSA").generatePrivate(
                PKCS8EncodedKeySpec(Base64.decode(pem, Base64.DEFAULT)))
            val password = "test-only".toCharArray()
            val store = KeyStore.getInstance(KeyStore.getDefaultType()).apply {
                load(null)
                setKeyEntry("test", key, password, arrayOf(cert))
            }
            val managers = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply {
                init(store, password)
            }
            server = SSLContext.getInstance("TLS").apply { init(managers.keyManagers, null, null) }
                .serverSocketFactory.createServerSocket(0, 50, InetAddress.getByName("127.0.0.1")) as SSLServerSocket
            workers.execute {
                while (!server.isClosed) {
                    val socket = try { server.accept() } catch (_: IOException) { break }
                    sockets.add(socket)
                    connections.incrementAndGet()
                    workers.execute { serve(socket) }
                }
            }
        }

        private fun serve(socket: Socket) {
            try {
                socket.use {
                    socket.soTimeout = 10000
                    val reader = socket.getInputStream().bufferedReader(Charsets.US_ASCII)
                    val output = socket.getOutputStream()
                    while (reader.readLine() != null) {
                        while (true) {
                            val line = reader.readLine() ?: return
                            if (line.isEmpty()) break
                        }
                        output.write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Type: text/plain\r\nConnection: keep-alive\r\n\r\nok"
                            .toByteArray(Charsets.US_ASCII))
                        output.flush()
                    }
                }
            } catch (_: IOException) {
                // Pool eviction and the wrong-certificate test both close connections normally.
            }
        }

        override fun close() {
            server.close()
            sockets.forEach { runCatching { it.close() } }
            workers.shutdownNow()
        }
    }
}
