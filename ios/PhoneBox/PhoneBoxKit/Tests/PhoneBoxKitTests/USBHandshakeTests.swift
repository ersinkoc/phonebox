import XCTest
@testable import PhoneBoxKit

final class USBHandshakeTests: XCTestCase {
    private let nonce = String(repeating: "a1", count: 32)
    private let mac = String(repeating: "0f", count: 32)

    func testChallengeRoundTrips() {
        let line = USBHandshake.challenge(nonce: nonce)
        XCTAssertEqual(line, "PhoneBox-USB 1 \(nonce)")
        XCTAssertEqual(USBHandshake.parseChallenge(line), nonce)
    }

    func testChallengeRejectsAnotherVersionOrBadNonce() {
        XCTAssertNil(USBHandshake.parseChallenge("PhoneBox-USB 2 \(nonce)"))
        XCTAssertNil(USBHandshake.parseChallenge("PhoneBox-USB 1 short"))
        XCTAssertNil(USBHandshake.parseChallenge("PhoneBox-USB 1 \(nonce)0"))
        XCTAssertNil(USBHandshake.parseChallenge("PhoneBox-USB 1 \(String(repeating: "g", count: 64))"))
        XCTAssertNil(USBHandshake.parseChallenge("AUTH \(mac)"))
    }

    func testAuthRoundTripsAndNormalisesCase() {
        XCTAssertEqual(USBHandshake.parseAuth(USBHandshake.auth(mac: mac)), mac)
        XCTAssertEqual(USBHandshake.parseAuth("AUTH \(mac.uppercased())"), mac)
    }

    func testAuthRejectsShortOrForeignLines() {
        XCTAssertNil(USBHandshake.parseAuth("AUTH \(String(mac.dropLast()))"))
        XCTAssertNil(USBHandshake.parseAuth("GO"))
        XCTAssertNil(USBHandshake.parseAuth("PhoneBox-USB 1 \(nonce)"))
        XCTAssertNil(USBHandshake.parseAuth("AUTH \(String(repeating: "z", count: 64))"))
    }

    func testGoValue() {
        XCTAssertEqual(USBHandshake.go, "GO")
    }
}
