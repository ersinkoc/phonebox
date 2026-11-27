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

    /// Forgets several computers at once (one Keychain write). Nothing changes when the write fails: the error is
    /// shown and the computers stay paired, rather than coming back after the next launch.
    func forget(_ ids: Set<String>) {
        let next = computers.filter { !ids.contains($0.id) }
        guard next.count != computers.count else { return }
        do {
            try keychain.save(next)
        } catch {
            errorMessage = (error as? KeychainError)?.errorDescription ?? "Could not forget the computer: \(error.localizedDescription)"
            return
        }
        computers = next
        if let active = activeID, ids.contains(active) {
            activeID = next.first?.id
            UserDefaults.standard.set(activeID, forKey: Self.activeKey)
        }
        errorMessage = nil
    }

    func forget(_ id: String) { forget([id]) }
}
