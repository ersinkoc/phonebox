package net.oxog.phonebox.deskpanel

import java.io.Closeable

/**
 * Keeps one live connection to the agent's /api/stream (server-sent events) and hands every event to [deliver].
 * Reconnects after errors. On Wi-Fi it reconnects every minute so a freshly plugged-in USB cable is preferred again.
 * "open" / "close" pseudo-events tell the panel when to stop or resume its polling fallback.
 */
class AgentStream(private val link: AgentLink, private val deliver: (event: String, data: String) -> Unit) {

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

    private fun loop(gen: Int) {
        val alive = { running && gen == generation }
        while (alive()) {
            var opened = false
            var body: Closeable? = null
            try {
                val (route, reply) = link.stream("/api/stream") ?: throw java.io.IOException("offline")
                if (reply.code != 200) { reply.body.close(); throw java.io.IOException("HTTP ${reply.code}") }
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
                    while (alive()) {
                        val line = r.readLine() ?: break
                        if (!alive()) break
                        when {
                            line.isEmpty() -> {
                                if (data.isNotEmpty()) dispatch(gen, event, data.toString())
                                event = "message"
                                data.setLength(0)
                                if (route == AgentLink.Route.WIFI && System.currentTimeMillis() - since > 60_000) break
                            }
                            line.startsWith("event:") -> event = line.substring(6).trim()
                            line.startsWith("data:") -> data.append(line.substring(5).trimStart())
                        }
                    }
                }
            } catch (_: Exception) {
            } finally {
                synchronized(gate) { if (current === body) current = null } // a newer thread may own it already
            }
            if (opened) dispatch(gen, "close", "{}")
            if (alive()) try { Thread.sleep(if (opened) 300 else 2000) } catch (_: InterruptedException) {}
        }
    }
}
