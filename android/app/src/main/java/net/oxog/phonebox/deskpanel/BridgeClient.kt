package net.oxog.phonebox.deskpanel

/** Talks to the agent's /api/bridge endpoints over whichever route is up (USB, or Wi-Fi after pairing). */
class BridgeClient(private val link: AgentLink) {
    /** Sends [text] to the computer's clipboard. Never throws: a failure is [SendResult.UNREACHABLE]. */
    fun sendClipboard(text: String): SendResult = try {
        val reply = link.request("POST", "/api/bridge/clipboard", BridgeClip.requestBody(text).toByteArray(Charsets.UTF_8), "application/json")
        try { reply.body.close() } catch (_: Exception) {} // a failed close is not a failed send: the agent already answered
        BridgeClip.resultFor(reply.code)
    } catch (_: Exception) {
        SendResult.UNREACHABLE
    }
}
