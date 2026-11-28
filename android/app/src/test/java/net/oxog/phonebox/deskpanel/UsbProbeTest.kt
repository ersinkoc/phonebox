package net.oxog.phonebox.deskpanel

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import javax.net.ssl.SSLSocketFactory

/** A silent USB tunnel is found out in a moment, not after a request's whole read timeout (#750). */
class UsbProbeTest {
    private val factory = SSLSocketFactory.getDefault() as SSLSocketFactory
    private val loopback = InetAddress.getLoopbackAddress()

    @Test
    fun aPortNobodyListensOnIsRefused() {
        val port = ServerSocket(0, 1, loopback).use { it.localPort }
        assertEquals(UsbProbe.Result.REFUSED, UsbProbe.check(factory, loopback.hostAddress!!, port))
    }

    @Test
    fun aTunnelThatAcceptsAndStaysSilentIsStalledWithinTheHandshakeTime() {
        ServerSocket(0, 1, loopback).use { server ->
            // accepts (the backlog does) and never answers the TLS hello, like adb's port with the cable out
            val started = System.nanoTime()
            val result = UsbProbe.check(factory, loopback.hostAddress!!, server.localPort, handshakeMs = 300)
            val tookMs = (System.nanoTime() - started) / 1_000_000
            assertEquals(UsbProbe.Result.STALLED, result)
            assertTrue("took $tookMs ms", tookMs < 3000)
        }
    }

    @Test
    fun aTunnelThatClosesAtOnceIsStalled() {
        ServerSocket(0, 1, loopback).use { server ->
            val closer = Thread { try { server.accept().close() } catch (_: Exception) {} }.apply { start() }
            assertEquals(UsbProbe.Result.STALLED, UsbProbe.check(factory, loopback.hostAddress!!, server.localPort, handshakeMs = 2000))
            closer.join(2000)
        }
    }

    @Test
    fun aWorkingTunnelIsTrustedForAWhileAndAStalledOneSkipped() {
        var now = 1_000L
        val health = UsbHealth(okForMs = 10_000, downForMs = 15_000, clock = { now })
        assertNull(health.decide())               // nothing known yet: probe
        health.ok()
        assertEquals(true, health.decide())
        now += 10_000
        assertNull(health.decide())               // trusted for 10 s after the last success, then probed again
        health.down()
        assertEquals(false, health.decide())
        now += 14_999
        assertEquals(false, health.decide())
        now += 1
        assertNull(health.decide())               // skipped for 15 s, then probed again
        health.ok()
        health.unsure()
        assertNull(health.decide())               // a failed request: the next one probes
    }
}
