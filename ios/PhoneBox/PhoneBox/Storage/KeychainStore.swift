import Foundation
import PhoneBoxKit
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

    /// Every paired computer, in pairing order. A pairing saved by an older version is moved over once; it stays in
    /// its old place until the list has been written.
    func load() -> [PairedComputer] {
        let stored = PairingList.decode(PairedComputer.self, list: read(account), legacy: read(legacyAccount))
        if stored.fromLegacy { try? save(stored.computers) }
        return stored.computers
    }

    /// Replaces the stored list. The item is updated in place, so a failed write leaves the previous list intact; only
    /// after a successful write is the pre-list item (and its token) removed.
    func save(_ computers: [PairedComputer]) throws {
        guard !computers.isEmpty else { try delete(); return }
        let data = try JSONEncoder().encode(computers)
        let update: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        var status = SecItemUpdate(query(account) as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var attributes = query(account)
            attributes.merge(update) { _, new in new }
            status = SecItemAdd(attributes as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
        SecItemDelete(query(legacyAccount) as CFDictionary)
    }

    /// Removes every stored pairing and its token. The pre-list item goes first: if it can't be removed, the list stays,
    /// rather than leaving only the old pairing to be read back at the next launch.
    func delete() throws {
        for name in [legacyAccount, account] {
            let status = SecItemDelete(query(name) as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else { throw KeychainError.unexpectedStatus(status) }
        }
    }
}
