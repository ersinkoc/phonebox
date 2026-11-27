import Foundation
import Security

enum KeychainError: LocalizedError {
    case unexpectedStatus(OSStatus)

    var errorDescription: String? {
        switch self {
        case let .unexpectedStatus(status):
            return "The iOS Keychain refused to store the pairing (status \(status))."
        }
    }
}

/// The only place pairing tokens are written. One generic-password item holds every paired computer, available
/// after the first unlock and never migrated to another device by a backup.
final class KeychainStore {
    private let service = "net.oxog.phonebox.ios"
    private let account = "paired-computers"
    /// Before several computers could be paired, the one pairing lived under this account.
    private let legacyAccount = "paired-computer"

    private func query(_ account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    private func read(_ account: String) -> Data? {
        var q = query(account)
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    /// Every paired computer, in pairing order. A pairing saved by an older version is moved over once.
    func load() -> [PairedComputer] {
        if let data = read(account), let computers = try? JSONDecoder().decode([PairedComputer].self, from: data) {
            return computers
        }
        guard let data = read(legacyAccount), let single = try? JSONDecoder().decode(PairedComputer.self, from: data) else {
            return []
        }
        if (try? save([single])) != nil { SecItemDelete(query(legacyAccount) as CFDictionary) }
        return [single]
    }

    func save(_ computers: [PairedComputer]) throws {
        guard !computers.isEmpty else { delete(); return }
        let data = try JSONEncoder().encode(computers)
        SecItemDelete(query(account) as CFDictionary)
        var attributes = query(account)
        attributes[kSecValueData as String] = data
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
    }

    func delete() {
        SecItemDelete(query(account) as CFDictionary)
        SecItemDelete(query(legacyAccount) as CFDictionary)
    }
}
