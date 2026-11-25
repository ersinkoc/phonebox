import SwiftUI

@main
struct PhoneBoxApp: App {
    @StateObject private var pairing = PairingStore()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(pairing)
                .preferredColorScheme(.dark)
                .tint(Color(red: 1.0, green: 0.62, blue: 0.24))
        }
    }
}
