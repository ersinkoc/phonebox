import XCTest
@testable import PhoneBoxKit

final class PairLinkTests: XCTestCase {
    private let fingerprint = "a1b2c3d4" + String(repeating: "0", count: 56)

    private func httpsLink(query: String) -> String {
        "https://ersinkoc.github.io/phonebox/pair/#\(query)"
    }

    func testParsesTheQrCodeFragmentLink() {
        let raw = httpsLink(query: "v=1&pc=Desk%20PC&fp=\(fingerprint)&port=8766&h=192.168.1.20,100.101.102.103&c=abcdefghijklmnop")
        let link = PairLink(urlString: raw)

        XCTAssertEqual(link?.computerName, "Desk PC")
        XCTAssertEqual(link?.fingerprint, fingerprint)
        XCTAssertEqual(link?.port, 8766)
        XCTAssertEqual(link?.hosts, ["192.168.1.20", "100.101.102.103"])
        XCTAssertEqual(link?.code, "abcdefghijklmnop")
        XCTAssertEqual(link?.checkCode, "A1B2 C3D4")
    }

    func testParsesThePhoneboxSchemeLink() {
        let raw = "phonebox://pair?v=1&pc=Workstation&fp=\(fingerprint.uppercased())&port=8766&h=10.0.0.5&c=ABCDEFGHIJKLMNOPQRST"
        let link = PairLink(urlString: raw)

        XCTAssertEqual(link?.computerName, "Workstation")
        XCTAssertEqual(link?.fingerprint, fingerprint, "fingerprints are stored lower-case")
        XCTAssertEqual(link?.hosts, ["10.0.0.5"])
        XCTAssertEqual(link?.port, 8766)
    }

    func testKeepsPlusAndAmpersandInTheComputerName() {
        let raw = httpsLink(query: "v=1&pc=A%2BB%20%26%20C&fp=\(fingerprint)&port=8766&h=192.168.0.2&c=abcdefghijklmnop")
        XCTAssertEqual(PairLink(urlString: raw)?.computerName, "A+B & C")
    }

    func testKeepsBase64UrlCharactersInTheCode() {
        let raw = httpsLink(query: "v=1&pc=PC&fp=\(fingerprint)&port=8766&h=10.1.2.3&c=ab_cd-ef_gh-ijkl")
        XCTAssertEqual(PairLink(urlString: raw)?.code, "ab_cd-ef_gh-ijkl")
    }

    func testDefaultsTheNameWhenTheLinkHasNone() {
        let raw = httpsLink(query: "v=1&fp=\(fingerprint)&port=8766&h=10.1.2.3&c=abcdefghijklmnop")
        XCTAssertEqual(PairLink(urlString: raw)?.computerName, "your computer")
    }

    func testDropsUnusableHostsButKeepsTheRest() {
        let raw = httpsLink(query: "v=1&fp=\(fingerprint)&port=8766&h=8.8.8.8,192.168.1.9,example.com&c=abcdefghijklmnop")
        XCTAssertEqual(PairLink(urlString: raw)?.hosts, ["192.168.1.9"])
    }

    func testRejectsMalformedLinks() {
        let bad = [
            "https://example.com/phonebox/pair/#v=1&fp=\(fingerprint)&port=8766&h=10.0.0.1&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=2&fp=\(fingerprint)&port=8766&h=10.0.0.1&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=1&fp=\(String(fingerprint.dropLast()))&port=8766&h=10.0.0.1&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=1&fp=\(fingerprint)&port=0&h=10.0.0.1&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=1&fp=\(fingerprint)&port=8766&h=8.8.8.8&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=1&fp=\(fingerprint)&port=8766&c=abcdefghijklmnop",
            "https://ersinkoc.github.io/phonebox/pair/#v=1&fp=\(fingerprint)&port=8766&h=10.0.0.1&c=short",
            "phonebox://somewhere?v=1&fp=\(fingerprint)&port=8766&h=10.0.0.1&c=abcdefghijklmnop",
            "not a link",
            "",
        ]
        for raw in bad {
            XCTAssertNil(PairLink(urlString: raw), "should reject \(raw)")
        }
    }

    func testAcceptsOnlyTheDocumentedPrivateRanges() {
        let accepted = ["10.0.0.1", "10.255.255.255", "172.16.0.1", "172.31.255.255", "192.168.1.1", "100.64.0.1", "100.127.255.255"]
        let rejected = ["8.8.8.8", "172.15.0.1", "172.32.0.1", "192.169.1.1", "100.63.0.1", "100.128.0.1",
                        "256.1.1.1", "1.2.3", "1.2.3.4.5", " 10.0.0.1", "10.0.0.01x", "localhost", ""]
        for host in accepted {
            XCTAssertTrue(PairLink.isLocalAddress(host), "should accept \(host)")
        }
        for host in rejected {
            XCTAssertFalse(PairLink.isLocalAddress(host), "should reject \(host)")
        }
    }

    func testCheckCodeUsesTheFirstEightCharacters() {
        XCTAssertEqual(PairLink.checkCode(for: "deadbeef" + String(repeating: "9", count: 56)), "DEAD BEEF")
        XCTAssertEqual(PairLink.checkCode(for: "abc"), "ABC")
    }
}
