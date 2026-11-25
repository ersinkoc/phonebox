import Foundation
import WebKit
import PhoneBoxKit

/// Serves the bundled panel (the www folder copied from the Android app at build time) under
/// `phonebox://localhost/www/…`, and forwards the panel's `/agent/*` images through the native client.
///
/// The panel's own `fetch()` calls are proxied by PanelProxyHandler; `fetch` to a custom scheme is refused by
/// WebKit, so the shim replaces it. Images cannot carry the panel header, so GETs for the read-only image
/// endpoints are allowed without it — exactly the exception the Android shell makes.
final class PanelSchemeHandler: NSObject, WKURLSchemeHandler {
    static let scheme = "phonebox"
    static let host = "localhost"

    private let client: AgentClient
    private let assetRoot: URL?
    // WebKit calls the scheme handler on the main thread. Asynchronous replies return there too.
    private var activeTasks = Set<ObjectIdentifier>()

    init(client: AgentClient) {
        self.client = client
        self.assetRoot = Bundle.main.resourceURL?.appendingPathComponent("www", isDirectory: true)
        super.init()
    }

    func webView(_ webView: WKWebView, start task: WKURLSchemeTask) {
        activeTasks.insert(ObjectIdentifier(task))
        guard let url = task.request.url else {
            activeTasks.remove(ObjectIdentifier(task))
            task.didFailWithError(URLError(.badURL))
            return
        }
        if url.path.hasPrefix("/agent/") {
            proxy(task, url: url)
        } else {
            serveAsset(task, url: url)
        }
    }

    func webView(_ webView: WKWebView, stop task: WKURLSchemeTask) {
        activeTasks.remove(ObjectIdentifier(task))
    }

    private func serveAsset(_ task: WKURLSchemeTask, url: URL) {
        guard let fileURL = assetURL(for: url), let data = try? Data(contentsOf: fileURL) else {
            respond(task, status: 404, contentType: "text/plain; charset=utf-8", data: Data("Not found".utf8))
            return
        }
        respond(task, status: 200, contentType: Self.mimeType(for: fileURL.path), data: data)
    }

    private func assetURL(for url: URL) -> URL? {
        guard let root = assetRoot else { return nil }
        let path = url.path
        guard path.hasPrefix("/www/") else { return nil }
        let relative = String(path.dropFirst("/www/".count))
        guard !relative.isEmpty,
              !relative.contains(".."),
              !relative.contains("\\"),
              !relative.hasPrefix("/") else { return nil }
        let file = root.appendingPathComponent(relative)
        let rootPath = root.standardizedFileURL.path
        guard file.standardizedFileURL.path.hasPrefix(rootPath + "/") else { return nil }
        return file
    }

    private func proxy(_ task: WKURLSchemeTask, url: URL) {
        let query = URLComponents(url: url, resolvingAgainstBaseURL: false)?.percentEncodedQuery.map { "?" + $0 } ?? ""
        guard let target = PanelProxyPolicy.agentRequest(
            method: "GET",
            pathAndQuery: url.path + query,
            hasPanelHeader: false
        ) else {
            respond(task, status: 403, contentType: "text/plain; charset=utf-8", data: Data())
            return
        }

        Task {
            let reply = await client.request(method: "GET", pathAndQuery: target)
            let resolved: (status: Int, contentType: String, body: Data)
            if let reply {
                if (300..<400).contains(reply.status) || !(100...599).contains(reply.status) {
                    resolved = (502, "application/json", Data("{\"error\":\"unexpected response\"}".utf8))
                } else {
                    resolved = (reply.status, reply.contentType ?? "application/octet-stream", reply.body)
                }
            } else {
                resolved = (503, "application/json", Data("{\"error\":\"offline\"}".utf8))
            }
            DispatchQueue.main.async {
                self.respond(task, status: resolved.status, contentType: resolved.contentType, data: resolved.body)
            }
        }
    }

    private func respond(_ task: WKURLSchemeTask, status: Int, contentType: String, data: Data) {
        let id = ObjectIdentifier(task)
        guard activeTasks.contains(id), let url = task.request.url else { return }

        let headers = [
            "Content-Type": contentType,
            "Content-Length": String(data.count),
            "Cache-Control": "no-cache",
        ]
        guard let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers) else {
            activeTasks.remove(id)
            task.didFailWithError(URLError(.badServerResponse))
            return
        }
        task.didReceive(response)
        guard activeTasks.contains(id) else { return }
        if !data.isEmpty { task.didReceive(data) }
        guard activeTasks.remove(id) != nil else { return }
        task.didFinish()
    }

    private static func mimeType(for path: String) -> String {
        switch (path as NSString).pathExtension.lowercased() {
        case "html", "htm": return "text/html; charset=utf-8"
        case "js", "mjs": return "text/javascript; charset=utf-8"
        case "css": return "text/css; charset=utf-8"
        case "json": return "application/json; charset=utf-8"
        case "svg": return "image/svg+xml"
        case "png": return "image/png"
        case "jpg", "jpeg": return "image/jpeg"
        case "webp": return "image/webp"
        case "ico": return "image/x-icon"
        case "woff2": return "font/woff2"
        default: return "application/octet-stream"
        }
    }
}
