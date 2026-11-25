import Foundation
import PhoneBoxKit

/// A computer this iPhone is paired with. The whole record (including the agent token) lives in the iOS Keychain;
/// it is never written to preferences or given to the WebView.
struct PairedComputer: Codable, Equatable, Identifiable {
    let name: String
    let hosts: [String]
    let port: Int
    let token: String
    let fingerprint: String

    var id: String { fingerprint.lowercased() }
    var checkCode: String { PairLink.checkCode(for: fingerprint) }
}
