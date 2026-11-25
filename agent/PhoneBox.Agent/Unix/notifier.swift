import Foundation
import UserNotifications

// PhoneBox notifier (PhoneBox.app/Contents/MacOS/PhoneBoxNotifier). Started by the PhoneBox agent, never by hand.
//   PhoneBoxNotifier --authorize [--result FILE]
//                                  asks macOS for permission to show notifications (the only mode that may show a question; it
//                                  must be started through LaunchServices, `open -W -n -a PhoneBox.app --args ...`, or macOS shows
//                                  no question and refuses; the exit code then goes to FILE, because `open` does not pass it on)
//   PhoneBoxNotifier --status      says whether it is allowed, never asking
//   PhoneBoxNotifier               reads one JSON request on stdin {"title","subtitle","body","image"(base64 PNG, optional),"id"(optional)}
//                                  and shows it, never asking
// Exit codes: 0 done (allowed / shown), 2 bad request, 3 not allowed, 4 macOS refused the notification,
//             5 no answer yet (the permission question is still open, or macOS did not reply in time).
// Exists so a phone's notification carries the PhoneBox icon and the phone app's own icon as its picture; the agent falls back
// to osascript (Script Editor's icon, no picture) whenever this does not work.

let center = UNUserNotificationCenter.current()
let finished = DispatchSemaphore(value: 0)
var code: Int32 = 5
var imageFile: URL?

/// Ends the program; an icon file macOS did not take is deleted (one it took was moved into its own store, with the notification).
func finish(_ status: Int32, _ message: String? = nil) -> Never {
    if let message = message { FileHandle.standardError.write(Data((message + "\n").utf8)) }
    if let index = CommandLine.arguments.firstIndex(of: "--result"), index + 1 < CommandLine.arguments.count {
        try? "\(status)".write(toFile: CommandLine.arguments[index + 1], atomically: true, encoding: .utf8)
    }
    if let file = imageFile { try? FileManager.default.removeItem(at: file) }
    exit(status)
}

let arguments = CommandLine.arguments
let authorizeMode = arguments.contains("--authorize")
let statusMode = arguments.contains("--status")
var request: [String: Any] = [:]
if !authorizeMode && !statusMode {
    let input = FileHandle.standardInput.readDataToEndOfFile()
    guard let parsed = try? JSONSerialization.jsonObject(with: input) as? [String: Any] else { finish(2, "not a request") }
    request = parsed
}

func show() {
    let content = UNMutableNotificationContent()
    content.title = request["title"] as? String ?? ""
    content.subtitle = request["subtitle"] as? String ?? ""
    content.body = request["body"] as? String ?? ""
    if let encoded = request["image"] as? String, let png = Data(base64Encoded: encoded) {
        // the system moves the attachment file away, so every notification gets a file of its own
        let file = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString + ".png")
        if (try? png.write(to: file)) != nil {
            imageFile = file
            if let attachment = try? UNNotificationAttachment(identifier: "image", url: file, options: [UNNotificationAttachmentOptionsTypeHintKey: "public.png"]) {
                content.attachments = [attachment]
            } else {
                try? FileManager.default.removeItem(at: file) // a picture macOS will not take is shown without it
                imageFile = nil
            }
        }
    }
    // once macOS has the notification it is shown, whether or not it answers in time: a late answer must not make the agent show it again
    code = 0
    // a request with the same identifier replaces the notification already shown (a call whose caller was named after it first rang)
    center.add(UNNotificationRequest(identifier: (request["id"] as? String).flatMap { $0.isEmpty ? nil : "phonebox-" + $0 } ?? UUID().uuidString, content: content, trigger: nil)) { error in
        if let error = error { code = 4; FileHandle.standardError.write(Data("could not show it: \(error.localizedDescription)\n".utf8)) }
        finished.signal()
    }
}

if authorizeMode {
    center.requestAuthorization(options: [.alert, .sound]) { granted, _ in
        code = granted ? 0 : 3
        finished.signal()
    }
    _ = finished.wait(timeout: .now() + 150)
    finish(code)
}

// --status and showing never raise a question: they only look at what the person decided
center.getNotificationSettings { settings in
    switch settings.authorizationStatus {
    case .authorized, .provisional:
        if statusMode { code = 0; finished.signal() } else { show() }
    case .denied:
        code = 3
        finished.signal()
    default:
        code = 5
        finished.signal()
    }
}
_ = finished.wait(timeout: .now() + 8)
finish(code)
