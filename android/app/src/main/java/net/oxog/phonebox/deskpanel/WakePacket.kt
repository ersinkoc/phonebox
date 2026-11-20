package net.oxog.phonebox.deskpanel

import org.json.JSONArray

/**
 * An IPv4 address of this phone on the local network (Wi-Fi or Ethernet), the broadcast address of its subnet, the
 * interface ([name], such as wlan0) that holds it and the length of its subnet prefix (24 for 255.255.255.0).
 */
internal class LanLink(val address: java.net.Inet4Address, val broadcast: java.net.InetAddress?, val name: String, val prefixLength: Int) {
    /** True when [other] is on this link's subnet. */
    fun contains(other: java.net.Inet4Address): Boolean {
        val mine = address.address
        val theirs = other.address
        var bits = prefixLength.coerceIn(0, 32)
        for (i in 0 until 4) {
            val mask = if (bits >= 8) 0xFF else if (bits <= 0) 0 else (0xFF shl (8 - bits)) and 0xFF
            if ((mine[i].toInt() and mask) != (theirs[i].toInt() and mask)) return false
            bits -= 8
        }
        return true
    }
}

/** A network as Android describes it: the interface it runs on (null when Android does not say) and the addresses it holds. */
internal class NetCandidate<N>(val network: N, val interfaceName: String?, val addresses: List<java.net.InetAddress>)

/** Lets each key (a computer's id) through at most once every [everyMs]; a key's turn never holds another key back. */
internal class RefreshThrottle(private val everyMs: Long) {
    private val last = HashMap<String, Long>()

    @Synchronized
    fun allow(key: String, nowMs: Long): Boolean {
        val before = last[key]
        if (before != null && nowMs - before < everyMs) return false
        last[key] = nowMs
        return true
    }
}

/** The Wake-on-LAN magic packet: six 0xFF bytes, then the target's MAC 16 times (102 bytes, UDP port 9). */
internal object WakePacket {
    const val PORT = 9

    /** AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF or AABBCCDDEEFF; null for anything else, all-zero or broadcast. */
    fun parseMac(text: String): ByteArray? {
        val hex = text.trim().replace(":", "").replace("-", "")
        if (hex.length != 12 || !hex.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) return null
        val mac = ByteArray(6) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        if (mac.all { it == 0.toByte() } || mac.all { it == 0xFF.toByte() }) return null
        return mac
    }

    fun format(mac: ByteArray): String = mac.joinToString(":") { "%02X".format(it) }

    /**
     * The MACs an agent reported, as "AA:BB:CC:DD:EE:FF": invalid entries dropped, duplicates removed, at most 4.
     * Null when there is nothing usable (no list from an older agent, an empty one from an agent whose adapter is
     * briefly not eligible, or only invalid entries): that is no new information, so the caller keeps the list it has.
     */
    fun macsFrom(list: JSONArray?): List<String>? {
        if (list == null) return null
        return (0 until list.length()).mapNotNull { parseMac(list.optString(it)) }.map { format(it) }.distinct().take(4).ifEmpty { null }
    }

    /** An IPv4 address written out as four numbers ("192.168.1.5"), never a name: a lookup here would reach for DNS. */
    private fun ipv4Literal(text: String): java.net.Inet4Address? {
        val parts = text.trim().split('.')
        if (parts.size != 4) return null
        val bytes = ByteArray(4)
        for (i in 0 until 4) {
            val n = parts[i].takeIf { it.isNotEmpty() && it.length <= 3 && it.all { c -> c in '0'..'9' } }?.toInt() ?: return null
            if (n > 255) return null
            bytes[i] = n.toByte()
        }
        return java.net.InetAddress.getByAddress(bytes) as java.net.Inet4Address
    }

    /**
     * The links, among [links], on a network the computer is on: their subnet holds one of the computer's stored
     * [hosts] (IPv4 addresses; a name or a Tailscale address is not on the local network). The packet carries the
     * computer's MAC, so it must not go out on a café's or a hotel's network.
     */
    fun linksOnOwnNetwork(hosts: List<String>, links: List<LanLink>): List<LanLink> {
        val addresses = hosts.mapNotNull { ipv4Literal(it) }
        return links.filter { link -> addresses.any { link.contains(it) } }
    }

    /**
     * Sends the packet for every MAC to the limited broadcast and to each link's subnet broadcast, 3 rounds, with
     * [pause] between rounds. Every send is tried on its own: one failing address or target never skips the rest, and
     * an interrupted pause (the app is closing) ends the burst quietly. Returns how many sends worked.
     */
    fun burst(
        macs: List<ByteArray>,
        links: List<LanLink>,
        send: (LanLink, java.net.InetAddress, ByteArray) -> Unit,
        pause: () -> Unit = { Thread.sleep(100) },
    ): Int {
        val limited = java.net.InetAddress.getByName("255.255.255.255")
        var worked = 0
        for (round in 0 until 3) {
            if (round > 0) try { pause() } catch (_: InterruptedException) { return worked }
            for (link in links) for (mac in macs) {
                val packet = build(mac)
                for (target in listOfNotNull(limited, link.broadcast)) {
                    try { send(link, target, packet); worked++ } catch (_: Exception) { /* the other targets still try */ }
                }
            }
        }
        return worked
    }

    /**
     * The network, among [networks], that runs on [link]'s interface and holds its address; null when none does.
     * The address alone is not enough: Android does not promise that an address is unique across networks, so a
     * cellular network listed first could otherwise be taken for the Wi-Fi.
     */
    fun <N> networkOwning(link: LanLink, networks: List<NetCandidate<N>>): N? =
        networks.firstOrNull { link.address in it.addresses && it.interfaceName == link.name }?.network

    fun build(mac: ByteArray): ByteArray {
        val packet = ByteArray(6 + 16 * 6) { 0xFF.toByte() }
        for (i in 0 until 16) System.arraycopy(mac, 0, packet, 6 + i * 6, 6)
        return packet
    }
}
