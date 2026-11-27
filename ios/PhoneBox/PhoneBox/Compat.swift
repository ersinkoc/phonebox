import SwiftUI

// iOS 15 support, for the iPhones and iPads that can't update past iOS/iPadOS 15.
// These wrappers use the iOS 16 API when it is there and a plain equivalent otherwise.

/// `LabeledContent` replacement: a title on the left, its value on the right.
struct InfoRow: View {
    let title: String
    let value: String

    init(_ title: String, value: String) {
        self.title = title
        self.value = value
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
            Spacer(minLength: 12)
            Text(value)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.trailing)
        }
        .accessibilityElement(children: .combine)
    }
}

/// `NavigationStack` on iOS 16+, a stack-style `NavigationView` on iOS 15.
struct CompatNavigation<Content: View>: View {
    @ViewBuilder var content: () -> Content

    var body: some View {
        if #available(iOS 16, *) {
            NavigationStack(root: content)
        } else {
            NavigationView(content: content)
                .navigationViewStyle(.stack)
        }
    }
}

extension View {
    /// Medium/large sheet detents on iOS 16+; a full sheet on iOS 15.
    @ViewBuilder
    func compatMediumLargeDetents() -> some View {
        if #available(iOS 16, *) {
            presentationDetents([.medium, .large])
        } else {
            self
        }
    }
}
