import Foundation

/// What the WebView may ask the native proxy for. The panel talks to the agent through `/agent/*`; the shell
/// forwards those requests over the paired, pinned connection (see the Android app's MainActivity.proxy).
///
/// Two rules, copied from the Android app:
/// - a request must carry the panel's `X-PhoneBox-Panel` header (a sandboxed Custom HTML widget cannot add it
///   without a CORS preflight the proxy refuses), or
/// - it is a read-only GET for one of the panel's images (album art, process icons, …), which cannot carry headers.
///
/// Everything else — other origins, other methods, settings or pairing endpoints — is refused.
public enum PanelProxyPolicy {
    /// The panel's image endpoints, without the `/agent` prefix. Same list as MainActivity.READ_ONLY_IMAGES.
    public static let readOnlyImagePaths: Set<String> = [
        "/api/art",
        "/api/procs/icon",
        "/api/controls/radio-logo",
        "/api/apps/icon",
        "/api/discord/img",
        "/api/shortcuts/icon",
        "/api/steam/cover",
        "/api/wallpaper",
    ]

    /// Returns the agent path and query to request (e.g. `/api/state?fresh=1`), or nil when the request must be
    /// refused. `pathAndQuery` is what the WebView asked for (e.g. `/agent/api/state?fresh=1`).
    public static func agentRequest(method: String, pathAndQuery: String, hasPanelHeader: Bool) -> String? {
        let upper = method.uppercased()
        guard upper == "GET" || upper == "POST" else { return nil }
        guard pathAndQuery.hasPrefix("/agent/") else { return nil }
        guard !pathAndQuery.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7f }) else { return nil }

        let parts = pathAndQuery.split(separator: "?", maxSplits: 1, omittingEmptySubsequences: false)
        let rawPath = String(parts[0])
        let query = parts.count > 1 ? String(parts[1]) : nil

        let agentPath = String(rawPath.dropFirst("/agent".count))
        guard agentPath.hasPrefix("/"), agentPath.count > 1 else { return nil }
        guard !agentPath.contains(".."), !agentPath.contains("//"), !agentPath.contains("\\") else { return nil }

        // A percent-encoded dot or slash must not smuggle a traversal through the path. The query is left alone:
        // `/agent/api/proxy?url=https%3A%2F%2F…` is legitimate.
        let lowerPath = agentPath.lowercased()
        guard !lowerPath.contains("%2e"), !lowerPath.contains("%2f"), !lowerPath.contains("%5c") else { return nil }

        let allowed = hasPanelHeader || (upper == "GET" && readOnlyImagePaths.contains(agentPath))
        guard allowed else { return nil }

        if let query { return agentPath + "?" + query }
        return agentPath
    }
}
