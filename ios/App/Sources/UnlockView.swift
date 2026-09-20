import PassVaultKit
import SwiftUI
import UniformTypeIdentifiers

@MainActor
struct UnlockView: View {
    @EnvironmentObject private var vault: PhoneVault

    @State private var phrase = ""
    @State private var pin = ""
    @State private var newPin = ""
    @State private var newPinAgain = ""
    @State private var message = ""
    @State private var busy = false
    @State private var importing = false
    @State private var showingPhrase = false

    var body: some View {
        VStack(spacing: 22) {
            Spacer(minLength: 12)

            Image(systemName: "lock.shield")
                .font(.system(size: 44))
                .foregroundStyle(.tint)
            Text("PassVault").font(.largeTitle.weight(.semibold))

            if busy {
                ProgressView("Opening...").padding(.top, 30)
            } else if vault.offerToRemember {
                rememberOnThisPhone
            } else if !vault.hasVault {
                importFirst
            } else if vault.canUseFaceId || vault.canUsePin {
                everyday
            } else {
                phraseEntry(title: "Set this phone up", note: "Type your 12 words once.")
            }

            if !message.isEmpty {
                Text(message)
                    .font(.footnote)
                    .foregroundStyle(.red)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal)
            }

            Spacer()

            // Always reachable. Without this there is no way to bring over a newer vault, and
            // no way out at all if the stored one ever ends up unreadable.
            if !busy && vault.hasVault && !vault.offerToRemember {
                VStack(spacing: 6) {
                    Button("Bring over a newer vault file") { importing = true }
                    Button("Forget this phone", role: .destructive) {
                        vault.forgetEverything()
                        message = ""
                    }
                }
                .font(.footnote)
            }
        }
        .padding()
        .fileImporter(isPresented: $importing, allowedContentTypes: [UTType.data]) { result in
            switch result {
            case .success(let url):
                do {
                    try vault.importVault(from: url)
                    message = ""
                } catch {
                    message = error.localizedDescription
                }
            case .failure(let error):
                message = error.localizedDescription
            }
        }
    }

    // ---- first run ----

    private var importFirst: some View {
        VStack(spacing: 16) {
            Text("Bring your vault over from the Windows app. Use the Back up button there, then AirDrop or save the file to this phone.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            Button("Choose your vault file") { importing = true }
                .buttonStyle(.borderedProminent)
        }
    }

    // ---- the everyday unlock ----

    private var everyday: some View {
        VStack(spacing: 18) {
            if vault.canUseFaceId {
                Button {
                    run { try await vault.openWithFaceId() }
                } label: {
                    Label("Unlock with Face ID", systemImage: "faceid")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
            }

            if vault.canUsePin {
                VStack(spacing: 8) {
                    Text("or your PIN").font(.footnote).foregroundStyle(.secondary)

                    SecureField("PIN", text: $pin)
                        .keyboardType(.numberPad)
                        .multilineTextAlignment(.center)
                        .font(.title2)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 160)
                        .onChange(of: pin) { typed in
                            guard typed.count == 4 else { return }
                            pin = ""
                            run { try await vault.openWithPin(typed) }
                        }

                    if vault.triesLeft < PhoneVault.maxAttempts {
                        Text("\(vault.triesLeft) tries left").font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }

            if showingPhrase {
                phraseEntry(title: "Your 12 words", note: "This always works, and switches the PIN back on.")
                Button("Back to PIN") { showingPhrase = false; phrase = "" }
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            } else {
                Button("Use my 12 words") { showingPhrase = true }
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
    }

    // ---- the 12 words ----

    private func phraseEntry(title: String, note: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title).font(.headline)
            Text(note).font(.footnote).foregroundStyle(.secondary)

            TextEditor(text: $phrase)
                .font(.system(.body, design: .monospaced))
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .frame(height: 96)
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(.secondary.opacity(0.4)))

            Button("Open my vault") {
                let typed = phrase
                run {
                    try await vault.openWithPhrase(typed)
                    phrase = ""
                }
            }
            .buttonStyle(.borderedProminent)
            .frame(maxWidth: .infinity)
        }
    }

    // ---- turning on Face ID and a PIN ----

    private var rememberOnThisPhone: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Remember this phone").font(.headline)
            Text("Turn on Face ID and pick a 4-digit PIN, so you do not need the 12 words every time.")
                .font(.footnote).foregroundStyle(.secondary)

            SecureField("PIN", text: $newPin)
                .keyboardType(.numberPad).textFieldStyle(.roundedBorder)
            SecureField("Type it again", text: $newPinAgain)
                .keyboardType(.numberPad).textFieldStyle(.roundedBorder)

            Button("Turn it on") {
                guard newPin.count == 4, newPin.allSatisfy(\.isNumber) else {
                    message = "A PIN is exactly 4 digits."; return
                }
                guard newPin == newPinAgain else { message = "Those two PINs are not the same."; return }

                let chosen = newPin
                run {
                    try await vault.rememberOnThisPhone(pin: chosen)
                    newPin = ""
                    newPinAgain = ""
                }
            }
            .buttonStyle(.borderedProminent)
            .frame(maxWidth: .infinity)

            Button("Not now") { vault.offerToRemember = false }
                .font(.footnote)
                .frame(maxWidth: .infinity)
        }
    }

    // ---- plumbing ----

    /// Stays on the main actor. The slow parts push themselves off it inside PhoneVault,
    /// so nothing here ever publishes state from a background thread.
    private func run(_ work: @escaping @MainActor () async throws -> Void) {
        busy = true
        message = ""

        Task {
            do {
                try await work()
            } catch {
                message = error.localizedDescription
            }
            busy = false
        }
    }
}
