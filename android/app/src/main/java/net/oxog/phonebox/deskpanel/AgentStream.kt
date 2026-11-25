package net.oxog.phonebox.deskpanel

import java.io.Closeable

/**
 * Keeps one live connection to the agent's /api/stream (server-sent events) and hands every event to [deliver].
 * Reconnects after errors. On Wi-Fi it reconnects every minute (unless [rotateOnWifi] is false) so a freshly plugged-in
 * USB cable is preferred again. "open" / "close" pseudo-events tell the panel when to stop or resume its polling fallback.
 */
class AgentStream(
    private val link: AgentLink,
    private val path: String = "/api/stream",
    /** How long a silent connection may stay open. The panel's stream sends every second; the bridge's only pings every 15 s. */
    private val readTimeoutMs: Int = AgentLink.STREAM_READ_TIMEOUT,
    /** Wait before asking again after a failure: it starts at [retryMinMs], doubles up to [retryMaxMs], and starts over after a
     *  connection that opened. The defaults keep the panel's fixed 2 s. */
    private val retryMinMs: Long = 2000,
    private val retryMaxMs: Long = 2000,
    /** Wait after the agent answers 403 (the computer's bridge switch is off). */
    private val refusedRetryMs: Long = 2000,
    /** Called for every failed attempt with the HTTP status the agent answered, or null when it could not be reached. */
    private val onFailure: ((Int?) -> Unit)? = null,
    /** Drop a Wi-Fi stream after [wifiRotateMs] so a USB cable plugged in meanwhile is used. The panel's stream does (its
     *  data comes every second, a reconnect costs nothing); the bridge's does not: each reconnect is a TLS handshake, and
     *  what is copied in the gap would be lost. */
    private val rotateOnWifi: Boolean = true,
    private val wifiRotateMs: Long = WIFI_ROTATE_MS,
    /** Where the last SSE `id:` this stream delivered is kept, for a `Last-Event-ID` header on the next connection (the
     *  agent then replays what came in between). Null for the panel's stream: no ids are kept and no header is sent. One
     *  [EventIds] can outlive the stream, so a replacement stream in the same process resumes where this one ended. */
    private val eventIds: EventIds? = null,
    private val deliver: (event: String, data: String) -> Unit,
) {

    /**
     * The last event id received from each computer; written only for an event delivered while its stream was current. An id
     * numbers the events of one computer's agent, so it is only ever sent back to that computer: a USB cable can lead to one
     * Mac while Wi-Fi is paired with another, and the other would replay what it copied meanwhile.
     */
    class EventIds {
        private val ids = java.util.concurrent.ConcurrentHashMap<String, String>()
        fun get(computer: String): String? = ids[computer]
        fun set(computer: String, id: String) { ids[computer] = id }
        fun clear() = ids.clear()
    }

    private val gate = Any()
    @Volatile private var running = false
    // each start() / stop() advances generation; a thread from an earlier one (still connecting or closing)
    // sees the change and ends without touching replacement state or firing post-stop callbacks
    @Volatile private var generation = 0
    @Volatile private var current: Closeable? = null
    private var thread: Thread? = null

    fun start() {
        val worker: Thread
        synchronized(gate) {
            if (running) return
            running = true
            val gen = ++generation
            worker = Thread({ loop(gen) }, "agent-stream").apply { isDaemon = true }
            thread = worker
        }
        worker.start()
    }

    fun stop() {
        val active: Closeable?
        val worker: Thread?
        synchronized(gate) {
            running = false
            ++generation
            active = current
            current = null
            worker = thread
            thread = null
        }
        try { active?.close() } catch (_: Exception) {}
        worker?.interrupt()
    }

    /**
     * Delivers only while generation [gen] is current. The check and the delivery happen under [gate], so stop() waits
     * for a delivery in flight and nothing is delivered after it returns. [deliver] must not block (it only posts).
     */
    private fun dispatch(gen: Int, event: String, data: String) {
        synchronized(gate) { if (running && gen == generation) deliver(event, data) }
    }

    /**
     * The end of one frame: delivers its data, then remembers its id, both only while generation [gen] is current, so an id
     * is never kept for an event that was not handed over (the agent would not replay it). A frame with an id and no data
     * still moves the id on, as in the SSE spec.
     */
    private fun endFrame(gen: Int, computer: String, event: String, data: String?, id: String?) {
        synchronized(gate) {
            if (!running || gen != generation) return
            if (data != null) deliver(event, data)
            if (id != null) eventIds?.set(computer, id)
        }
    }

    private fun loop(gen: Int) {
        val alive = { running && gen == generation }
        var failures = 0 // in a row, for the backoff
        while (alive()) {
            var opened = false
            var failedWith: Int? = null // the HTTP status of a refused attempt; null with failed = no route
            var failed = false
            var body: Closeable? = null
            try {
                // the link asks for the id of the computer each attempt reaches (USB first, then Wi-Fi): they can differ
                val reached = link.stream(path, readTimeoutMs) { computer -> eventIds?.get(computer) } ?: run { failed = true; throw java.io.IOException("offline") }
                val route = reached.route
                val reply = reached.reply
                val computer = reached.computer
                if (reply.code != 200) {
                    failed = true
                    failedWith = reply.code
                    reply.body.close()
                    throw java.io.IOException("HTTP ${reply.code}")
                }
                val attached = synchronized(gate) {
                    if (!alive()) false
                    else {
                        body = reply.body
                        current = body
                        opened = true
                        true
                    }
                }
                if (!attached) { reply.body.close(); break }
                reply.body.bufferedReader(Charsets.UTF_8).use { r ->
                    if (!alive()) return@use
                    dispatch(gen, "open", "{\"route\":\"${route.name.lowercase()}\"}")
                    val since = System.currentTimeMillis()
                    var event = "message"
                    val data = StringBuilder()
                    var id: String? = null // this frame's, kept only once the frame is complete (a cut frame is replayed)
                    while (alive()) {
                        val line = r.readLine() ?: break
                        if (!alive()) break
                        when {
                            line.isEmpty() -> {
                                if (data.isNotEmpty() || id != null) endFrame(gen, computer, event, data.takeIf { it.isNotEmpty() }?.toString(), id)
                                event = "message"
                                data.setLength(0)
                                id = null
                                if (rotateOnWifi && route == AgentLink.Route.WIFI && System.currentTimeMillis() - since > wifiRotateMs) break
                            }
                            line.startsWith("event:") -> event = line.substring(6).trim()
                            line.startsWith("data:") -> data.append(line.substring(5).trimStart())
                            line.startsWith("id:") -> parseEventId(line)?.let { id = it }
                        }
                    }
                }
            } catch (_: Exception) {
                if (!opened) failed = true // an error before the connection was usable counts as unreachable
            } finally {
                synchronized(gate) { if (current === body) current = null } // a newer thread may own it already
            }
            if (opened) dispatch(gen, "close", "{}")
            if (opened) failures = 0
            // under the gate, like dispatch: stop() then waits for a report in flight, and none is made after it returns (a late
            // one would overwrite the state of a stopped or replacement stream). [onFailure] must not block either.
            else if (failed) synchronized(gate) { if (alive()) onFailure?.invoke(failedWith) }
            val wait = when {
                opened -> 300L
                failedWith == 403 -> refusedRetryMs
                else -> minOf(retryMaxMs, retryMinMs shl minOf(failures, 16))
            }
            if (!opened) failures++
            if (alive()) try { Thread.sleep(wait) } catch (_: InterruptedException) {}
        }
    }

    companion object {
        /** How long the panel's stream stays on Wi-Fi before it looks for USB again. */
        const val WIFI_ROTATE_MS = 60_000L

        /**
         * The value of an SSE `id:` line, or null when it is not one this app sends back: the agent numbers its events in
         * decimal, so anything else (empty, a sign, letters, more than 18 digits) is ignored and the previous id stays.
         */
        fun parseEventId(line: String): String? {
            if (!line.startsWith("id:")) return null
            val value = line.substring(3).removePrefix(" ")
            return value.takeIf { it.length in 1..18 && it.all { c -> c in '0'..'9' } }
        }
    }
}
