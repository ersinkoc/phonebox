import Foundation
import UIKit

/// Headers the agent uses to tell phones apart, mirroring what AgentLink sends on Android.
enum DeviceIdentity {
    /// 12 lower-case hex characters, stable for this installation. Not a secret; the agent only groups phones by it.
    static let deviceID: String = {
        let key = "phonebox.device-id"
        if let existing = UserDefaults.standard.string(forKey: key), !existing.isEmpty { return existing }
        let raw = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let id = String(raw.prefix(12))
        UserDefaults.standard.set(id, forKey: key)
        return id
    }()

    static let model: String = {
        let text = "iOS \(UIDevice.current.systemVersion) \(UIDevice.current.model)"
        var scalars = String.UnicodeScalarView()
        for scalar in text.unicodeScalars where scalar.value >= 0x20 && scalar.value < 0x7f {
            if scalars.count >= 40 { break }
            scalars.append(scalar)
        }
        return String(scalars)
    }()

    static let appVersion: String = {
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
        return String(version.prefix(31))
    }()
}
