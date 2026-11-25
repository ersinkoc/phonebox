import Foundation

/// A pairing link from the desktop agent's QR code, parsed with the same rules as the Android app
/// (android/app/src/main/java/net/oxog/phonebox/deskpanel/AgentLink.kt, `PairLink`).
///
/// Accepted forms:
/// - `https://ersinkoc.github.io/phonebox/pair/#v=1&pc=…&fp=…&port=…&h=…&c=…`
///   (the QR code itself; the data is in the fragment so it never reaches a web server)
/// - `phonebox://pair?v=1&pc=…&fp=…&port=…&h=…&c=…`
///   (the "Open in PhoneBox" button on that page)
///
/// The fingerprint is the SHA-256 of the agent's self-signed TLS certificate (lower-case hex). The code is the
/// one-time pairing code that `POST /api/pair/claim` trades for the agent's Wi-Fi token.
public struct PairLink: Equatable, Identifiable, Sendable {
    public let computerName: String
    public let fingerprint: String
    public let port: Int
    public let hosts: [String]
    public let code: String

    /// Stable identity for SwiftUI sheets; a pairing link is unique per certificate and code.
    public var id: String { "\(fingerprint):\(code)" }

    /// The short check code shown next to the QR code on the computer: the first 8 hex characters of the
    /// fingerprint, upper-case and split in two groups ("A1B2 C3D4"). Same formatting as the Android app.
    public var checkCode: String { Self.checkCode(for: fingerprint) }

    public static func checkCode(for fingerprint: String) -> String {
        let prefix = String(fingerprint.prefix(8)).uppercased()
        guard prefix.count == 8 else { return prefix }
        return "\(prefix.prefix(4)) \(prefix.suffix(4))"
    }

    public init?(urlString: String) {
        guard let url = URL(string: urlString) else { return nil }
        self.init(url: url)
    }

    public init?(url: URL) {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return nil }
        let scheme = (components.scheme ?? "").lowercased()
        let host = (components.host ?? "").lowercased()

        let raw: String
        if scheme == "https" && host == "ersinkoc.github.io" && components.path.hasPrefix("/phonebox/pair") {
            guard let fragment = components.percentEncodedFragment else { return nil }
            raw = fragment
        } else if scheme == "phonebox" && host == "pair" {
            guard let query = components.percentEncodedQuery else { return nil }
            raw = query
        } else {
            return nil
        }

        guard let fields = Self.parseQuery(raw) else { return nil }
        guard fields["v"] == "1" else { return nil }
        guard let fingerprintRaw = fields["fp"], Self.isFingerprint(fingerprintRaw) else { return nil }
        guard let portRaw = fields["port"], let port = Int(portRaw), (1...65535).contains(port) else { return nil }
        guard let code = fields["c"], Self.isPairingCode(code) else { return nil }

        let hosts = fields["h", default: ""]
            .split(separator: ",", omittingEmptySubsequences: false)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { Self.isLocalAddress($0) }
        guard !hosts.isEmpty else { return nil }

        let cleanedName = Self.cleanName(fields["pc", default: ""])
        let computerName = cleanedName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "your computer" : cleanedName

        self.computerName = computerName
        self.fingerprint = fingerprintRaw.lowercased()
        self.port = port
        self.hosts = Array(hosts.prefix(8))
        self.code = code
    }

    /// The real addresses the agent offers: private network (10/8, 172.16/12, 192.168/16) and Tailscale
    /// (100.64/10) IPv4. Anything else is not a PhoneBox pairing host.
    public static func isLocalAddress(_ host: String) -> Bool {
        let parts = host.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return false }
        var octets: [Int] = []
        for part in parts {
            guard (1...3).contains(part.count),
                  part.unicodeScalars.allSatisfy({ $0.value >= 48 && $0.value <= 57 }),
                  let value = Int(part),
                  (0...255).contains(value) else { return false }
            octets.append(value)
        }
        return octets[0] == 10
            || (octets[0] == 172 && (16...31).contains(octets[1]))
            || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 100 && (64...127).contains(octets[1]))
    }

    /// Drops control characters and keeps at most 64 characters, like the Android app's name filter.
    private static func cleanName(_ raw: String) -> String {
        var scalars = String.UnicodeScalarView()
        for scalar in raw.unicodeScalars where scalar.value >= 0x20 {
            if scalars.count >= 64 { break }
            scalars.append(scalar)
        }
        return String(scalars)
    }

    public static func isFingerprint(_ value: String) -> Bool {
        guard value.count == 64 else { return false }
        return value.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 48...57, 65...70, 97...102: return true
            default: return false
            }
        }
    }

    /// One-time pairing codes are 16–64 characters of base64url (the agent makes 128 random bits).
    public static func isPairingCode(_ value: String) -> Bool {
        guard (16...64).contains(value.count) else { return false }
        return value.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 48...57, 65...90, 97...122, 45, 95: return true
            default: return false
            }
        }
    }

    /// `name=value&…` with percent-decoded values. '+' stays a plus, like Android's `Uri.decode`.
    static func parseQuery(_ raw: String) -> [String: String]? {
        var fields: [String: String] = [:]
        for part in raw.split(separator: "&", omittingEmptySubsequences: false) {
            guard let separator = part.firstIndex(of: "="), separator > part.startIndex else { continue }
            let key = String(part[part.startIndex..<separator])
            let value = String(part[part.index(after: separator)...])
            fields[key] = value.removingPercentEncoding ?? value
        }
        return fields.isEmpty ? nil : fields
    }
}
