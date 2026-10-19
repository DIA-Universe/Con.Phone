import SwiftUI

@main
struct ConPhoneCompanionApp: App {
    @StateObject private var bridge = ClipboardBridge()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(bridge)
        }
    }
}
