import SwiftUI

@main
struct PassVaultApp: App {
    @StateObject private var vault = PhoneVault()
    @Environment(\.scenePhase) private var scenePhase
    @State private var covered = false

    var body: some Scene {
        WindowGroup {
            Group {
                if vault.isOpen && !vault.offerToRemember {
                    VaultView()
                } else {
                    UnlockView()
                }
            }
            .environmentObject(vault)
            .preferredColorScheme(.dark)
            .tint(Color(red: 0.30, green: 0.55, blue: 1.0))
            // iOS takes the app-switcher snapshot around .inactive, which can catch the
            // credential list before SwiftUI has swapped screens. Cover it rather than lock
            // early - locking at .inactive would fight the Face ID sheet and the file picker.
            .overlay { if covered { Color.black.ignoresSafeArea() } }
        }
        .onChange(of: scenePhase) { phase in
            covered = phase != .active
            if phase == .background { vault.lock() }
        }
    }
}
