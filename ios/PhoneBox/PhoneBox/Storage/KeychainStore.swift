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

/// The only place the pairing token is written. A generic-password item, available after the first unlock and
/// never migrated to another device by a backup.
final class KeychainStore {
    private let service = "net.oxog.phonebox.ios"
    private let account = "paired-computer"

    private func baseQuery() -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    func load() -> PairedComputer? {
        var query = baseQuery()
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        return try? JSONDecoder().decode(PairedComputer.self, from: data)
    }

    func save(_ computer: PairedComputer) throws {
        let data = try JSONEncoder().encode(computer)
        SecItemDelete(baseQuery() as CFDictionary)
        var attributes = baseQuery()
        attributes[kSecValueData as String] = data
        attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(attributes as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
    }

    func delete() {
        SecItemDelete(baseQuery() as CFDictionary)
    }
}
