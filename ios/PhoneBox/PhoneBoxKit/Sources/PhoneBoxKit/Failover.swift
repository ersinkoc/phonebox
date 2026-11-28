import Foundation

/// When the iOS app looks for another paired computer, and when it goes back to the one the user picked.
///
/// The rule, kept apart from networking so it can be tested:
/// - One failed request is not enough. Only when the computer on screen has answered nothing for `unreachableFor`
///   (an agent restarting for an update, a slow request, a short Wi-Fi drop are all shorter) are the others asked.
/// - When asking finds nobody (away from home, every computer asleep), the next ask waits longer each time, up to
///   `maxProbeDelay`.
/// - While the panel shows another computer than the user's own pick, the pick is asked every `preferredCheckEvery`,
///   and the panel goes back to it once it answers.
public struct Failover: Equatable {
    public static let unreachableFor: TimeInterval = 30
    public static let firstProbeDelay: TimeInterval = 4
    public static let maxProbeDelay: TimeInterval = 60
    public static let preferredCheckEvery: TimeInterval = 15

    /// When the computer on screen last stopped answering; nil while it answers.
    public private(set) var failingSince: Date?
    /// Asks of the others in a row that found nobody.
    public private(set) var emptyProbes = 0
    public private(set) var nextProbe = Date.distantPast
    public private(set) var nextPreferredCheck = Date.distantPast

    public init() {}

    /// The computer on screen answered: whatever came before was a passing failure.
    public mutating func answered() {
        failingSince = nil
        emptyProbes = 0
        nextProbe = .distantPast
    }

    /// The app came back to the foreground. A failure before it was suspended and one after it resumes are not one
    /// outage: the window starts again from the next failure.
    public mutating func resumed() {
        failingSince = nil
    }

    /// A request to the computer on screen got no answer at `now`. True when the others should be asked now.
    public mutating func failed(at now: Date) -> Bool {
        let since = failingSince ?? now
        failingSince = since
        return now.timeIntervalSince(since) >= Self.unreachableFor && now >= nextProbe
    }

    /// Asking the others finished at `now`; `found` when one of them answered.
    public mutating func probeFinished(found: Bool, at now: Date) {
        emptyProbes = found ? 0 : emptyProbes + 1
        nextProbe = now.addingTimeInterval(Self.probeDelay(afterEmptyProbes: emptyProbes))
    }

    /// 4 s after a probe that found someone, then 8, 16, 32 and at most 60 s while probes find nobody.
    public static func probeDelay(afterEmptyProbes count: Int) -> TimeInterval {
        guard count > 0 else { return firstProbeDelay }
        return min(maxProbeDelay, firstProbeDelay * pow(2, Double(min(count, 8))))
    }

    /// Whether the user's own pick should be asked now (only while the panel shows another computer).
    public mutating func shouldCheckPreferred(at now: Date) -> Bool {
        guard now >= nextPreferredCheck else { return false }
        nextPreferredCheck = now.addingTimeInterval(Self.preferredCheckEvery)
        return true
    }
}
