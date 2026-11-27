import SwiftUI
import PhoneBoxKit

struct UnpairedView: View {
    @EnvironmentObject private var pairing: PairingStore
    @State private var pendingLink: PairLink?
    @State private var showScanner = false
    @State private var showPaste = false
    @State private var pastedLink = ""

    var body: some View {
        Group {
            if let link = pendingLink {
                PairConfirmationView(
                    link: link,
                    busy: pairing.isPairing,
                    onCancel: { pendingLink = nil },
                    onConfirm: { startPairing(link) }
                )
            } else {
                welcome
            }
        }
    }

    private var welcome: some View {
        ZStack {
            background
            ScrollView {
                VStack(alignment: .leading, spacing: 22) {
                    header
                    steps
                    if let message = pairing.errorMessage { errorBox(message) }
                    buttons
                    footer
                }
                .padding(24)
                .frame(maxWidth: 620, alignment: .leading)
                .frame(maxWidth: .infinity)
            }
        }
        .sheet(isPresented: $showScanner) {
            QRScannerSheet(
                onResult: { value in
                    showScanner = false
                    accept(value)
                },
                onCancel: { showScanner = false }
            )
        }
        .sheet(isPresented: $showPaste) { pasteSheet }
    }

    private var background: some View {
        LinearGradient(
            colors: [Color(red: 0.07, green: 0.08, blue: 0.10), .black],
            startPoint: .top,
            endPoint: .bottom
        )
        .ignoresSafeArea()
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Image(systemName: "square.grid.2x2.fill")
                    .font(.system(size: 28, weight: .semibold))
                    .foregroundStyle(Color(red: 1.0, green: 0.62, blue: 0.24))
                Text("PhoneBox")
                    .font(.system(size: 34, weight: .bold, design: .rounded))
            }
            Text("This iPhone is not paired with a computer yet.")
                .font(.title3.weight(.medium))
            Text("Pair a computer to show its PhoneBox panel here, full screen.")
                .foregroundStyle(.secondary)
        }
    }

    private var steps: some View {
        VStack(alignment: .leading, spacing: 12) {
            step(1, "On the computer, open PhoneBox settings → Wi-Fi & Tailscale and switch on “Let the phone connect without the USB cable”.")
            step(2, "Choose “Pair a phone over Wi-Fi”. The settings page shows a QR code and a short check code.")
            step(3, "Scan the QR code with this iPhone and confirm that the check code matches the one on the computer.")
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 16).fill(Color.white.opacity(0.06)))
    }

    private func step(_ number: Int, _ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text("\(number)")
                .font(.caption.bold())
                .frame(width: 22, height: 22)
                .background(Circle().fill(Color.white.opacity(0.12)))
            Text(text)
                .font(.subheadline)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func errorBox(_ message: String) -> some View {
        Label {
            Text(message)
                .font(.footnote)
                .fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: "exclamationmark.triangle.fill")
        }
        .foregroundStyle(Color(red: 1.0, green: 0.74, blue: 0.36))
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.orange.opacity(0.12)))
    }

    private var buttons: some View {
        VStack(spacing: 12) {
            Button {
                showScanner = true
            } label: {
                Label("Scan the pairing QR code", systemImage: "qrcode.viewfinder")
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 4)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)

            Button {
                showPaste = true
            } label: {
                Label("Paste a pairing link", systemImage: "link")
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 4)
            }
            .buttonStyle(.bordered)
            .controlSize(.large)
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("The iPhone must be on the same Wi-Fi or Tailscale network as the computer. iOS may ask for Local Network access the first time; allow it.")
            Text("Pair over Wi-Fi or Tailscale. After pairing, an iPhone can also connect over USB through a desktop agent with iOS USB support. Android-only panel features (light and proximity sensors, phone battery, dictation, haptics, volume-key paging and forced landscape) are not implemented.")
        }
        .font(.footnote)
        .foregroundStyle(.secondary)
    }

    @ViewBuilder
    private var pasteField: some View {
        if #available(iOS 16, *) {
            TextField("phonebox://pair?v=1&…", text: $pastedLink, axis: .vertical)
                .lineLimit(3...6)
        } else {
            TextEditor(text: $pastedLink)
                .frame(minHeight: 88)
        }
    }

    private var pasteSheet: some View {
        CompatNavigation {
            Form {
                Section("Pairing link") {
                    pasteField
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .font(.system(.footnote, design: .monospaced))
                }
                Section {
                    Button("Use this link") {
                        let value = pastedLink
                        showPaste = false
                        accept(value)
                    }
                    .disabled(pastedLink.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                } footer: {
                    Text("Scanning is easier. The link contains a one-time pairing code, so do not share it.")
                }
            }
            .navigationTitle("Paste pairing link")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { showPaste = false }
                }
            }
        }
        .compatMediumLargeDetents()
    }

    private func accept(_ value: String) {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let link = PairLink(urlString: trimmed) else {
            pairing.errorMessage = "That is not a PhoneBox pairing link. Scan the QR code shown by the computer, or paste the whole link (it starts with phonebox://pair or https://ersinkoc.github.io/phonebox/pair/)."
            return
        }
        pairing.errorMessage = nil
        pastedLink = ""
        pendingLink = link
    }

    private func startPairing(_ link: PairLink) {
        Task { @MainActor in
            await pairing.pair(with: link)
            if pairing.computer == nil { pendingLink = nil }
        }
    }
}
