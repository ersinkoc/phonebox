import Foundation

/// Reading the stored pairings, including the single pairing an older version saved.
///
/// The app keeps every paired computer as one JSON list. Before several computers could be paired, the one pairing
/// was stored on its own, under another Keychain account. `decode` reads either, so updating the app keeps the
/// pairing; the caller then writes the list and removes the old item.
public enum PairingList {
    /// The stored computers, and whether they came from the old single item (so the caller should move them over).
    /// A list that can't be decoded falls back to the old item; nothing usable gives an empty list.
    public static func decode<Computer: Decodable>(
        _ type: Computer.Type, list: Data?, legacy: Data?
    ) -> (computers: [Computer], fromLegacy: Bool) {
        let decoder = JSONDecoder()
        if let list, let computers = try? decoder.decode([Computer].self, from: list) {
            return (computers, false)
        }
        if let legacy, let single = try? decoder.decode(Computer.self, from: legacy) {
            return ([single], true)
        }
        return ([], false)
    }
}
