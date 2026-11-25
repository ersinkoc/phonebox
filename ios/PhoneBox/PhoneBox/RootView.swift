import SwiftUI
import PhoneBoxKit

struct RootView: View {
    @EnvironmentObject private var pairing: PairingStore

    var body: some View {
        Group {
            if let computer = pairing.computer {
                PanelScreen(computer: computer)
                    .id(computer.id)
            } else {
                UnpairedView()
            }
        }
    }
}

/// The panel fills the screen. A small options button opens the paired computer's details and Forget.
private struct PanelScreen: View {
    let computer: PairedComputer
    @EnvironmentObject private var pairing: PairingStore
    @State private var showingOptions = false
    @StateObject private var usb: USBLink

    init(computer: PairedComputer) {
        self.computer = computer
        _usb = StateObject(wrappedValue: USBLink(computer: computer))
    }

    var body: some View {
        ZStack(alignment: .topTrailing) {
            PanelView(computer: computer, usb: usb)
                .ignoresSafeArea()

            Button {
                showingOptions = true
            } label: {
                Image(systemName: "ellipsis.circle.fill")
                    .font(.system(size: 17))
                    .symbolRenderingMode(.palette)
                    .foregroundStyle(.white.opacity(0.8), .black.opacity(0.45))
                    .padding(10)
            }
            .accessibilityLabel("Paired computer options")
        }
        .sheet(isPresented: $showingOptions) {
            ComputerOptionsView(computer: computer, usb: usb) {
                showingOptions = false
                pairing.forget()
            }
        }
    }
}

private struct ComputerOptionsView: View {
    let computer: PairedComputer
    @ObservedObject var usb: USBLink
    var onForget: () -> Void
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Form {
                Section("Computer") {
                    LabeledContent("Name", value: computer.name)
                    LabeledContent("Addresses", value: computer.hosts.joined(separator: ", "))
                    LabeledContent("Port", value: String(computer.port))
                }
                Section("Certificate") {
                    Text(computer.fingerprint)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                    LabeledContent("Check code", value: computer.checkCode)
                        .font(.system(.body, design: .monospaced))
                }
                Section {
                    LabeledContent("USB cable", value: usb.isConnected ? "Connected" : "Not connected")
                } footer: {
                    Text("The desktop agent carries the cable link automatically; keep it running. USB reuses this pairing's certificate and token, so no Wi-Fi network is needed once the app is paired.")
                }
                Section {
                    Button("Forget this computer", role: .destructive) {
                        onForget()
                        dismiss()
                    }
                } footer: {
                    Text("The pairing lives in the iOS Keychain. Forgetting it removes the agent token from this iPhone.")
                }
            }
            .navigationTitle("Paired computer")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .presentationDetents([.medium, .large])
    }
}
