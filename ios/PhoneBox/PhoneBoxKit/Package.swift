// swift-tools-version: 5.9
import PackageDescription

// The parts of the iOS app that can be tested without Xcode: pairing-link parsing (the same rules as
// AgentLink.kt) and the allowlist used by the native /agent proxy. The app target links this package.
let package = Package(
    name: "PhoneBoxKit",
    platforms: [.iOS(.v16), .macOS(.v12)],
    products: [
        .library(name: "PhoneBoxKit", targets: ["PhoneBoxKit"]),
    ],
    targets: [
        .target(name: "PhoneBoxKit"),
        .testTarget(name: "PhoneBoxKitTests", dependencies: ["PhoneBoxKit"]),
    ]
)
