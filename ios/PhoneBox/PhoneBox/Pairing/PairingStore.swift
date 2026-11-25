import Combine
import Foundation
import PhoneBoxKit

/// The app's single source of pairing state. The token is held in memory for the session and in the Keychain
/// across launches; changing the pairing is the only way to replace it.
@MainActor
final class PairingStore: ObservableObject {
    @Published private(set) var computer: PairedComputer?
    @Published private(set) var isPairing = false
    @Published var errorMessage: String?

    private let keychain: KeychainStore

    init() {
        let store = KeychainStore()
        keychain = store
        computer = store.load()
    }

    func pair(with link: PairLink) async {
        guard !isPairing else { return }
        isPairing = true
        errorMessage = nil
        defer { isPairing = false }

        do {
            let paired = try await PairingService().claim(link: link)
            try keychain.save(paired)
            computer = paired
        } catch let error as PairingError {
            errorMessage = error.errorDescription
        } catch {
            errorMessage = "Pairing failed: \(error.localizedDescription)"
        }
    }

    func forget() {
        keychain.delete()
        computer = nil
        errorMessage = nil
    }
}
