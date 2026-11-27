import SwiftUI
import PhoneBoxKit

struct RootView: View {
    @EnvironmentObject private var pairing: PairingStore

    var body: some View {
        Group {
            if let computer = pairing.computer {
                // A new id rebuilds the panel: switching computers starts a fresh page with that computer's layout.
                PanelScreen(computer: computer, others: pairing.others)
                    .id(computer.id)
            } else {
                UnpairedView()
            }
        }
    }
}

/// The panel shares the screen with a toolbar for the paired computers: details, switching, pairing and Forget.
private struct PanelScreen: View {
    let computer: PairedComputer
    let others: [PairedComputer]
    @EnvironmentObject private var pairing: PairingStore
    @State private var showingOptions = false
    @StateObject private var usb: USBLink

    init(computer: PairedComputer, others: [PairedComputer]) {
        self.computer = computer
        self.others = others
        _usb = StateObject(wrappedValue: USBLink(computer: computer))
    }

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
            PanelView(computer: computer, others: others, usb: usb) { found in
                pairing.activate(found.id)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .sheet(isPresented: $showingOptions) {
            ComputerOptionsView(computer: computer, usb: usb)
                .environmentObject(pairing)
        }
    }
}

private struct ComputerOptionsView: View {
    let computer: PairedComputer
    @ObservedObject var usb: USBLink
    @EnvironmentObject private var pairing: PairingStore
    @Environment(\.dismiss) private var dismiss
    @State private var addingComputer = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    ForEach(pairing.computers) { item in
                        Button {
                            pairing.activate(item.id)
                            dismiss()
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(item.name).foregroundStyle(.primary)
                                    Text(item.hosts.joined(separator: ", "))
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                if item.id == computer.id {
                                    Image(systemName: "checkmark").foregroundStyle(.tint)
                                }
                            }
                        }
                    }
                    .onDelete { offsets in
                        for index in offsets { pairing.forget(pairing.computers[index].id) }
                    }
                    Button {
                        addingComputer = true
                    } label: {
                        Label("Pair another computer", systemImage: "plus.circle")
                    }
                } header: {
                    Text("Computers")
                } footer: {
                    Text("When the computer on screen stops answering, PhoneBox switches to another paired computer that answers. Pair each operating system of a dual-boot PC once (Windows and macOS run separate agents), and the panel follows whichever is running.")
                }
                Section("This computer") {
                    LabeledContent("Name", value: computer.name)
                    LabeledContent("Addresses", value: computer.hosts.joined(separator: ", "))
                    LabeledContent("Port", value: String(computer.port))
                }
                Section("Certificate") {
                    Text(computer.fingerprint)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                    LabeledContent("Check code") {
                        Text(computer.checkCode).monospaced()
                    }
                }
                Section {
                    LabeledContent("USB cable", value: usb.isConnected ? "Connected" : "Not connected")
                } footer: {
                    Text("The desktop agent carries the cable link automatically; keep it running. USB reuses this pairing's certificate and token, so no Wi-Fi network is needed once the app is paired.")
                }
                Section {
                    Button("Forget this computer", role: .destructive) {
                        pairing.forget(computer.id)
                        dismiss()
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
        .presentationDetents([.medium, .large])
        .sheet(isPresented: $addingComputer) {
            UnpairedView(addingAnother: true) { addingComputer = false }
                .environmentObject(pairing)
        }
    }
}
