import Foundation

struct AgentReply: Equatable {
    let status: Int
    let contentType: String?
    let body: Data
}

/// Talks HTTPS to the paired computer. The Bearer token and the certificate pin live only here: the WebView
/// never sees either. Hosts are tried in the order the pairing stored them.
///
/// When the active computer stops answering a GET, the other paired computers are asked in the background (at
/// most every few seconds) and the first that answers is handed to `onSwitch`. That is how a dual-boot PC works:
/// Windows and macOS run different agents with different certificates, often at the same address, and only the
/// one that is booted answers. A request is never resent to another computer: a POST may run a command.
final class AgentClient {
    private let computer: PairedComputer
    private let others: [PairedComputer]
    private let onSwitch: @MainActor (PairedComputer) -> Void
    private let pinned: PinnedSession
    private let deviceID: String
    private let appVersion: String
    private let model: String

    private let probeLock = NSLock()
    private var probing = false
    private var lastProbe = Date.distantPast
    private var invalidated = false
    /// Counts answers from the active computer. A probe started before one of them is stale: the active computer is
    /// back, so whatever the probe finds must not take its place.
    private var activeAnswers = 0
    private static let probeInterval: TimeInterval = 4

    init(computer: PairedComputer, others: [PairedComputer] = [], onSwitch: @escaping @MainActor (PairedComputer) -> Void = { _ in }) {
        self.computer = computer
        self.others = others
        self.onSwitch = onSwitch
        self.pinned = PinnedSession(pin: computer.fingerprint, timeout: 8)
        self.deviceID = DeviceIdentity.deviceID
        self.appVersion = DeviceIdentity.appVersion
        self.model = DeviceIdentity.model
    }

    /// Forwards one request. Returns nil when no address answered (the caller turns that into the panel's
    /// usual 503 "offline"). Only GET and POST are accepted.
    func request(method: String, pathAndQuery: String, body: Data? = nil, contentType: String? = nil) async -> AgentReply? {
        let reply = await send(method: method, pathAndQuery: pathAndQuery, body: body, contentType: contentType)
        if reply == nil { lookForAnotherComputer() } else { activeAnswered() }
        return reply
    }

    private func send(method: String, pathAndQuery: String, body: Data?, contentType: String?) async -> AgentReply? {
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
            identify(&request, token: computer.token)
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

    private func identify(_ request: inout URLRequest, token: String) {
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue(appVersion, forHTTPHeaderField: "X-PhoneBox-App")
        request.setValue("ios", forHTTPHeaderField: "X-PhoneBox-Platform")
        request.setValue(deviceID, forHTTPHeaderField: "X-PhoneBox-Device")
        request.setValue(model, forHTTPHeaderField: "X-PhoneBox-Model")
    }

    // MARK: - Another paired computer

    private func activeAnswered() {
        probeLock.lock()
        activeAnswers &+= 1
        probeLock.unlock()
    }

    private func lookForAnotherComputer() {
        guard !others.isEmpty else { return }
        probeLock.lock()
        guard !probing, Date().timeIntervalSince(lastProbe) >= Self.probeInterval else { probeLock.unlock(); return }
        probing = true
        let startedAt = activeAnswers
        probeLock.unlock()

        Task.detached(priority: .utility) { [self] in
            let found = await withTaskGroup(of: PairedComputer?.self) { group -> PairedComputer? in
                for candidate in others {
                    group.addTask { await self.answers(candidate) ? candidate : nil }
                }
                for await result in group {
                    if let result { group.cancelAll(); return result }
                }
                return nil
            }
            // A panel that went away meanwhile (the user picked another computer, or the pairings changed) must not be
            // overruled, and neither may an active computer that answered again while the probe ran.
            if finishProbe(startedAt: startedAt), let found { await onSwitch(found) }
        }
    }

    /// Ends a probe; false when its result must be dropped: this client was invalidated, or the active computer
    /// answered after the probe started.
    private func finishProbe(startedAt: Int) -> Bool {
        probeLock.lock()
        defer { probeLock.unlock() }
        probing = false
        lastProbe = Date()
        return !invalidated && activeAnswers == startedAt
    }

    /// Whether a paired computer answers on one of its Wi-Fi/Tailscale addresses with its own certificate and
    /// accepts its token. The USB relay is left out: it belongs to the active computer's pairing.
    private func answers(_ candidate: PairedComputer) async -> Bool {
        let session = PinnedSession(pin: candidate.fingerprint, timeout: 4)
        defer { session.invalidate() }
        for host in candidate.hosts {
            guard !Task.isCancelled,
                  let url = URL(string: "https://\(host):\(candidate.port)/api/state") else { continue }
            var request = URLRequest(url: url)
            request.timeoutInterval = 4
            identify(&request, token: candidate.token)
            if let (response, _) = try? await session.data(for: request), response.statusCode == 200 { return true }
        }
        return false
    }

    func invalidate() {
        probeLock.lock()
        invalidated = true
        probeLock.unlock()
        pinned.invalidate()
    }
}
