import Foundation
import PhoneBoxKit

enum PairingError: LocalizedError, Equatable {
    case unreachable(computer: String, hosts: [String])
    case codeRejected(computer: String)
    case badAnswer(computer: String, status: Int)
    case wrongCertificate(computer: String)
    case malformedAnswer(computer: String)

    var errorDescription: String? {
        switch self {
        case let .unreachable(computer, hosts):
            return "Could not reach \(computer) at \(hosts.joined(separator: ", ")). Check that the computer is on the same Wi-Fi or Tailscale network, that Wi-Fi pairing is switched on, and that iOS allowed PhoneBox to use the local network."
        case let .codeRejected(computer):
            return "\(computer) refused the one-time pairing code. A code works once and expires after five minutes; start pairing again on the computer."
        case let .badAnswer(computer, status):
            return "\(computer) answered with an unexpected status (\(status))."
        case let .wrongCertificate(computer):
            return "The computer's certificate does not match the fingerprint in the pairing link, so PhoneBox did not trust it and did not save a token. Start again from the computer's QR code."
        case let .malformedAnswer(computer):
            return "\(computer) sent an incomplete pairing answer, so nothing was saved."
        }
    }
}

/// Trades the QR link's one-time code for the agent's Wi-Fi token over TLS pinned to the link's fingerprint.
///
/// The pin is checked while the TLS connection is made, before the request body (the code) is sent. The answer's
/// fingerprint is checked again before the token is returned, so a token is only stored for the certificate the
/// user confirmed.
struct PairingService {
    private struct ClaimAnswer: Decodable {
        let token: String?
        let fingerprint: String?
        let port: Int?
        let hosts: [String]?
    }

    func claim(link: PairLink) async throws -> PairedComputer {
        let pinned = PinnedSession(pin: link.fingerprint, timeout: 8)
        defer { pinned.invalidate() }

        var failure = PairingError.unreachable(computer: link.computerName, hosts: link.hosts)

        for host in link.hosts {
            guard let url = URL(string: "https://\(host):\(link.port)/api/pair/claim") else { continue }

            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 8
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue(DeviceIdentity.model, forHTTPHeaderField: "X-PhoneBox-Model")
            request.setValue(DeviceIdentity.appVersion, forHTTPHeaderField: "X-PhoneBox-App")
            request.httpBody = try? JSONSerialization.data(withJSONObject: ["code": link.code])

            pinned.resetMismatch()
            let result: (HTTPURLResponse, Data)
            do {
                result = try await pinned.data(for: request)
            } catch {
                failure = pinned.sawCertificateMismatch
                    ? .wrongCertificate(computer: link.computerName)
                    : .unreachable(computer: link.computerName, hosts: link.hosts)
                continue
            }

            if result.0.statusCode == 403 {
                throw PairingError.codeRejected(computer: link.computerName)
            }
            guard result.0.statusCode == 200 else {
                failure = .badAnswer(computer: link.computerName, status: result.0.statusCode)
                continue
            }
            guard let answer = try? JSONDecoder().decode(ClaimAnswer.self, from: result.1) else {
                throw PairingError.malformedAnswer(computer: link.computerName)
            }

            // The token belongs to the certificate the user confirmed; a different fingerprint (or none) means
            // this answer is not from that computer, so it is discarded.
            let fingerprint = (answer.fingerprint ?? "").lowercased()
            guard fingerprint == link.fingerprint.lowercased() else {
                throw PairingError.wrongCertificate(computer: link.computerName)
            }
            guard let token = answer.token, !token.isEmpty else {
                throw PairingError.malformedAnswer(computer: link.computerName)
            }
            let port = answer.port ?? link.port
            guard (1...65535).contains(port) else {
                throw PairingError.malformedAnswer(computer: link.computerName)
            }
            let reportedHosts = (answer.hosts ?? []).filter(PairLink.isLocalAddress)

            return PairedComputer(
                name: link.computerName,
                hosts: reportedHosts.isEmpty ? link.hosts : reportedHosts,
                port: port,
                token: token,
                fingerprint: fingerprint
            )
        }

        throw failure
    }
}
