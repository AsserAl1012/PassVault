import PassVaultKit
import SwiftUI
import UIKit
import UniformTypeIdentifiers

struct VaultView: View {
    @EnvironmentObject private var vault: PhoneVault
    @State private var toast = ""

    var body: some View {
        NavigationStack {
            Group {
                if vault.shown.isEmpty {
                    // ContentUnavailableView would be neater but it is iOS 17 only.
                    VStack(spacing: 10) {
                        Image(systemName: "key").font(.largeTitle)
                        Text(vault.search.isEmpty ? "Nothing saved yet" : "Nothing matches that")
                    }
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else {
                    List(vault.shown) { entry in
                        NavigationLink {
                            EntryView(entry: entry, toast: $toast)
                        } label: {
                            VStack(alignment: .leading, spacing: 3) {
                                Text(entry.name).fontWeight(.medium)
                                if !entry.who.isEmpty {
                                    Text(entry.who).font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
            }
            .searchable(text: $vault.search, prompt: "Search your credentials")
            .navigationTitle("PassVault")
            .toolbar {
                // .topBarTrailing is iOS 17 only.
                ToolbarItem(placement: .navigationBarTrailing) {
                    Button("Lock") { vault.lock() }
                }
            }
            .safeAreaInset(edge: .bottom) {
                if !toast.isEmpty {
                    Text(toast)
                        .font(.footnote)
                        .foregroundStyle(.green)
                        .padding(.bottom, 6)
                }
            }
        }
    }
}

struct EntryView: View {
    let entry: Entry
    @Binding var toast: String

    @State private var showingPassword = false

    var body: some View {
        List {
            if let username = entry.username, !username.isEmpty {
                field("Username", username)
            }
            if let email = entry.email, !email.isEmpty {
                field("Email", email)
            }
            if let password = entry.password, !password.isEmpty {
                Section("Password") {
                    HStack {
                        Text(showingPassword ? password : String(repeating: "\u{2022}", count: 10))
                            .font(.system(.body, design: .monospaced))
                        Spacer()
                        Button {
                            showingPassword.toggle()
                        } label: {
                            Image(systemName: showingPassword ? "eye.slash" : "eye")
                        }
                        .buttonStyle(.plain)
                        Button {
                            copy(password, "Password")
                        } label: {
                            Image(systemName: "doc.on.doc")
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            if let notes = entry.notes, !notes.isEmpty {
                Section("Notes") { Text(notes) }
            }

            Section {
                Text("Read only on the phone. Change credentials in the Windows app, then bring the vault file over again.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .navigationTitle(entry.name)
        .navigationBarTitleDisplayMode(.inline)
    }

    private func field(_ label: String, _ value: String) -> some View {
        Section(label) {
            HStack {
                Text(value)
                Spacer()
                Button {
                    copy(value, label)
                } label: {
                    Image(systemName: "doc.on.doc")
                }
                .buttonStyle(.plain)
            }
        }
    }

    /// iOS can expire a clipboard item by itself, which is better than us trying to wipe it
    /// later - it works even if the app has been closed by then.
    private func copy(_ value: String, _ what: String) {
        // .localOnly keeps it off Universal Clipboard. Without it the password lands on
        // every Mac and iPad on the same Apple ID, where this phone's 20-second expiry
        // does not reach it and the message below would be a lie.
        UIPasteboard.general.setItems(
            [[UTType.utf8PlainText.identifier: value]],
            options: [.expirationDate: Date().addingTimeInterval(20), .localOnly: true]
        )
        toast = "\(what) copied - the clipboard clears in 20 seconds"

        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 4_000_000_000)
            toast = ""
        }
    }
}
