import Foundation
import LocalAuthentication
import PassVaultKit
import Security

enum PhoneError: LocalizedError {
    case keychain(String)
    case faceIdUnavailable(String)
    case notSetUp
    case pinOff(Int)
    case wrongPin(Int)

    var errorDescription: String? {
        switch self {
        case .keychain(let why): return why
        case .faceIdUnavailable(let why): return why
        case .notSetUp: return "This phone is not set up yet. Import your vault file first."
        case .pinOff(let tries): return "The PIN is switched off after \(tries) wrong tries. Use your 12 words."
        case .wrongPin(let left):
            return left > 0
                ? "Wrong PIN. \(left) \(left == 1 ? "try" : "tries") left before the PIN is switched off."
                : "Wrong PIN. The PIN is now switched off - use your 12 words."
        }
    }
}

/// Where the phone keeps its two secrets.
///
/// The master key sits behind Face ID. The device secret is a separate item with no
/// biometrics on it, used only as one half of the PIN key - so a PIN alone, without this
/// phone, is worth nothing.
enum Keychain {
    private static let service = "com.passvault.app"
    private static let masterKeyAccount = "master-key"
    private static let deviceSecretAccount = "device-secret"

    // ---- the master key, behind Face ID ----

    /// `.biometryCurrentSet` means the item dies if a new face or fingerprint is enrolled.
    /// Someone adding their own face to your unlocked phone does not get your vault - they
    /// would still need the PIN or the 12 words.
    ///
    /// `WhenPasscodeSetThisDeviceOnly` means it never leaves this phone and never reaches a
    /// backup or iCloud.
    static func saveMasterKey(_ key: Data) throws {
        forgetMasterKey()

        var cfError: Unmanaged<CFError>?
        guard let access = SecAccessControlCreateWithFlags(
            kCFAllocatorDefault,
            kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly,
            .biometryCurrentSet,
            &cfError
        ) else {
            throw PhoneError.keychain("Could not set up Face ID protection on this phone.")
        }

        var query = base(account: masterKeyAccount)
        query[kSecValueData as String] = key
        query[kSecAttrAccessControl as String] = access

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw PhoneError.keychain("Could not save to the keychain (code \(status)).")
        }
    }

    static func masterKeyWithFaceId(reason: String) throws -> Data {
        let context = LAContext()
        context.localizedReason = reason

        var query = base(account: masterKeyAccount)
        query[kSecReturnData as String] = true
        query[kSecUseAuthenticationContext as String] = context

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)

        switch status {
        case errSecSuccess:
            guard let data = item as? Data else {
                throw PhoneError.keychain("The keychain gave back something unexpected.")
            }
            return data
        case errSecUserCanceled:
            throw PhoneError.faceIdUnavailable("Face ID was cancelled.")
        case errSecItemNotFound:
            // We only got here because the item existed. Its key was destroyed, which means
            // a face or fingerprint was added or reset. Clear the dead row so we stop
            // offering a button that can never work again.
            forgetMasterKey()
            throw PhoneError.faceIdUnavailable(
                "Face ID protection was reset when this phone's face or fingerprint set changed. "
                + "Use your PIN or your 12 words, then turn Face ID back on.")
        default:
            throw PhoneError.faceIdUnavailable("Face ID did not work (code \(status)). Use your PIN.")
        }
    }

    static var hasMasterKey: Bool {
        var query = base(account: masterKeyAccount)
        query[kSecReturnData as String] = false
        // Do not prompt for Face ID just to find out whether the item is there.
        query[kSecUseAuthenticationUI as String] = kSecUseAuthenticationUIFail

        let status = SecItemCopyMatching(query as CFDictionary, nil)
        return status == errSecSuccess || status == errSecInteractionNotAllowed
    }

    static func forgetMasterKey() {
        SecItemDelete(base(account: masterKeyAccount) as CFDictionary)
    }

    // ---- this phone's own 32 bytes, no biometrics ----

    /// Read only, never creates. A restored backup brings phone.json - and so the PIN slot -
    /// but not this item, and minting a fresh one there would tell someone their correct PIN
    /// was wrong until the slot burned itself out.
    static func existingDeviceSecret() -> Data? { load(account: deviceSecretAccount) }

    /// Creates one if there is none. Only setting up a PIN should ever call this.
    static func deviceSecret() throws -> Data {
        if let existing = load(account: deviceSecretAccount) { return existing }

        let secret = try Random.bytes(32)

        var query = base(account: deviceSecretAccount)
        query[kSecValueData as String] = secret
        query[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw PhoneError.keychain("Could not save this phone's secret (code \(status)).")
        }
        return secret
    }

    static func forgetDeviceSecret() {
        SecItemDelete(base(account: deviceSecretAccount) as CFDictionary)
    }

    // ---- plumbing ----

    private static func load(account: String) -> Data? {
        var query = base(account: account)
        query[kSecReturnData as String] = true

        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    private static func base(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrSynchronizable as String: false,
        ]
    }
}
