package net.oxog.phonebox.deskpanel

import java.io.InputStream
import java.util.concurrent.CopyOnWriteArrayList

// Network boundary only: AgentStream.kt is compiled directly from production.
// [code] is the HTTP status of each attempt (200 unless a check wants a refusal), [offline] makes an attempt find no
// route at all (the production link answers null then), [route] is the route each attempt reports; [paths], [timeouts]
// and [headers] record what was asked for. [computer] is which computer each attempt reaches (the production link
// answers with the pinned certificate of the one it connected to); the Last-Event-ID an attempt sends is the one the
// caller keeps for that computer.
class AgentLink(
    private val code: () -> Int = { 200 },
    private val offline: () -> Boolean = { false },
    private val route: () -> Route = { Route.USB },
    private val computer: () -> String = { "mac" },
    private val next: () -> InputStream,
) {
    class Reply(val code: Int, val body: InputStream)
    enum class Route { USB, WIFI }
    class Stream(val route: Route, val reply: Reply, val computer: String)
    val paths = CopyOnWriteArrayList<String>()
    val timeouts = CopyOnWriteArrayList<Int>()
    val headers = CopyOnWriteArrayList<Map<String, String>>()
    fun stream(path: String, readTimeoutMs: Int = STREAM_READ_TIMEOUT, resumeFor: (String) -> String? = { null }): Stream? {
        paths.add(path)
        timeouts.add(readTimeoutMs)
        if (offline()) { headers.add(emptyMap()); return null }
        val reached = computer()
        headers.add(resumeFor(reached)?.let { mapOf("Last-Event-ID" to it) } ?: emptyMap())
        return Stream(route(), Reply(code(), next()), reached)
    }
    companion object { const val STREAM_READ_TIMEOUT = 8000 }
}
