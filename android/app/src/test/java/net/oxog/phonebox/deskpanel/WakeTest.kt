package net.oxog.phonebox.deskpanel

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WakeTest {
    private val mac = byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0xDD.toByte(), 0xEE.toByte(), 0x01)

    @Test
    fun packetIsSixFFThenTheMacSixteenTimes() {
        val p = WakePacket.build(mac)
        assertEquals(102, p.size)
        for (i in 0 until 6) assertEquals(0xFF.toByte(), p[i])
        for (i in 0 until 16) assertArrayEquals(mac, p.copyOfRange(6 + i * 6, 12 + i * 6))
    }

    @Test
    fun aMacIsAcceptedInEveryCommonFormat() {
        assertArrayEquals(mac, WakePacket.parseMac("AA:BB:CC:DD:EE:01"))
        assertArrayEquals(mac, WakePacket.parseMac("aa-bb-cc-dd-ee-01"))
        assertArrayEquals(mac, WakePacket.parseMac("AABBCCDDEE01"))
        assertArrayEquals(mac, WakePacket.parseMac("  AA:BB:CC:DD:EE:01 "))
    }

    @Test
    fun aBadMacIsRejected() {
        for (bad in listOf("", "AA:BB:CC:DD:EE", "AA:BB:CC:DD:EE:01:02", "GG:BB:CC:DD:EE:01", "00:00:00:00:00:00", "FF:FF:FF:FF:FF:FF"))
            assertNull(bad, WakePacket.parseMac(bad))
    }

    @Test
    fun macsFromTheAgentAreCleanedDeduplicatedAndCappedAtFour() {
        val a = JSONArray(listOf("aa-bb-cc-dd-ee-01", "junk", "AA:BB:CC:DD:EE:01", "AABBCCDDEE02", "AA:BB:CC:DD:EE:03", "AA:BB:CC:DD:EE:04", "AA:BB:CC:DD:EE:05"))
        assertEquals(listOf("AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02", "AA:BB:CC:DD:EE:03", "AA:BB:CC:DD:EE:04"), WakePacket.macsFrom(a))
    }

    // An agent whose adapter is briefly not eligible answers with an empty list: that is no new information, and must
    // never replace (wipe) the list already stored for the computer.
    @Test
    fun aReportWithoutUsableMacsIsNoNewInformation() {
        assertNull(WakePacket.macsFrom(null))
        assertNull(WakePacket.macsFrom(JSONArray()))
        assertNull(WakePacket.macsFrom(JSONArray(listOf("junk", "00:00:00:00:00:00"))))
    }

    private fun lan(address: String, prefix: Int, name: String = "wlan0") =
        LanLink(java.net.InetAddress.getByName(address) as java.net.Inet4Address, null, name, prefix)

    // The packet carries the computer's MAC: it may only go out on a network the computer is on.
    @Test
    fun aLinkIsUsedWhenItsSubnetHoldsAStoredAddressOfTheComputer() {
        val wifi = lan("192.168.1.20", 24)
        assertEquals(listOf(wifi), WakePacket.linksOnOwnNetwork(listOf("192.168.1.5"), listOf(wifi)))
        assertEquals(listOf(wifi), WakePacket.linksOnOwnNetwork(listOf("100.101.1.2", "192.168.1.5"), listOf(wifi))) // any address will do
    }

    @Test
    fun aStrangersNetworkIsNotUsedEvenWhenItIsPrivate() {
        // a café or hotel: another private network, the computer's own addresses are elsewhere
        assertEquals(emptyList<LanLink>(), WakePacket.linksOnOwnNetwork(listOf("192.168.1.5"), listOf(lan("192.168.2.20", 24))))
        assertEquals(emptyList<LanLink>(), WakePacket.linksOnOwnNetwork(listOf("192.168.1.5"), listOf(lan("10.0.0.7", 8))))
    }

    @Test
    fun theSubnetMaskDecidesNotTheFirstThreeBytes() {
        assertEquals(1, WakePacket.linksOnOwnNetwork(listOf("10.0.200.1"), listOf(lan("10.0.5.9", 16))).size)
        assertEquals(0, WakePacket.linksOnOwnNetwork(listOf("10.0.200.1"), listOf(lan("10.0.5.9", 24))).size)
        assertEquals(0, WakePacket.linksOnOwnNetwork(listOf("10.0.5.10"), listOf(lan("10.0.5.9", 32))).size)
        assertEquals(1, WakePacket.linksOnOwnNetwork(listOf("10.0.5.9"), listOf(lan("10.0.5.9", 32))).size)
    }

    @Test
    fun tailscaleAddressesAndNamesAreNotOnTheLocalNetwork() {
        val wifi = lan("192.168.1.20", 24)
        assertEquals(emptyList<LanLink>(), WakePacket.linksOnOwnNetwork(listOf("100.101.1.2", "my-pc.local", "fe80::1", "300.1.1.1"), listOf(wifi)))
        assertEquals(emptyList<LanLink>(), WakePacket.linksOnOwnNetwork(emptyList(), listOf(wifi)))
    }

    private val link = LanLink(java.net.InetAddress.getByName("192.168.1.20") as java.net.Inet4Address, java.net.InetAddress.getByName("192.168.1.255"), "wlan0", 24)
    private val two = listOf(mac, byteArrayOf(0xAA.toByte(), 0xBB.toByte(), 0xCC.toByte(), 0xDD.toByte(), 0xEE.toByte(), 0x02))

    @Test
    fun aFailingSendNeverSkipsTheOtherTargetsMacsOrRounds() {
        var attempts = 0
        val sent = WakePacket.burst(two, listOf(link), send = { _, _, _ -> if (attempts++ == 0) throw java.io.IOException("first one fails") }, pause = {})
        assertEquals(12, attempts) // 2 MACs x (subnet + limited broadcast) x 3 rounds
        assertEquals(11, sent)
    }

    private fun ip(s: String) = java.net.InetAddress.getByName(s)

    private fun net(name: String, iface: String?, vararg addresses: String) = NetCandidate(name, iface, addresses.map { ip(it) })

    @Test
    fun theNetworkThatOwnsTheWifiAddressOnTheWifiInterfaceIsFoundAmongTheOthers() {
        val networks = listOf(net("mobile", "rmnet0", "10.20.30.40", "fe80::1"), net("wifi", "wlan0", "192.168.1.20"))
        assertEquals("wifi", WakePacket.networkOwning(link, networks))
    }

    // Android does not promise that an address is unique across networks: cellular may list the Wi-Fi address too
    @Test
    fun aNetworkOnAnotherInterfaceIsNotTheWifiEvenWhenItListsTheSameAddressFirst() {
        val networks = listOf(net("mobile", "rmnet0", "192.168.1.20"), net("wifi", "wlan0", "192.168.1.20"))
        assertEquals("wifi", WakePacket.networkOwning(link, networks))
    }

    @Test
    fun noNetworkIsReturnedWhenTheAddressOrTheInterfaceDoesNotMatch() {
        assertNull(WakePacket.networkOwning(link, listOf(net("wifi", "wlan0", "192.168.1.99"))))
        assertNull(WakePacket.networkOwning(link, listOf(net("mobile", "rmnet0", "192.168.1.20"))))
        assertNull(WakePacket.networkOwning(link, listOf(net("unknown", null, "192.168.1.20"))))
        assertNull(WakePacket.networkOwning<String>(link, emptyList()))
    }

    @Test
    fun aBurstPausesBetweenRoundsAndStopsQuietlyWhenInterrupted() {
        var pauses = 0
        WakePacket.burst(listOf(mac), listOf(link), send = { _, _, _ -> }, pause = { pauses++ })
        assertEquals(2, pauses)
        var attempts = 0
        val sent = WakePacket.burst(listOf(mac), listOf(link), send = { _, _, _ -> attempts++ }, pause = { throw InterruptedException() })
        assertEquals(2, attempts) // only the first round
        assertEquals(2, sent)
    }

    private fun storedComputer(): JSONObject = JSONObject().put("id", "fp").put("name", "Desk")
        .put("hosts", JSONArray(listOf("192.168.1.5"))).put("port", 8766).put("token", "t")

    @Test
    fun aComputerStoredByAnOlderVersionWithoutMacsStillLoads() {
        val pc = AgentLink.Pc.from(storedComputer())
        assertTrue(pc.macs.isEmpty())
        assertEquals("Desk", pc.name)
    }

    // One timestamp for all computers would let A's refresh hold B back for the whole interval, and B could then answer over
    // Wi-Fi, go to sleep and never have its MACs stored.
    @Test
    fun theMacRefreshIsThrottledPerComputer() {
        val throttle = RefreshThrottle(600_000)
        assertTrue(throttle.allow("a", 1_000))
        assertFalse(throttle.allow("a", 2_000))
        assertTrue(throttle.allow("b", 2_000)) // another computer is not held back by the first
        assertFalse(throttle.allow("b", 3_000))
        assertFalse(throttle.allow("a", 600_999)) // still inside a's interval
        assertTrue(throttle.allow("a", 601_000)) // a may refresh again once the interval has passed
    }

    @Test
    fun macsSurviveASaveAndLoad() {
        val pc = AgentLink.Pc("fp", "Desk", listOf("192.168.1.5"), 8766, "t", listOf("AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02"))
        assertEquals(listOf("AA:BB:CC:DD:EE:01", "AA:BB:CC:DD:EE:02"), AgentLink.Pc.from(pc.json()).macs)
    }
}
