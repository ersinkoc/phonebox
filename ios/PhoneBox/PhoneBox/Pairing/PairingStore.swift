import Combine
import Foundation
import PhoneBoxKit

/// The app's single source of pairing state. Tokens are held in memory for the session and in the Keychain
/// across launches.
///
/// Several computers can be paired (two machines, or Windows and macOS on one dual-boot PC). One is active: the
/// panel shows it. When the active one stops answering, AgentClient looks for another paired computer that
/// answers and makes it active, so switching operating systems needs no re-pairing.
@MainActor
final class PairingStore: ObservableObject {
    @Published private(set) var computers: [PairedComputer]
    @Published private(set) var activeID: String?
    @Published private(set) var isPairing = false
    @Published var errorMessage: String?

    private let keychain: KeychainStore
    /// The active computer's id is not secret (it is the certificate fingerprint), so it can live in defaults.
    private static let activeKey = "activeComputerID"

    init() {
        let store = KeychainStore()
        keychain = store
        computers = store.load()
        let saved = UserDefaults.standard.string(forKey: Self.activeKey)
        activeID = computers.contains { $0.id == saved } ? saved : computers.first?.id
    }

    /// The computer the panel shows.
    var computer: PairedComputer? {
        computers.first { $0.id == activeID } ?? computers.first
    }

    /// The paired computers other than the active one.
    var others: [PairedComputer] {
        computers.filter { $0.id != computer?.id }
    }

    func pair(with link: PairLink) async {
        guard !isPairing else { return }
        isPairing = true
        errorMessage = nil
        defer { isPairing = false }

        do {
            let paired = try await PairingService().claim(link: link)
            // Pairing the same computer again (same certificate) replaces its token and addresses.
            var next = computers.filter { $0.id != paired.id }
            next.append(paired)
            try keychain.save(next)
            computers = next
            activate(paired.id)
        } catch let error as PairingError {
            errorMessage = error.errorDescription
        } catch {
            errorMessage = "Pairing failed: \(error.localizedDescription)"
        }
    }

    func activate(_ id: String) {
        guard computers.contains(where: { $0.id == id }), id != activeID else { return }
        activeID = id
        UserDefaults.standard.set(id, forKey: Self.activeKey)
    }

    func forget(_ id: String) {
        let next = computers.filter { $0.id != id }
        try? keychain.save(next)
        computers = next
        if activeID == id {
            activeID = next.first?.id
            UserDefaults.standard.set(activeID, forKey: Self.activeKey)
        }
        errorMessage = nil
    }
}
