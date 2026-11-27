import XCTest
@testable import PhoneBoxKit

final class PanelProxyPolicyTests: XCTestCase {
    func testPanelRequestsAreForwarded() {
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/state", hasPanelHeader: true),
            "/api/state"
        )
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(method: "POST", pathAndQuery: "/agent/api/run/abc", hasPanelHeader: true),
            "/api/run/abc"
        )
    }

    func testQueryStringsArePreserved() {
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/procs?top=3&sort=cpu", hasPanelHeader: true),
            "/api/procs?top=3&sort=cpu"
        )
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(
                method: "GET",
                pathAndQuery: "/agent/api/proxy?url=https%3A%2F%2Fexample.com%2Ffeed.xml",
                hasPanelHeader: true
            ),
            "/api/proxy?url=https%3A%2F%2Fexample.com%2Ffeed.xml"
        )
    }

    func testRequestsWithoutThePanelHeaderAreRefused() {
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/state", hasPanelHeader: false))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "POST", pathAndQuery: "/agent/api/run/abc", hasPanelHeader: false))
    }

    func testReadOnlyImagesAreAllowedWithoutThePanelHeader() {
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/art?rev=4", hasPanelHeader: false),
            "/api/art?rev=4"
        )
        XCTAssertNil(
            PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/procs/icon?name=steam", hasPanelHeader: false),
            "process icons need the panel header"
        )
        XCTAssertEqual(
            PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/procs/icon?name=steam", hasPanelHeader: true),
            "/api/procs/icon?name=steam"
        )
        XCTAssertNil(
            PanelProxyPolicy.agentRequest(method: "POST", pathAndQuery: "/agent/api/art", hasPanelHeader: false),
            "an image exception is a read-only GET"
        )
    }

    func testOnlyAgentPathsAreForwarded() {
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/api/state", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agentX/api/state", hasPanelHeader: true))
    }

    func testUnsupportedMethodsAreRefused() {
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "PUT", pathAndQuery: "/agent/api/settings", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "DELETE", pathAndQuery: "/agent/api/config", hasPanelHeader: true))
    }

    func testTraversalIsRefused() {
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/../api/settings", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/%2e%2e/settings", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api//settings", hasPanelHeader: true))
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/a\\b", hasPanelHeader: true))
    }

    func testControlCharactersAreRefused() {
        XCTAssertNil(PanelProxyPolicy.agentRequest(method: "GET", pathAndQuery: "/agent/api/state\r\nX-Bad: 1", hasPanelHeader: true))
    }
}
