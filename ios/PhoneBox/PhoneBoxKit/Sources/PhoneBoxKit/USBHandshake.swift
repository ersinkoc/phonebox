import Foundation

/// The line protocol that opens the iPhone's USB relay to a computer.
///
/// iOS cannot reverse a USB connection (usbmuxd only dials from the computer into the phone), so the app
/// listens on loopback and the desktop agent connects to it through usbmuxd. Each connection must prove it
/// knows the pairing token before the app lets it carry a request:
///
/// 1. app → computer: `PhoneBox-USB 1 <64 hex nonce>`
/// 2. computer → app: `AUTH <64 hex HMAC-SHA256(token, nonce)>`
/// 3. once the app assigns the pipe to a panel request: app → computer: `GO`
///
/// The token itself never crosses the cable; only the keyed hash of a fresh nonce does.
public enum USBHandshake {
    public static let version = 1
    public static let challengePrefix = "PhoneBox-USB \(version) "
    public static let authPrefix = "AUTH "
    public static let go = "GO"

    /// A fresh 32-byte nonce is 64 lower-case hex characters.
    public static func isValidNonce(_ value: String) -> Bool {
        isHex(value, length: 64)
    }

    /// The HMAC-SHA256 answer is also 32 bytes, hex encoded.
    public static func isValidMAC(_ value: String) -> Bool {
        isHex(value, length: 64)
    }

    public static func challenge(nonce: String) -> String {
        challengePrefix + nonce
    }

    public static func auth(mac: String) -> String {
        authPrefix + mac
    }

    /// Returns the nonce from a valid challenge line, or nil for anything else (including another version).
    public static func parseChallenge(_ line: String) -> String? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix(challengePrefix) else { return nil }
        let nonce = String(trimmed.dropFirst(challengePrefix.count))
        return isValidNonce(nonce) ? nonce.lowercased() : nil
    }

    /// Returns the lower-case MAC from a valid `AUTH …` line, or nil for anything else.
    public static func parseAuth(_ line: String) -> String? {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix(authPrefix) else { return nil }
        let mac = String(trimmed.dropFirst(authPrefix.count))
        return isValidMAC(mac) ? mac.lowercased() : nil
    }

    private static func isHex(_ value: String, length: Int) -> Bool {
        guard value.count == length else { return false }
        return value.unicodeScalars.allSatisfy { scalar in
            switch scalar.value {
            case 48...57, 65...70, 97...102: return true
            default: return false
            }
        }
    }
}
