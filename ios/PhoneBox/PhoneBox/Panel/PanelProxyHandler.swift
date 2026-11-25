import Foundation
import WebKit
import PhoneBoxKit

/// Receives the panel's `/agent/*` fetches from PanelShim.js and runs them through AgentClient.
///
/// Only the main frame may call it, and only with the per-launch bridge key injected at document start (the
/// same defence the Android shell uses): a sandboxed Custom HTML widget iframe has neither, so it cannot use
/// the proxy to reach the agent.
final class PanelProxyHandler: NSObject, WKScriptMessageHandlerWithReply {
    static let messageName = "phoneboxProxy"

    private let client: AgentClient
    private let bridgeKey: String

    init(client: AgentClient, bridgeKey: String) {
        self.client = client
        self.bridgeKey = bridgeKey
        super.init()
    }

    func userContentController(
        _ userContentController: WKUserContentController,
        didReceive message: WKScriptMessage,
        replyHandler: @escaping (Any?, String?) -> Void
    ) {
        guard message.frameInfo.isMainFrame,
              let body = message.body as? [String: Any],
              body["key"] as? String == bridgeKey,
              let method = body["method"] as? String,
              let path = body["path"] as? String else {
            replyHandler(Self.refusal("forbidden"), nil)
            return
        }

        let headers = body["headers"] as? [String: Any] ?? [:]
        let hasPanelHeader = headers.keys.contains { $0.caseInsensitiveCompare("X-PhoneBox-Panel") == .orderedSame }
        guard let target = PanelProxyPolicy.agentRequest(method: method, pathAndQuery: path, hasPanelHeader: hasPanelHeader) else {
            replyHandler(Self.refusal("forbidden"), nil)
            return
        }

        let contentType = headers.first { $0.key.caseInsensitiveCompare("Content-Type") == .orderedSame }?.value as? String
        var payload: Data?
        if let encoded = body["body"] as? String, !encoded.isEmpty {
            guard let decoded = Data(base64Encoded: encoded), decoded.count <= 64 * 1024 else {
                replyHandler(Self.refusal("bad request"), nil)
                return
            }
            payload = decoded
        }

        Task {
            let reply = await client.request(method: method, pathAndQuery: target, body: payload, contentType: contentType)
            let result: [String: Any]
            if let reply {
                result = [
                    "status": reply.status,
                    "statusText": Self.statusText(for: reply.status),
                    "headers": ["Content-Type": reply.contentType ?? "application/octet-stream"],
                    "body": reply.body.base64EncodedString(),
                ]
            } else {
                result = [
                    "status": 503,
                    "statusText": "Service Unavailable",
                    "headers": ["Content-Type": "application/json"],
                    "body": Data("{\"error\":\"offline\"}".utf8).base64EncodedString(),
                ]
            }
            DispatchQueue.main.async { replyHandler(result, nil) }
        }
    }

    private static func refusal(_ reason: String) -> [String: Any] {
        [
            "status": 403,
            "statusText": "Forbidden",
            "headers": ["Content-Type": "application/json"],
            "body": Data("{\"error\":\"\(reason)\"}".utf8).base64EncodedString(),
        ]
    }

    private static func statusText(for status: Int) -> String {
        switch status {
        case 200: return "OK"
        case 201: return "Created"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 403: return "Forbidden"
        case 404: return "Not Found"
        case 500: return "Internal Server Error"
        case 503: return "Service Unavailable"
        default: return ""
        }
    }
}
