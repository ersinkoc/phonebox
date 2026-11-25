import Combine
import CryptoKit
import Foundation
import Network
import PhoneBoxKit
import Security

/// Carries the panel's HTTPS traffic to the agent over the iPhone's cable.
///
/// iOS has no `adb reverse`. usbmuxd can only open connections *from the computer into the phone*, so the
/// direction is inverted (the same shape metro-usb uses for Metro):
///
/// 1. This app listens on `127.0.0.1:8765` for connections from the computer. The desktop agent finds the
///    iPhone through usbmuxd and connects to that device port.
/// 2. Every connection from the computer must answer a fresh nonce with `HMAC-SHA256(token, nonce)`. Only
///    those pipes join the pool, so another app that finds the loopback port cannot make the relay hand a
///    panel request (and its Bearer token) to itself. The pairing token never crosses the cable.
/// 3. `AgentClient` connects to `127.0.0.1:8767`, this app's proxy listener. Each such connection takes one
///    authenticated pipe; while a pipe is idle the request waits for it. The agent connects the pipe to its
///    own Wi-Fi HTTPS port, so TLS still runs end to end between this app and the agent. The certificate pin
///    and the token are the same as over Wi-Fi, and the agent only moves ciphertext.
///
/// The proxy listener exists only while at least one pipe is authenticated, so with no cable or no agent
/// `127.0.0.1:8767` refuses the connection and `AgentClient` moves on to the Wi-Fi addresses at once.
final class USBLink: ObservableObject {
    /// The device port the desktop agent reaches through usbmuxd.
    static let hostPort: UInt16 = 8765
    /// The loopback port `AgentClient` uses for the cable.
    static let proxyPort: UInt16 = 8767

    /// True while at least one authenticated pipe (idle or in use) exists. Shown in the options sheet.
    @Published private(set) var isConnected = false

    private let token: String
    private let queue = DispatchQueue(label: "net.oxog.phonebox.usb")
    private enum ListenerRole { case host, proxy }
    private var listeners: [ListenerRole: NWListener] = [:]
    private var listenerRetries: [ListenerRole: DispatchWorkItem] = [:]
    private var pipes: [Pipe] = []
    private var sessions: [ObjectIdentifier: Session] = [:]
    private var running = false

    init(computer: PairedComputer) {
        token = computer.token
    }

    func start() {
        queue.async { [weak self] in
            guard let self, !self.running, !self.token.isEmpty else { return }
            self.running = true
            self.syncListener(.host)
            self.syncListener(.proxy)
        }
    }

