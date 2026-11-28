package net.oxog.phonebox.deskpanel

import android.os.SystemClock
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import javax.net.ssl.SSLSocket
import javax.net.ssl.SSLSocketFactory

/**
 * Whether the USB tunnel answers, found out with a short TLS handshake before a request is sent over it (#750). With the
 * cable out, the adb daemon on some phones keeps accepting on the tunnel's port and then says nothing, so every request
 * and every reopen of the live stream used to wait out its whole read timeout (8 s for the stream) before Wi-Fi was tried.
 */
internal object UsbProbe {
    enum class Result {
        /** The handshake completed with the pinned certificate: the tunnel leads to the paired agent. */
        REACHABLE,
        /** Nothing listens (no tunnel): as cheap to find out again next time. */
        REFUSED,
        /** Accepted, then silent, closed or not the paired agent: not worth a request for a while. */
        STALLED,
    }

    fun check(factory: SSLSocketFactory, host: String, port: Int, connectMs: Int = 400, handshakeMs: Int = 1500): Result {
        val raw = Socket()
        try {
            try {
                raw.connect(InetSocketAddress(host, port), connectMs)
            } catch (_: SocketTimeoutException) {
                return Result.STALLED
            } catch (_: Exception) {
                return Result.REFUSED
            }
            return try {
                raw.soTimeout = handshakeMs
                (factory.createSocket(raw, host, port, false) as SSLSocket).use { it.startHandshake() }
                Result.REACHABLE
            } catch (_: Exception) {
                Result.STALLED
            }
        } finally {
            try { raw.close() } catch (_: Exception) {}
        }
    }
}

/**
 * What the last USB probe or request found, so a probe is not made for every request: a working tunnel is trusted for
 * [okForMs] after its last success, a stalled one is skipped for [downForMs], and in between the next request probes.
 */
internal class UsbHealth(
    private val okForMs: Long = 10_000,
    private val downForMs: Long = 15_000,
    // elapsed time, not the wall clock: a clock set back an hour must not skip a working tunnel for that hour
    private val clock: () -> Long = { SystemClock.elapsedRealtime() },
) {
    private var okUntil = 0L
    private var downUntil = 0L

    /** true: use USB without a probe; false: skip USB; null: probe first. */
    @Synchronized fun decide(): Boolean? {
        val now = clock()
        return when {
            now < okUntil -> true
            now < downUntil -> false
            else -> null
        }
    }

    @Synchronized fun ok() { okUntil = clock() + okForMs; downUntil = 0 }
    @Synchronized fun down() { downUntil = clock() + downForMs; okUntil = 0 }
    /** A request over USB failed: the next one probes again. */
    @Synchronized fun unsure() { okUntil = 0 }
}
