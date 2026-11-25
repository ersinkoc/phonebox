import Foundation

struct AgentReply: Equatable {
    let status: Int
    let contentType: String?
    let body: Data
}

/// Talks HTTPS to the paired computer. The Bearer token and the certificate pin live only here: the WebView
/// never sees either. Hosts are tried in the order the pairing stored them.
final class AgentClient {
    private let computer: PairedComputer
    private let pinned: PinnedSession
    private let deviceID: String
    private let appVersion: String
    private let model: String

    init(computer: PairedComputer) {
        self.computer = computer
        self.pinned = PinnedSession(pin: computer.fingerprint, timeout: 8)
        self.deviceID = DeviceIdentity.deviceID
        self.appVersion = DeviceIdentity.appVersion
        self.model = DeviceIdentity.model
    }

    /// Forwards one request. Returns nil when no address answered (the caller turns that into the panel's
    /// usual 503 "offline"). Only GET and POST are accepted.
    func request(method: String, pathAndQuery: String, body: Data? = nil, contentType: String? = nil) async -> AgentReply? {
        let upper = method.uppercased()
        guard upper == "GET" || upper == "POST" else { return nil }
        guard pathAndQuery.hasPrefix("/") else { return nil }

        // The cable first, then the addresses from the pairing link. The relay at 127.0.0.1:8767 only
        // listens while an authenticated USB pipe exists; otherwise the connection is refused at once and
        // the Wi-Fi address is tried with no noticeable delay.
        for endpoint in endpoints {
            guard let url = URL(string: "https://\(endpoint.host):\(endpoint.port)\(pathAndQuery)") else { continue }
            var request = URLRequest(url: url)
            request.httpMethod = upper
            // A POST can start a macro or toggle a switch: it gets the agent's long timeout, like on Android.
            request.timeoutInterval = upper == "POST" ? 30 : 8
            request.setValue("Bearer \(computer.token)", forHTTPHeaderField: "Authorization")
            request.setValue(appVersion, forHTTPHeaderField: "X-PhoneBox-App")
            request.setValue("ios", forHTTPHeaderField: "X-PhoneBox-Platform")
            request.setValue(deviceID, forHTTPHeaderField: "X-PhoneBox-Device")
            request.setValue(model, forHTTPHeaderField: "X-PhoneBox-Model")
            if let contentType { request.setValue(contentType, forHTTPHeaderField: "Content-Type") }
            if let body { request.httpBody = body }

            do {
                let (response, data) = try await pinned.data(for: request)
                let status = response.statusCode
                if !(100...599).contains(status) || (300..<400).contains(status) {
                    return AgentReply(
                        status: 502,
                        contentType: "application/json",
                        body: Data("{\"error\":\"unexpected response\"}".utf8)
                    )
                }
                return AgentReply(
                    status: status,
                    contentType: response.value(forHTTPHeaderField: "Content-Type"),
                    body: data
                )
            } catch {
                // A POST may already have run on the computer even though the answer never arrived: never send
                // the same command to another address. Only a connection that was never made is retried.
                if upper == "POST" {
                    if let urlError = error as? URLError,
                       urlError.code == .cannotConnectToHost || urlError.code == .cannotFindHost {
                        continue
                    }
                    return nil
                }
            }
        }
        return nil
    }

    /// The USB relay's loopback address, then the paired computer's Wi-Fi/Tailscale addresses.
    private var endpoints: [(host: String, port: Int)] {
        [(host: "127.0.0.1", port: Int(USBLink.proxyPort))]
            + computer.hosts.map { (host: $0, port: computer.port) }
    }

    func invalidate() {
        pinned.invalidate()
    }
}
