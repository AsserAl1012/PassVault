import Foundation
import PassVaultKit
import SwiftUI

/// What this phone remembers between launches. Never leaves the device.
struct PhoneState: Codable {
    var deviceId: String
    var vaultId: String
    var pinSlot: KeySlot?
    var kdf: KdfSettings
    var highestEpochSeen: Int
    var highestRevisionSeen: Int64
    var failedAttempts: Int
    var pinDisabled: Bool
}

/// The phone's copy of the vault and the ways into it.
///
/// Version 1 only reads. Changes are made on the Windows app and brought over by importing
/// the vault file again, which skips the whole question of merging two edits.
@MainActor
final class PhoneVault: ObservableObject {
    static let maxAttempts = 10

    @Published private(set) var entries: [Entry] = []
    @Published private(set) var isOpen = false

    /// Set after the 12 words let someone in on a phone that has no Face ID key yet. It
    /// lives here rather than in the unlock screen's own state, because that screen is
    /// thrown away the instant the vault opens.
    @Published var offerToRemember = false

    @Published var search = ""

    private var state: PhoneState?
    private var masterKey: Data?

    /// Bumped by lock(). An unlock that was already running checks it before publishing, so
    /// locking during the second Argon2 takes cannot be undone by the work finishing late.
    private var generation = 0

    private let folder: URL
    private let vaultPath: URL
    private let statePath: URL

    init() {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        folder = support.appendingPathComponent("PassVault", isDirectory: true)
        vaultPath = folder.appendingPathComponent("vault.pv")
        statePath = folder.appendingPathComponent("phone.json")

        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        // Keep all of it out of iCloud and iTunes backups. The Windows side is the backup.
        var excluded = folder
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? excluded.setResourceValues(&values)

        state = loadState()
    }

    // ---- what the unlock screen needs to know ----

    var hasVault: Bool { FileManager.default.fileExists(atPath: vaultPath.path) }
    var isSetUp: Bool { hasVault && state != nil }
    var canUseFaceId: Bool { isSetUp && Keychain.hasMasterKey }
    var canUsePin: Bool { isSetUp && state?.pinSlot != nil && state?.pinDisabled != true }
    var triesLeft: Int { Self.maxAttempts - (state?.failedAttempts ?? 0) }

