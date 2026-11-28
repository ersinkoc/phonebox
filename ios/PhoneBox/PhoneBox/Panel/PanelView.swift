import SwiftUI
import WebKit

/// The PhoneBox panel (the same HTML/JS the Android app ships) in a WKWebView.
///
/// The page is served from the app bundle under a private `phonebox://localhost/www/…` origin, so it behaves
/// like a normal web page (modules, localStorage) while no file URL APIs exist. `/agent/*` requests never leave
/// the app as WebView requests: images go through the scheme handler and `fetch()` through the message bridge,
/// and both use the native AgentClient, which adds the Bearer token and pins the certificate.
struct PanelView: UIViewRepresentable {
    let computer: PairedComputer
    /// The other paired computers, asked when this one stops answering.
    let others: [PairedComputer]
    /// The user's own pick when this is another computer: asked now and then, and returned to once it answers.
    let preferred: PairedComputer?
    let usb: USBLink
    let onSwitch: @MainActor (_ from: String, _ to: PairedComputer) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(computer: computer, others: others, preferred: preferred, usb: usb, onSwitch: onSwitch)
    }

    func makeUIView(context: Context) -> WKWebView {
        context.coordinator.makeWebView()
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {}

    static func dismantleUIView(_ uiView: WKWebView, coordinator: Coordinator) {
        coordinator.shutdown()
    }

    final class Coordinator: NSObject, WKNavigationDelegate, WKUIDelegate {
        private let client: AgentClient
        private let usb: USBLink
        private let schemeHandler: PanelSchemeHandler
        private let proxyHandler: PanelProxyHandler
        private let bridgeKey: String
        private var webView: WKWebView?
        private var loaded = false
        private var resumeObserver: NSObjectProtocol?

        init(
            computer: PairedComputer,
            others: [PairedComputer],
            preferred: PairedComputer?,
            usb: USBLink,
            onSwitch: @escaping @MainActor (_ from: String, _ to: PairedComputer) -> Void
        ) {
            let client = AgentClient(computer: computer, others: others, preferred: preferred, onSwitch: onSwitch)
            let key = UUID().uuidString
            self.client = client
            self.usb = usb
            self.schemeHandler = PanelSchemeHandler(client: client)
            self.proxyHandler = PanelProxyHandler(client: client, bridgeKey: key)
            self.bridgeKey = key
            super.init()
            // iOS suspends the app in the background: failures before and after that are not one outage
            resumeObserver = NotificationCenter.default.addObserver(
                forName: UIApplication.willEnterForegroundNotification, object: nil, queue: .main
            ) { [weak client] _ in client?.appResumed() }
        }

        func makeWebView() -> WKWebView {
            if let webView { return webView }

            let configuration = WKWebViewConfiguration()
            configuration.setURLSchemeHandler(schemeHandler, forURLScheme: PanelSchemeHandler.scheme)
            configuration.websiteDataStore = .default()
            configuration.allowsInlineMediaPlayback = true
            configuration.mediaTypesRequiringUserActionForPlayback = []
            configuration.defaultWebpagePreferences.allowsContentJavaScript = true

            let controller = configuration.userContentController
            let shim = Self.panelShim(bridgeKey: bridgeKey)
            controller.addUserScript(WKUserScript(source: shim, injectionTime: .atDocumentStart, forMainFrameOnly: true))
            let languages = Self.languagesJSON()
            controller.addUserScript(WKUserScript(
                source: "window.__phoneboxLanguages = \(languages); window.__phoneboxIOS = true;",
                injectionTime: .atDocumentStart,
                forMainFrameOnly: true
            ))
            controller.addScriptMessageHandler(proxyHandler, contentWorld: .page, name: PanelProxyHandler.messageName)

            let webView = WKWebView(frame: .zero, configuration: configuration)
            webView.navigationDelegate = self
            webView.uiDelegate = self
            webView.isOpaque = false
            webView.backgroundColor = .black
            webView.scrollView.backgroundColor = .black
            webView.scrollView.bounces = false
            webView.scrollView.contentInsetAdjustmentBehavior = .never
            webView.allowsBackForwardNavigationGestures = false
            webView.allowsLinkPreview = false

            self.webView = webView
            usb.start()
            load(webView)
            return webView
        }

        private func load(_ webView: WKWebView) {
            guard !loaded else { return }
            loaded = true
            guard let url = URL(string: "\(PanelSchemeHandler.scheme)://\(PanelSchemeHandler.host)/www/index.html") else { return }
            webView.load(URLRequest(url: url))
        }

        func shutdown() {
            if let resumeObserver { NotificationCenter.default.removeObserver(resumeObserver) }
            resumeObserver = nil
            usb.stop()
            client.invalidate()
            webView?.stopLoading()
            webView?.configuration.userContentController.removeScriptMessageHandler(forName: PanelProxyHandler.messageName)
            webView?.navigationDelegate = nil
            webView?.uiDelegate = nil
            webView = nil
        }

        private static func panelShim(bridgeKey: String) -> String {
            let source: String
            if let url = Bundle.main.url(forResource: "PanelShim", withExtension: "js"),
               let text = try? String(contentsOf: url, encoding: .utf8) {
                source = text
            } else {
                source = ""
            }
            let quotedKey = bridgeKey.replacingOccurrences(of: "'", with: "")
            return "window.__phoneboxBridgeKey = '\(quotedKey)';\n" + source
        }

        private static func languagesJSON() -> String {
            let data = (try? JSONSerialization.data(withJSONObject: Locale.preferredLanguages)) ?? Data("[]".utf8)
            return String(data: data, encoding: .utf8) ?? "[]"
        }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            guard let url = navigationAction.request.url else {
                decisionHandler(.cancel)
                return
            }
            if url.scheme == PanelSchemeHandler.scheme, url.host == PanelSchemeHandler.host {
                decisionHandler(.allow)
                return
            }
            if url.absoluteString == "about:srcdoc", navigationAction.targetFrame?.isMainFrame == false {
                decisionHandler(.allow)
                return
            }
            // Links the panel opens (widget shortcuts, Steam pages, …) belong to the system browser; the panel
            // itself stays on its own origin.
            if navigationAction.navigationType == .linkActivated {
                UIApplication.shared.open(url)
            }
            decisionHandler(.cancel)
        }

        func webView(
            _ webView: WKWebView,
            createWebViewWith configuration: WKWebViewConfiguration,
            for navigationAction: WKNavigationAction,
            windowFeatures: WKWindowFeatures
        ) -> WKWebView? {
            if let url = navigationAction.request.url { UIApplication.shared.open(url) }
            return nil
        }
    }
}
