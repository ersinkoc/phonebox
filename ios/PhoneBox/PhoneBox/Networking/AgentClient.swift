import Foundation
import PhoneBoxKit

struct AgentReply: Equatable {
    let status: Int
    let contentType: String?
    let body: Data
}

/// Talks HTTPS to the paired computer. The Bearer token and the certificate pin live only here: the WebView
/// never sees either. Hosts are tried in the order the pairing stored them.
///
/// Several paired computers (two machines, or Windows and macOS on one dual-boot PC, which run different agents with
/// different certificates, often at the same address): `Failover` decides when to look at the others.
/// - When the computer on screen has answered nothing for 30 s, the others are asked in the background and the first
///   that answers is handed to `onSwitch`.
/// - While the panel shows another computer than the user's own pick, the pick is asked every 15 s and handed to
///   `onSwitch` once it answers again.
/// A request is never resent to another computer: a POST may run a command. `onSwitch` gets the id of the computer
/// this client serves, so a switch is applied only if the panel still shows it.
final class AgentClient {
    private let computer: PairedComputer
    private let others: [PairedComputer]
    /// The user's own pick when the panel shows another computer, else nil.
    private let preferred: PairedComputer?
    private let onSwitch: @MainActor (_ from: String, _ to: PairedComputer) -> Void
    private let pinned: PinnedSession
    private let deviceID: String
    private let appVersion: String
    private let model: String

    private let lock = NSLock()
    private var failover = Failover()
    private var probing = false
    private var checkingPreferred = false
    private var invalidated = false
    /// Counts answers from the computer on screen. A probe started before one of them is stale: the computer is back,
    /// so whatever the probe finds must not take its place.
    private var activeAnswers = 0

    init(
        computer: PairedComputer,
        others: [PairedComputer] = [],
        preferred: PairedComputer? = nil,
        onSwitch: @escaping @MainActor (_ from: String, _ to: PairedComputer) -> Void = { _, _ in }
    ) {
        self.computer = computer
        self.others = others
        self.preferred = preferred.flatMap { pick in others.first { $0.id == pick.id } }
        self.onSwitch = onSwitch
        self.pinned = PinnedSession(pin: computer.fingerprint, timeout: 8)
        self.deviceID = DeviceIdentity.deviceID
        self.appVersion = DeviceIdentity.appVersion
        self.model = DeviceIdentity.model
    }

    /// Forwards one request. Returns nil when no address answered (the caller turns that into the panel's
    /// usual 503 "offline"). Only GET and POST are accepted.
    func request(method: String, pathAndQuery: String, body: Data? = nil, contentType: String? = nil) async -> AgentReply? {
        // A request this client never sends is a mistake in the caller, not a computer that doesn't answer: it must
        // not count towards a switch.
        let upper = method.uppercased()
        guard upper == "GET" || upper == "POST", pathAndQuery.hasPrefix("/") else { return nil }
        let reply = await send(method: upper, pathAndQuery: pathAndQuery, body: body, contentType: contentType)
        if reply == nil { activeFailed() } else { activeAnswered() }
        return reply
    }

    /// `method` is GET or POST and `pathAndQuery` starts with "/" (checked by `request`).
    private func send(method upper: String, pathAndQuery: String, body: Data?, contentType: String?) async -> AgentReply? {

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
        lock.lock()
        activeAnswers &+= 1
        failover.answered()
        let pick = preferred
        let check = pick != nil && !invalidated && !checkingPreferred && failover.shouldCheckPreferred(at: Date())
        if check { checkingPreferred = true }
        lock.unlock()
        guard check, let pick else { return }

        Task.detached(priority: .utility) { [self] in
            let back = await answers(pick)
            if finishPreferredCheck(), back { await onSwitch(computer.id, pick) }
        }
    }

    /// Ends a check of the user's pick; false when this client was invalidated meanwhile.
    private func finishPreferredCheck() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        checkingPreferred = false
        return !invalidated
    }

    private func activeFailed() {
        lock.lock()
        guard !others.isEmpty, !invalidated, !probing, failover.failed(at: Date()) else { lock.unlock(); return }
        probing = true
        let startedAt = activeAnswers
        lock.unlock()

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
            // overruled, and neither may a computer on screen that answered again while the probe ran.
            if finishProbe(found: found != nil, startedAt: startedAt), let found { await onSwitch(computer.id, found) }
        }
    }

    /// Ends a probe; false when its result must be dropped: this client was invalidated, or the computer on screen
    /// answered after the probe started.
    private func finishProbe(found: Bool, startedAt: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        probing = false
        failover.probeFinished(found: found, at: Date())
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

    /// The app came back to the foreground: requests that failed before it was suspended don't count any more.
    func appResumed() {
        lock.lock()
        failover.resumed()
        lock.unlock()
    }

    func invalidate() {
        lock.lock()
        invalidated = true
        lock.unlock()
        pinned.invalidate()
    }
}
