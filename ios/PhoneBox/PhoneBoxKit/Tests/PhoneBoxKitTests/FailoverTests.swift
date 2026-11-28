import XCTest
@testable import PhoneBoxKit

final class FailoverTests: XCTestCase {
    private let t0 = Date(timeIntervalSince1970: 1_800_000_000)

    func testOneFailedRequestDoesNotSwitch() {
        var f = Failover()
        XCTAssertFalse(f.failed(at: t0))
        // an agent restarting for an update, a slow GET: answered again within the window
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(10)))
        f.answered()
        XCTAssertNil(f.failingSince)
        // the window starts again from the next failure, not the first one
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(35)))
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(60)))
        XCTAssertTrue(f.failed(at: t0.addingTimeInterval(65)))
    }

    func testThirtySecondsWithoutAnAnswerAsksTheOthers() {
        var f = Failover()
        XCTAssertFalse(f.failed(at: t0))
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(29)))
        XCTAssertTrue(f.failed(at: t0.addingTimeInterval(30)))
    }

    func testASuspensionIsNotPartOfTheOutage() {
        var f = Failover()
        XCTAssertFalse(f.failed(at: t0))
        // the app was in the background for a minute: the failure after it resumes starts a new window
        f.resumed()
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(60)))
        XCTAssertFalse(f.failed(at: t0.addingTimeInterval(89)))
        XCTAssertTrue(f.failed(at: t0.addingTimeInterval(90)))
    }

    func testAskingNobodyBacksOff() {
        var f = Failover()
        _ = f.failed(at: t0)
        var now = t0.addingTimeInterval(30)
        var delays: [TimeInterval] = []
        for _ in 0..<6 {
            XCTAssertTrue(f.failed(at: now))
            f.probeFinished(found: false, at: now)
            XCTAssertFalse(f.failed(at: now.addingTimeInterval(1)))
            delays.append(f.nextProbe.timeIntervalSince(now))
            now = f.nextProbe
        }
        XCTAssertEqual(delays, [8, 16, 32, 60, 60, 60])
        // the computer on screen answers again: back to the first step
        f.answered()
        XCTAssertEqual(f.emptyProbes, 0)
        XCTAssertEqual(f.nextProbe, .distantPast)
    }

    func testProbeDelays() {
        XCTAssertEqual(Failover.probeDelay(afterEmptyProbes: 0), 4)
        XCTAssertEqual(Failover.probeDelay(afterEmptyProbes: 1), 8)
        XCTAssertEqual(Failover.probeDelay(afterEmptyProbes: 3), 32)
        XCTAssertEqual(Failover.probeDelay(afterEmptyProbes: 40), 60)
    }

    func testTheUsersPickIsAskedEveryFifteenSeconds() {
        var f = Failover()
        XCTAssertTrue(f.shouldCheckPreferred(at: t0))
        XCTAssertFalse(f.shouldCheckPreferred(at: t0.addingTimeInterval(14)))
        XCTAssertTrue(f.shouldCheckPreferred(at: t0.addingTimeInterval(15)))
    }
}

final class PairingListTests: XCTestCase {
    private struct Computer: Codable, Equatable {
        let name: String
        let token: String
    }

    private let mac = Computer(name: "Mac", token: "t1")
    private let pc = Computer(name: "PC", token: "t2")

    func testTheListIsRead() throws {
        let list = try JSONEncoder().encode([mac, pc])
        let result = PairingList.decode(Computer.self, list: list, legacy: nil)
        XCTAssertEqual(result.computers, [mac, pc])
        XCTAssertFalse(result.fromLegacy)
    }

    func testAnOlderVersionsSinglePairingIsMovedOver() throws {
        let legacy = try JSONEncoder().encode(mac)
        let result = PairingList.decode(Computer.self, list: nil, legacy: legacy)
        XCTAssertEqual(result.computers, [mac])
        XCTAssertTrue(result.fromLegacy)
    }

    func testTheListWinsOverALeftoverSinglePairing() throws {
        let list = try JSONEncoder().encode([pc])
        let legacy = try JSONEncoder().encode(mac)
        let result = PairingList.decode(Computer.self, list: list, legacy: legacy)
        XCTAssertEqual(result.computers, [pc])
        XCTAssertFalse(result.fromLegacy)
    }

    func testAnUnreadableListFallsBackToTheSinglePairing() throws {
        let legacy = try JSONEncoder().encode(mac)
        let result = PairingList.decode(Computer.self, list: Data("not json".utf8), legacy: legacy)
        XCTAssertEqual(result.computers, [mac])
        XCTAssertTrue(result.fromLegacy)
    }

    func testNothingStoredIsNoPairing() {
        let result = PairingList.decode(Computer.self, list: nil, legacy: Data("{".utf8))
        XCTAssertEqual(result.computers, [])
        XCTAssertFalse(result.fromLegacy)
    }
}