    var shown: [Entry] {
        entries
            .filter { !$0.deleted && $0.matches(search) }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    // ---- bringing a vault over from the Windows app ----

    /// Copy in a vault file the user picked. Refuses one older than we have already seen,
    /// which is what stops an old copy quietly undoing a password change.
    func importVault(from url: URL) throws {
        let reachable = url.startAccessingSecurityScopedResource()
        defer { if reachable { url.stopAccessingSecurityScopedResource() } }

        let incoming = try Data(contentsOf: url)
        let header = try VaultFile.readHeader(incoming)

        if let state, header.vaultId == state.vaultId,
           VaultFile.isRollback(header, highestEpochSeen: state.highestEpochSeen,
                                highestRevisionSeen: state.highestRevisionSeen) {
            throw VaultError.olderThanWeHaveSeen
        }

        // Atomic, or a kill mid-write leaves a half file that nothing can open and no
        // screen can replace.
        try incoming.write(to: vaultPath, options: [.atomic, .completeFileProtection])

        // A different vault, or a re-keyed one, means the old way in no longer fits.
        //
        // Nothing here has been authenticated yet - the header is plain text, and only opening
        // the body proves it. So how new the vault is gets recorded by finishOpening, after the
        // body opens, never from this header: a backup whose header had been edited used to set
        // the numbers sky-high, fail to open, and then have every genuine copy refused as older.
        if state == nil || state?.vaultId != header.vaultId || header.epoch != state?.highestEpochSeen {
            Keychain.forgetMasterKey()
            state = PhoneState(
                deviceId: UUID().uuidString,
                vaultId: header.vaultId,
                pinSlot: nil,
                kdf: header.kdf,
                highestEpochSeen: 0,
                highestRevisionSeen: 0,
                failedAttempts: 0,
                pinDisabled: false
            )
        }
        saveState()
    }

    // ---- the three ways in ----
    //
    // Each stays on the main actor and pushes only its slow call off, so published state is
    // never written from a background thread. Argon2id takes about a second on a phone, and
    // the Face ID read blocks until the person answers the prompt.

    func openWithPhrase(_ phrase: String) async throws {
        try Mnemonic.parse(phrase)   // gives a per-word message on a typo

        let g = generation
        let vault = try Data(contentsOf: vaultPath)

        let key = try await Task.detached(priority: .userInitiated) {
            try VaultFile.unlockWithPhrase(vault, phrase: phrase)
        }.value

        // The 12 words always work, and they switch the PIN back on.
        state?.failedAttempts = 0
        state?.pinDisabled = false
        saveState()

        try finishOpening(vault: vault, key: key, generation: g)
        if isOpen { offerToRemember = !Keychain.hasMasterKey }
    }

    func openWithFaceId() async throws {
        let g = generation

        let key = try await Task.detached(priority: .userInitiated) {
            try Keychain.masterKeyWithFaceId(reason: "Open your PassVault")
        }.value

        try finishOpening(vault: try Data(contentsOf: vaultPath), key: key, generation: g)
    }

    func openWithPin(_ pin: String) async throws {
        guard var current = state, let slot = current.pinSlot else { throw PhoneError.notSetUp }
        guard !current.pinDisabled else { throw PhoneError.pinOff(Self.maxAttempts) }

        // Read the secret, never create one. A restored backup brings phone.json but not
        // the keychain, and minting a fresh secret here would turn a correct PIN into
        // "wrong PIN" ten times over and then kill the slot for good.
        guard let secret = Keychain.existingDeviceSecret() else {
            throw PhoneError.keychain(
                "This phone's own secret is gone - a restored backup does not bring it across. "
                + "Use your 12 words, then turn the PIN back on.")
        }

        let g = generation

        // Count the try before checking it, so force-quitting cannot rewind the counter.
        current.failedAttempts += 1
        if current.failedAttempts >= Self.maxAttempts { current.pinDisabled = true }
        state = current
        saveState()

        let vaultId = current.vaultId
        let deviceId = current.deviceId
        let kdf = current.kdf

        let key: Data
        do {
            key = try await Task.detached(priority: .userInitiated) {
                try KeyWrap.unwrapWithPin(
                    slot: slot, deviceSecret: secret, pin: pin,
                    vaultId: vaultId, deviceId: deviceId, kdf: kdf
                )
            }.value
        } catch VaultError.wrongKey {
            // Only a genuine mismatch counts as a wrong PIN. A locked keychain or Argon2
            // running out of memory must not be reported as one.
            throw PhoneError.wrongPin(Self.maxAttempts - current.failedAttempts)
        }

        state?.failedAttempts = 0
        state?.pinDisabled = false
        saveState()

        try finishOpening(vault: try Data(contentsOf: vaultPath), key: key, generation: g)
    }

    /// Turn on Face ID and a PIN, once the vault is already open.
    func rememberOnThisPhone(pin: String) async throws {
        guard let key = masterKey, var current = state else { throw PhoneError.notSetUp }

        // The one and only place a device secret is created.
        let secret = try Keychain.deviceSecret()
        let vaultId = current.vaultId
        let deviceId = current.deviceId
        let kdf = current.kdf

        let slot = try await Task.detached(priority: .userInitiated) {
            try KeyWrap.wrapWithPin(
                masterKey: key, deviceSecret: secret, pin: pin,
                vaultId: vaultId, deviceId: deviceId, kdf: kdf
            )
        }.value

        try Keychain.saveMasterKey(key)

        current.pinSlot = slot
        current.failedAttempts = 0
        current.pinDisabled = false
        state = current
        saveState()
        offerToRemember = false
    }

    func lock() {
        generation += 1

        // Zero in place. `var copy = masterKey` then resetBytes would wipe a fresh copy and
        // leave the real bytes untouched, because Data is copy-on-write.
        if let count = masterKey?.count { masterKey?.resetBytes(in: 0..<count) }
        masterKey = nil

        entries = []
        search = ""
        isOpen = false
        offerToRemember = false
    }

    /// Wipe everything this app holds. The Windows side and your 12 words are untouched.
    func forgetEverything() {
        lock()
        Keychain.forgetMasterKey()
        Keychain.forgetDeviceSecret()
        try? FileManager.default.removeItem(at: vaultPath)
        try? FileManager.default.removeItem(at: statePath)
        state = nil
    }

    // ---- plumbing ----

    private func finishOpening(vault: Data, key: Data, generation g: Int) throws {
        // Locked while this was working - throw the result away rather than quietly
        // unlocking an app that is sitting in the background.
        guard g == generation else { return }

        let loaded = try VaultFile.readEntries(vault, masterKey: key)
        let header = try VaultFile.readHeader(vault)

        masterKey = key
        entries = loaded
        isOpen = true

        if var current = state {
            let newer = header.epoch > current.highestEpochSeen
                || (header.epoch == current.highestEpochSeen && header.revision > current.highestRevisionSeen)
            if newer {
                current.highestEpochSeen = header.epoch
                current.highestRevisionSeen = header.revision
                state = current
                saveState()
            }
        }
    }

    private func loadState() -> PhoneState? {
        guard let data = try? Data(contentsOf: statePath) else { return nil }
        return try? JSONDecoder().decode(PhoneState.self, from: data)
    }

    private func saveState() {
        guard let state, let data = try? JSONEncoder().encode(state) else { return }
        // phone.json is rewritten on every PIN attempt, so a torn write here is not rare.
        try? data.write(to: statePath, options: [.atomic, .completeFileProtection])
    }
}
