package net.oxog.phonebox.deskpanel

/** [UNKNOWN]: the text was sent but no answer came back (the link's 504), so it may well be on the Mac already.
 *  [PAIR_AGAIN]: the Mac refused this phone's key (401): it was paired again or got a new key. */
enum class SendResult { OK, OFF, TOO_LARGE, UNREACHABLE, UNKNOWN, PAIR_AGAIN }

/** Where the clipboard service is, for the Phone bridge screen. Kept in memory: it describes this process only. */
enum class ClipboardState { STOPPED, WAITING, RUNNING, MAC_OFF, UNSUPPORTED, PAIR_AGAIN }

/**
 * The phone bridge's pure helpers. No Android types, so tools/test-bridge-checks.py runs them on a plain JVM. The small
 * JSON here covers only what the agent sends and takes: flat objects of strings and numbers.
 */
object BridgeClip {
    /** The most text the agent takes (UTF-8 bytes); longer text is refused with 413, so it is not even sent. */
    const val MAX_BYTES = 64 * 1024

    /** Why a clip is not sent: there is nothing to send (empty, or private), or it is over the limit. */
    enum class Skip { NOTHING, TOO_LARGE }

    /** Null when the clip may be sent. A sensitive clip (a password manager's) counts as nothing, whatever its size. */
    fun skipReason(text: String?, sensitive: Boolean): Skip? = when {
        text.isNullOrBlank() || sensitive -> Skip.NOTHING
        text.toByteArray(Charsets.UTF_8).size > MAX_BYTES -> Skip.TOO_LARGE
        else -> null
    }

    /** Whether to send this clip: not empty, not marked sensitive (a password manager's), and within the limit. */
    fun shouldSend(text: String?, sensitive: Boolean): Boolean = skipReason(text, sensitive) == null

    /** The text of a bridge "clipboard" event (data is {"seq":n,"text":"..."}); null for any other event or a bad frame. */
    fun parseEvent(event: String, data: String): String? {
        if (event != "clipboard") return null
        val text = Json(data).parseObject()?.get("text") as? String
        return text?.takeIf { it.isNotEmpty() }
    }

    /** The body of POST /api/bridge/clipboard. */
    fun requestBody(text: String): String = buildString(text.length + 12) {
        append("{\"text\":\"")
        for (c in text) when {
            c == '"' -> append("\\\"")
            c == '\\' -> append("\\\\")
            c == '\n' -> append("\\n")
            c == '\r' -> append("\\r")
            c == '\t' -> append("\\t")
            c < ' ' -> append("\\u").append(c.code.toString(16).padStart(4, '0'))
            else -> append(c)
        }
        append("\"}")
    }

    /**
     * What a failed attempt to open the stream tells the screen: 403 is the Mac's switch, 404 or 501 an agent that has no
     * bridge (an older one, or a Windows or Linux one), 401 a key the Mac no longer accepts (it was paired again, or got a
     * new key: waiting would never end), anything else (no route, 5xx) just means waiting.
     */
    fun stateAfterFailure(code: Int?): ClipboardState = when (code) {
        401 -> ClipboardState.PAIR_AGAIN
        403 -> ClipboardState.MAC_OFF
        404, 501 -> ClipboardState.UNSUPPORTED
        else -> ClipboardState.WAITING
    }

    /** What [ClipboardService.sync] does for the switch's state: [wasLive] is whether the bridge's pieces were switched on before. */
    enum class SyncAction { START, CLEAN_UP, NOTHING }

    /** A phone whose bridge was never turned on (or has been cleaned up since) does nothing at all, however often this is asked. */
    fun syncAction(switchOn: Boolean, wasLive: Boolean): SyncAction = when {
        switchOn -> SyncAction.START
        wasLive -> SyncAction.CLEAN_UP
        else -> SyncAction.NOTHING
    }

    /**
     * Whether the panel coming back to the front starts the stream over. Yes with no stream, or one still connecting or
     * backing off after "unreachable" (the phone may have just come back to the Wi-Fi); no for a connected one, and no for
     * the refusals that retry on their own once a minute (the Mac's switch is off, the phone must be paired again, the agent
     * has no bridge): starting over each time would skip that wait.
     */
    fun restartOnResume(hasStream: Boolean, state: ClipboardState): Boolean = !hasStream || state == ClipboardState.WAITING

    /** A definitive refusal means the first agent did not write the clipboard. */
    fun canFallBackFromUsb(code: Int): Boolean = code == 403 || code == 404 || code == 405 || code == 501

    /** What the agent's answer to a send means. */
    fun resultFor(code: Int): SendResult = when (code) {
        200 -> SendResult.OK
        401 -> SendResult.PAIR_AGAIN
        403 -> SendResult.OFF
        413 -> SendResult.TOO_LARGE
        // AgentLink's answer when the request went out and the reply was lost: the Mac may have the text already
        504 -> SendResult.UNKNOWN
        else -> SendResult.UNREACHABLE
    }