    func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.running = false
            for listener in self.listeners.values { listener.cancel() }
            self.listeners.removeAll()
            for retry in self.listenerRetries.values { retry.cancel() }
            self.listenerRetries.removeAll()
            for pipe in self.pipes { pipe.connection.cancel() }
            for (_, session) in self.sessions { session.end() }
            self.pipes.removeAll()
            self.sessions.removeAll()
            self.publish(false)
        }
    }

    private func syncListener(_ role: ListenerRole) {
        guard running, role == .host || hasTransport else {
            listenerRetries.removeValue(forKey: role)?.cancel()
            listeners.removeValue(forKey: role)?.cancel()
            return
        }
        guard listeners[role] == nil, listenerRetries[role] == nil else { return }
        let number = role == .host ? Self.hostPort : Self.proxyPort
        guard let port = NWEndpoint.Port(rawValue: number) else { return }
        let parameters = NWParameters.tcp
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(host: NWEndpoint.Host("127.0.0.1"), port: port)
        guard let listener = try? NWListener(using: parameters) else {
            retryListener(role)
            return
        }

        listener.newConnectionHandler = { [weak self, weak listener] connection in
            guard let self, let listener, self.listeners[role] === listener else {
                connection.cancel()
                return
            }
            switch role {
            case .host: self.acceptHost(connection)
            case .proxy: self.acceptLocal(connection)
            }
        }
        listener.stateUpdateHandler = { [weak self, weak listener] state in
            guard case .failed = state, let self, let listener, self.listeners[role] === listener else { return }
            self.listeners.removeValue(forKey: role)?.cancel()
            self.retryListener(role)
        }
        listeners[role] = listener
        listener.start(queue: queue)
    }

    private func retryListener(_ role: ListenerRole) {
        guard running, role == .host || hasTransport, listenerRetries[role] == nil else { return }
        let retry = DispatchWorkItem { [weak self] in
            self?.listenerRetries.removeValue(forKey: role)
            self?.syncListener(role)
        }
        listenerRetries[role] = retry
        queue.asyncAfter(deadline: .now() + 1, execute: retry)
    }

    // MARK: - Listener side (the computer dials in)

    private func acceptHost(_ connection: NWConnection) {
        guard running, !token.isEmpty else {
            connection.cancel()
            return
        }

        let nonce = Self.randomHex(byteCount: 32)
        let expected = Self.mac(token: token, nonce: nonce)
        let timeout = DispatchWorkItem { [weak connection] in connection?.cancel() }
        queue.asyncAfter(deadline: .now() + 5, execute: timeout)

        connection.stateUpdateHandler = { [weak self, weak connection] state in
            guard let connection else { return }
            switch state {
            case .failed, .cancelled:
                timeout.cancel()
                self?.removeHost(connection)
            default:
                break
            }
        }
        connection.start(queue: queue)

        send(connection, USBHandshake.challenge(nonce: nonce) + "\n") { [weak connection] in
            connection?.cancel()
        }

        readLine(connection) { [weak self, weak connection] line in
            guard let self, let connection else { return }
            timeout.cancel()
            guard self.running else {
                connection.cancel()
                return
            }
            guard let line,
                  let mac = USBHandshake.parseAuth(line),
                  Self.constantTimeEqual(mac.lowercased(), expected.lowercased()) else {
                connection.cancel()
                return
            }
            let pipe = Pipe(connection: connection)
            self.pipes.append(pipe)
            self.publish(self.hasTransport)
            self.syncListener(.proxy)
        }
    }

    /// A host pipe failed or was cancelled: drop it, and close the request it was carrying, if any.
    private func removeHost(_ connection: NWConnection) {
        pipes.removeAll { $0.connection === connection }
        if let match = sessions.first(where: { $0.value.pipe.connection === connection }) {
            close(match.value)
        } else {
            publish(hasTransport)
            syncListener(.proxy)
        }
    }

    // MARK: - Proxy side (the panel's AgentClient connects here)

    private var hasTransport: Bool {
        !pipes.isEmpty || !sessions.isEmpty
    }

    private func acceptLocal(_ connection: NWConnection) {
        guard running else {
            connection.cancel()
            return
        }
        connection.start(queue: queue)
        // A pipe may have been taken by an earlier connection between accept and ready: wait briefly for
        // one instead of failing a request that has already started.
        waitForPipe(connection, attemptsLeft: 30)
    }

    private func waitForPipe(_ connection: NWConnection, attemptsLeft: Int) {
        guard running else {
            connection.cancel()
            return
        }
        if let pipe = pipes.last {
            pipes.removeLast()
            serve(connection, pipe: pipe)
            return
        }
        guard attemptsLeft > 0 else {
            connection.cancel()
            return
        }
        queue.asyncAfter(deadline: .now() + 0.1) { [weak self, weak connection] in
            guard let self, let connection else { return }
            self.waitForPipe(connection, attemptsLeft: attemptsLeft - 1)
        }
    }

    private func serve(_ local: NWConnection, pipe: Pipe) {
        let session = Session(pipe: pipe, local: local)
        sessions[ObjectIdentifier(session)] = session

        local.stateUpdateHandler = { [weak self, weak session] state in
            switch state {
            case .failed, .cancelled:
                if let session { self?.close(session) }
            default:
                break
            }
        }

        send(pipe.connection, USBHandshake.go + "\n") { [weak self, weak session] in
            guard let self, let session else { return }
            self.close(session)
        }

        pump(from: pipe.connection, to: local, session: session)
        pump(from: local, to: pipe.connection, session: session)
    }

    private func close(_ session: Session) {
        guard sessions.removeValue(forKey: ObjectIdentifier(session)) != nil else { return }
        session.end()
        publish(hasTransport)
        syncListener(.proxy)
    }

    // MARK: - Relay

    private func pump(from source: NWConnection, to target: NWConnection, session: Session) {
        source.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { [weak self] data, _, isComplete, error in
            guard let self, !session.ended else { return }
            if let data, !data.isEmpty {
                target.send(content: data, completion: .contentProcessed { [weak self] sendError in
                    guard let self, !session.ended else { return }
                    if sendError != nil || isComplete || error != nil {
                        self.close(session)
                    } else {
                        self.pump(from: source, to: target, session: session)
                    }
                })
            } else if isComplete || error != nil {
                self.close(session)
            } else {
                self.pump(from: source, to: target, session: session)
            }
        }
    }

    private func send(_ connection: NWConnection, _ text: String, onFailure: @escaping () -> Void) {
        connection.send(content: Data(text.utf8), completion: .contentProcessed { error in
            if error != nil { onFailure() }
        })
    }

    /// Reads one newline-terminated control line, one byte at a time so no TLS byte that follows can be
    /// swallowed by an over-reading buffer.
    private func readLine(_ connection: NWConnection, into data: Data = Data(), completion: @escaping (String?) -> Void) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1) { [weak self] chunk, _, isComplete, error in
            guard let self else { return }
            var data = data
            if let chunk, !chunk.isEmpty {
                data.append(chunk)
                if chunk.last == 0x0A {
                    completion(String(data: data.dropLast(), encoding: .utf8))
                    return
                }
                if data.count > 256 {
                    completion(nil)
                    return
                }
            }
            if isComplete || error != nil {
                completion(nil)
                return
            }
            self.readLine(connection, into: data, completion: completion)
        }
    }

    // MARK: - Helpers

    private func publish(_ connected: Bool) {
        DispatchQueue.main.async { [weak self] in
            guard let self, self.isConnected != connected else { return }
            self.isConnected = connected
        }
    }

    private static func randomHex(byteCount: Int) -> String {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        if SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) != errSecSuccess {
            for index in bytes.indices { bytes[index] = UInt8.random(in: UInt8.min...UInt8.max) }
        }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }

    private static func mac(token: String, nonce: String) -> String {
        let key = SymmetricKey(data: Data(token.utf8))
        return HMAC<SHA256>.authenticationCode(for: Data(nonce.utf8), using: key)
            .map { String(format: "%02x", $0) }
            .joined()
    }

    private static func constantTimeEqual(_ left: String, _ right: String) -> Bool {
        let a = Array(left.utf8)
        let b = Array(right.utf8)
        guard a.count == b.count else { return false }
        var difference: UInt8 = 0
        for index in a.indices { difference |= a[index] ^ b[index] }
        return difference == 0
    }

    private final class Pipe {
        let connection: NWConnection

        init(connection: NWConnection) {
            self.connection = connection
        }
    }

    private final class Session {
        let pipe: Pipe
        let local: NWConnection
        private(set) var ended = false

        init(pipe: Pipe, local: NWConnection) {
            self.pipe = pipe
            self.local = local
        }

        func end() {
            guard !ended else { return }
            ended = true
            pipe.connection.cancel()
            local.cancel()
        }
    }
}
