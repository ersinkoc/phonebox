import Foundation
import Security
import CryptoKit

/// Evaluates the agent's self-signed TLS certificate by its SHA-256 pin and nothing else.
///
/// The agent's certificate is self-signed with `CN=PhoneBox agent (<computer>)`, so the normal host-name and
/// CA checks can never pass. Identity is the certificate from the pairing link: if the leaf certificate's
/// SHA-256 does not match the pin, the connection is cancelled. There is no fallback to the system trust store
/// and no host-name override that would accept another certificate.
final class PinnedTrustDelegate: NSObject, URLSessionDelegate {
    private let pin: String
    private let lock = NSLock()
    private var mismatched = false

    init(pin: String) {
        self.pin = pin.lowercased()
        super.init()
    }

    var sawCertificateMismatch: Bool {
        lock.lock()
        defer { lock.unlock() }
        return mismatched
    }

    func resetMismatch() {
        lock.lock()
        mismatched = false
        lock.unlock()
    }

    func urlSession(
        _ session: URLSession,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust,
              let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate],
              let leaf = chain.first,
              let fingerprint = Self.sha256Hex(of: leaf) else {
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }

        guard fingerprint == pin else {
            lock.lock()
            mismatched = true
            lock.unlock()
            completionHandler(.cancelAuthenticationChallenge, nil)
            return
        }
        completionHandler(.useCredential, URLCredential(trust: trust))
    }

    /// SHA-256 over the certificate's DER encoding, lower-case hex — the same value the agent puts in the QR code.
    static func sha256Hex(of certificate: SecCertificate) -> String? {
        let der = SecCertificateCopyData(certificate) as Data
        return SHA256.hash(data: der).map { String(format: "%02x", $0) }.joined()
    }
}

/// A URLSession that trusts exactly one certificate. Sessions are kept alive for connection reuse and
/// invalidated by the owner when the paired computer goes away.
final class PinnedSession {
    private let delegate: PinnedTrustDelegate
    private let session: URLSession

    init(pin: String, timeout: TimeInterval) {
        let delegate = PinnedTrustDelegate(pin: pin)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = max(timeout, 30)
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        self.delegate = delegate
        self.session = URLSession(configuration: configuration, delegate: delegate, delegateQueue: nil)
    }

    var sawCertificateMismatch: Bool { delegate.sawCertificateMismatch }

    func resetMismatch() { delegate.resetMismatch() }

    func data(for request: URLRequest) async throws -> (HTTPURLResponse, Data) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        return (http, data)
    }

    func invalidate() {
        session.invalidateAndCancel()
    }
}