    /** How much of a shared text the confirmation shows. The whole text is sent; this only keeps the dialog small. */
    const val PREVIEW_CHARS = 600

    /** How long Send to Mac stays disabled after the confirmation appears: a tap meant for what was on screen before cannot confirm it. */
    const val CONFIRM_DELAY_MS = 500L

    /** What the confirmation shows: [shown], and what [shown] leaves out ([moreChars] characters on [moreLines] lines; 0 when nothing). */
    class Preview(val shown: String, val moreChars: Int, val moreLines: Int)

    /**
     * What the owner sees before Send to Mac, for a text any app can hand over. The whole text is sent, so the preview must not let
     * a sender hide the end of it behind its start: runs of spaces and blank lines are collapsed (600 newlines would push the real
     * text out of sight), characters that draw nothing or reorder the text (zero-width, right-to-left overrides) are left out, and
     * when the text is longer than [max] the rest is counted, so the dialog can say plainly that there is more. Never cut inside a
     * surrogate pair (half an emoji would show as a box). Only for display: it is not sent or logged.
     */
    fun preview(text: String, max: Int = PREVIEW_CHARS): Preview {
        val tidy = tidy(text)
        if (tidy.length <= max) return Preview(tidy, 0, 0)
        var end = max
        if (end > 0 && Character.isHighSurrogate(tidy[end - 1]) && Character.isLowSurrogate(tidy[end])) end--
        val rest = tidy.substring(end)
        return Preview(tidy.substring(0, end).trimEnd() + "…", rest.length, rest.lineSequence().count { it.isNotBlank() })
    }

    /** Letters and symbols that draw as empty space but are neither whitespace nor invisible formatting: Hangul fillers, the braille blank. */
    private val BLANK_LOOKING = intArrayOf(0x115F, 0x1160, 0x3164, 0xFFA0, 0x2800).toSet()

    /** One line break for any run of blank lines, one space for any run of spaces, nothing for invisible characters, no edges. */
    private fun tidy(text: String): String {
        val out = StringBuilder(minOf(text.length, 4096))
        var pendingSpace = false
        var pendingBreak = false
        var i = 0
        while (i < text.length) {
            val cp = text.codePointAt(i)
            i += Character.charCount(cp)
            when {
                cp == '\n'.code || cp == '\r'.code || cp == 0x2028 || cp == 0x2029 || cp == 0x85 -> pendingBreak = true
                Character.isWhitespace(cp) || Character.isSpaceChar(cp) || cp in BLANK_LOOKING -> pendingSpace = true
                Character.getType(cp) == Character.FORMAT.toInt() -> {} // zero-width and direction controls: they draw nothing, or draw the text out of order
                else -> {
                    if (out.isNotEmpty()) { if (pendingBreak) out.append('\n') else if (pendingSpace) out.append(' ') }
                    pendingSpace = false; pendingBreak = false
                    out.appendCodePoint(cp)
                }
            }
        }
        return out.toString()
    }

    /** A strict reader for one flat JSON object; any problem makes [parseObject] return null. */
    private class Json(private val s: String) {
        private var i = 0

        fun parseObject(): Map<String, Any?>? = try {
            ws(); expect('{'); ws()
            val out = HashMap<String, Any?>()
            if (peek() == '}') i++
            else while (true) {
                ws(); val key = string(); ws(); expect(':'); ws()
                out[key] = value(); ws()
                when (next()) { ',' -> continue; '}' -> break; else -> fail() }
            }
            ws(); if (i != s.length) fail()
            out
        } catch (_: IllegalArgumentException) { null }

        private fun fail(): Nothing = throw IllegalArgumentException()
        private fun peek(): Char = if (i < s.length) s[i] else fail()
        private fun next(): Char = peek().also { i++ }
        private fun expect(c: Char) { if (next() != c) fail() }
        private fun ws() { while (i < s.length && s[i] in " \t\r\n") i++ }

        private fun value(): Any? = when (peek()) {
            '"' -> string()
            't' -> literal("true", true)
            'f' -> literal("false", false)
            'n' -> literal("null", null)
            else -> number()
        }

        private fun literal(word: String, v: Any?): Any? {
            if (!s.startsWith(word, i)) fail()
            i += word.length
            return v
        }

        private fun number(): Double {
            val start = i
            while (i < s.length && s[i] in "+-.eE0123456789") i++
            return s.substring(start, i).toDoubleOrNull() ?: fail()
        }

        private fun string(): String {
            expect('"')
            val sb = StringBuilder()
            while (true) {
                val c = next()
                when {
                    c == '"' -> return sb.toString()
                    c < ' ' -> fail()
                    c != '\\' -> sb.append(c)
                    else -> when (val e = next()) {
                        '"', '\\', '/' -> sb.append(e)
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000c')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            if (i + 4 > s.length) fail()
                            sb.append(s.substring(i, i + 4).toIntOrNull(16)?.toChar() ?: fail())
                            i += 4
                        }
                        else -> fail()
                    }
                }
            }
        }
    }
}
