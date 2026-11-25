import SwiftUI
import PhoneBoxKit

/// Shown before anything is sent to the computer. The check code is the short value the settings page shows
/// next to the QR code, so a link that came from someone else is easy to spot.
struct PairConfirmationView: View {
    let link: PairLink
    let busy: Bool
    var onCancel: () -> Void
    var onConfirm: () -> Void

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    Text("Pair with this computer?")
                        .font(.largeTitle.bold())
                    Text(link.computerName)
                        .font(.title2.weight(.semibold))

                    Group {
                        LabeledContent("Addresses", value: link.hosts.joined(separator: ", "))
                        LabeledContent("Port", value: String(link.port))
                    }
                    .font(.subheadline)

                    VStack(alignment: .leading, spacing: 6) {
                        Text("Check code")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text(link.checkCode)
                            .font(.system(size: 34, weight: .bold, design: .monospaced))
                        Text("This must match the code shown next to the QR code on the computer. If it does not, cancel: the link did not come from your computer.")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    .padding(14)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(RoundedRectangle(cornerRadius: 14).fill(Color.white.opacity(0.06)))

                    VStack(alignment: .leading, spacing: 6) {
                        Text("Certificate fingerprint (SHA-256)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Text(link.fingerprint)
                            .font(.system(.caption, design: .monospaced))
                            .textSelection(.enabled)
                    }

                    HStack(spacing: 12) {
                        Button("Cancel", role: .cancel, action: onCancel)
                            .buttonStyle(.bordered)
                            .controlSize(.large)
                            .disabled(busy)
                        Button(action: onConfirm) {
                            Text(busy ? "Pairing…" : "Pair")
                                .frame(maxWidth: .infinity)
                        }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.large)
                        .disabled(busy)
                    }
                }
                .padding(24)
                .frame(maxWidth: 620, alignment: .leading)
                .frame(maxWidth: .infinity)
            }

            if busy {
                Color.black.opacity(0.55).ignoresSafeArea()
                VStack(spacing: 12) {
                    ProgressView().tint(.white)
                    Text("Checking the certificate and pairing…")
                        .font(.footnote)
                }
                .padding(20)
                .background(RoundedRectangle(cornerRadius: 14).fill(Color.white.opacity(0.1)))
            }
        }
    }
}
