import Combine
import Foundation
import PhoneBoxKit

/// The app's single source of pairing state. Tokens are held in memory for the session and in the Keychain
/// across launches.
///
/// Several computers can be paired (two machines, or Windows and macOS on one dual-boot PC). Two of them matter:
/// - the user's pick (`preferredID`): the computer they chose or paired last;
/// - the active one (`activeID`): the computer the panel shows.
/// They differ only while the pick doesn't answer: AgentClient then shows another paired computer that answers, and
/// goes back to the pick once it answers again. So switching operating systems needs no re-pairing, and the user's
/// choice is never lost.
@MainActor
final class PairingStore: ObservableObject {
    @Published private(set) var computers: [PairedComputer]
    @Published private(set) var activeID: String?
    @Published private(set) var preferredID: String?
    @Published private(set) var isPairing = false
    @Published var errorMessage: String?

    private let keychain: KeychainStore
    /// The ids are not secret (they are certificate fingerprints), so they can live in defaults.
    private static let activeKey = "activeComputerID"
    private static let preferredKey = "preferredComputerID"

    init() {
        let store = KeychainStore()
        keychain = store
        computers = store.load()
        let ids = Set(computers.map(\.id))
        let saved = UserDefaults.standard.string(forKey: Self.activeKey)
        activeID = saved.map(ids.contains) == true ? saved : computers.first?.id
        let pick = UserDefaults.standard.string(forKey: Self.preferredKey)
        preferredID = pick.map(ids.contains) == true ? pick : activeID
    }

    /// The computer the panel shows.
    var computer: PairedComputer? {
        computers.first { $0.id == activeID } ?? computers.first
    }

    /// The paired computers other than the active one.
    var others: [PairedComputer] {
        computers.filter { $0.id != computer?.id }
    }

    /// The user's own pick, when the panel shows another computer; nil while it shows the pick.
    var pickElsewhere: PairedComputer? {
        guard let pick = computers.first(where: { $0.id == preferredID }), pick.id != computer?.id else { return nil }
        return pick
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
            choose(paired.id)
        } catch let error as PairingError {
            errorMessage = error.errorDescription
        } catch {
            errorMessage = "Pairing failed: \(error.localizedDescription)"
        }
    }

    /// The user's own choice (a tap in the list, or pairing a computer): shown now, and returned to whenever another
    /// computer had to be shown while it didn't answer.
    func choose(_ id: String) {
        guard computers.contains(where: { $0.id == id }) else { return }
        preferredID = id
        UserDefaults.standard.set(id, forKey: Self.preferredKey)
        show(id)
    }

    /// AgentClient's switch: `to` answers while `from` doesn't, or `to` is the user's pick and answers again. Applied
    /// only while the panel still shows `from`, so a computer the user chose meanwhile is never overruled. On the main
    /// actor, like the user's own choice.
    func follow(from: String, to: String) {
        guard activeID == from, to != from, computers.contains(where: { $0.id == to }) else { return }
        show(to)
    }

    private func show(_ id: String) {
        guard id != activeID else { return }
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
        if let pick = preferredID, ids.contains(pick) {
            preferredID = activeID
            UserDefaults.standard.set(preferredID, forKey: Self.preferredKey)
        }
        errorMessage = nil
    }

    func forget(_ id: String) { forget([id]) }
}
