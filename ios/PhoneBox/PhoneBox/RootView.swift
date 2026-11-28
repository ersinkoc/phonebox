import SwiftUI
import PhoneBoxKit

struct RootView: View {
    @EnvironmentObject private var pairing: PairingStore

    var body: some View {
        Group {
            if let computer = pairing.computer {
                PanelScreen(computer: computer)
            } else {
                UnpairedView()
            }
        }
    }
}

/// What the panel's client is built from; when any of it changes, the panel and its client are built again.
private struct PanelIdentity: Hashable {
    let shown: PairedComputer
    let pick: String?
    let paired: [PairedComputer]
}

/// The cable state of the current panel, for the options sheet, which outlives any one panel.
private final class USBStatus: ObservableObject {
    @Published var isConnected = false
}

/// The panel shares the screen with a toolbar for the paired computers: details, switching, pairing and Forget.
/// The toolbar and its sheets stay put while the panel under them is built again, so a switch of computers never
/// closes the options sheet or loses a pairing link being entered there.
private struct PanelScreen: View {
    let computer: PairedComputer
    @EnvironmentObject private var pairing: PairingStore
    @State private var showingOptions = false
    @StateObject private var usbStatus = USBStatus()

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Spacer(minLength: 0)
                Button {
                    showingOptions = true
                } label: {
                    Image(systemName: "ellipsis.circle")
                        .font(.system(size: 19))
                        .frame(width: 44, height: 44)
                        .contentShape(Rectangle())
                }
                .accessibilityLabel("Paired computers")
            }
            .padding(.horizontal, 4)
            .background(.bar)
            // A new identity builds the panel again with a fresh client and USB link: switching computers (a page with
            // that computer's layout), pairing or forgetting one (the others to ask), pairing the same computer again
            // (new addresses or token), or a new pick (the computer to go back to, even when the pick is the computer
            // already on screen). An outstanding probe or pick check of the old client is discarded.
            PanelBody(computer: computer, others: pairing.others, preferred: pairing.pickElsewhere, status: usbStatus)
                .id(PanelIdentity(shown: computer, pick: pairing.preferredID, paired: pairing.computers))
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .sheet(isPresented: $showingOptions) {
            ComputerOptionsView(computer: computer, usb: usbStatus)
                .environmentObject(pairing)
        }
    }
}

/// The part built again with each identity: the WebView, its client and the USB link.
private struct PanelBody: View {
    let computer: PairedComputer
    let others: [PairedComputer]
    let preferred: PairedComputer?
    let status: USBStatus
    @EnvironmentObject private var pairing: PairingStore
    @StateObject private var usb: USBLink

    init(computer: PairedComputer, others: [PairedComputer], preferred: PairedComputer?, status: USBStatus) {
        self.computer = computer
        self.others = others
        self.preferred = preferred
        self.status = status
        _usb = StateObject(wrappedValue: USBLink(computer: computer))
    }

    var body: some View {
        PanelView(computer: computer, others: others, preferred: preferred, usb: usb) { from, found in
            pairing.follow(from: from, to: found.id)
        }
        .onReceive(usb.$isConnected) { status.isConnected = $0 }
    }
}

private struct ComputerOptionsView: View {
    let computer: PairedComputer
    @ObservedObject var usb: USBStatus
    @EnvironmentObject private var pairing: PairingStore
    @Environment(\.dismiss) private var dismiss
    @State private var addingComputer = false

    var body: some View {
        CompatNavigation {
            Form {
                if let message = pairing.errorMessage {
                    Section {
                        Label(message, systemImage: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                    }
                }
                Section {
                    ForEach(pairing.computers) { item in
                        Button {
                            pairing.choose(item.id)
                            dismiss()
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(item.name).foregroundStyle(.primary)
                                    Text(item.hosts.joined(separator: ", "))
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                    if item.id == pairing.preferredID, item.id != computer.id {
                                        Text("Your pick: shown again once it answers")
                                            .font(.caption)
                                            .foregroundStyle(.orange)
                                    }
                                }
                                Spacer()
                                if item.id == computer.id {
                                    Image(systemName: "checkmark").foregroundStyle(.tint)
                                }
                            }
                        }
                    }
                    .onDelete { offsets in
                        // offsets point into the list as it is now: turn them into ids before anything is removed
                        pairing.forget(Set(offsets.map { pairing.computers[$0].id }))
                    }
                    Button {
                        addingComputer = true
                    } label: {
                        Label("Pair another computer", systemImage: "plus.circle")
                    }
                } header: {
                    Text("Computers")
                } footer: {
                    Text("Tap a computer to show it. When it hasn't answered for 30 seconds, PhoneBox shows another paired computer that answers, and goes back to yours once it answers again. Pair each operating system of a dual-boot PC once (Windows and macOS run separate agents), and the panel follows whichever is running.")
                }
                Section("This computer") {
                    InfoRow("Name", value: computer.name)
                    InfoRow("Addresses", value: computer.hosts.joined(separator: ", "))
                    InfoRow("Port", value: String(computer.port))
                }
                Section("Certificate") {
                    Text(computer.fingerprint)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                    InfoRow("Check code", value: computer.checkCode)
                        .font(.system(.body, design: .monospaced))
                }
                Section {
                    InfoRow("USB cable", value: usb.isConnected ? "Connected" : "Not connected")
                } footer: {
                    Text("The desktop agent carries the cable link automatically; keep it running. USB reuses this pairing's certificate and token, so no Wi-Fi network is needed once the app is paired.")
                }
                Section {
                    Button("Forget this computer", role: .destructive) {
                        pairing.forget(computer.id)
                        if pairing.errorMessage == nil { dismiss() }
                    }
                } footer: {
                    Text("Pairings live in the iOS Keychain. Forgetting one removes that computer's agent token from this iPhone.")
                }
            }
            .navigationTitle("Paired computers")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Done") { dismiss() }
                }
            }
        }
        .compatMediumLargeDetents()
        .sheet(isPresented: $addingComputer) {
            UnpairedView(addingAnother: true) { addingComputer = false }
                .environmentObject(pairing)
        }
    }
}
