package net.oxog.phonebox.deskpanel

import java.io.InputStream

// Network boundary only: AgentStream.kt is compiled directly from production.
class AgentLink(private val next: () -> InputStream) {
    class Reply(val code: Int, val body: InputStream)
    enum class Route { USB, WIFI }
    fun stream(path: String): Pair<Route, Reply> {
        check(path == "/api/stream")
        return Route.USB to Reply(200, next())
    }
}
