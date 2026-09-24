import Foundation

/// One locked-away copy of the master key. The name is authenticated, so each way in opens
/// exactly the slot it expects - we never try slots until one works.
public struct KeySlot: Codable, Equatable, Sendable {
    public let name: String
    public let salt: String   // base64, 16 bytes
    public let blob: String   // base64, nonce(12) || ciphertext || tag(16)

    public init(name: String, salt: String, blob: String) {
        self.name = name
        self.salt = salt
        self.blob = blob
    }
}

public enum KeyWrap {
    public static let recoverySlot = "recovery"

    /// The phone's own PIN slot. It lives only on this device and never goes into the vault
    /// file, so it does not have to match the Windows PIN slot - and it cannot, because
    /// there is no USB secret on a phone.
    public static let pinSlot = "ios-pin"

    public static let pinLength = 4
    public static let secretSize = 32

    // ---- the 12 words ----

    public static func unwrapWithPhrase(
        slot: KeySlot, phrase: String, vaultId: String, kdf: KdfSettings
    ) throws -> Data {
        guard slot.name == recoverySlot else { throw VaultError.noSlot(recoverySlot) }

        let (salt, blob) = try decode(slot)
        let key = try Kdf.derive(password: Mnemonic.normalise(phrase), salt: salt, settings: kdf)

        return try Aead.open(key: key, blob: blob, aad: Data("pv1:slot:recovery:\(vaultId)".utf8))
    }

    // ---- this phone plus a PIN ----

    public static func wrapWithPin(
        masterKey: Data, deviceSecret: Data, pin: String,
        vaultId: String, deviceId: String, kdf: KdfSettings
    ) throws -> KeySlot {
        let salt = try Random.bytes(KdfSettings.saltSize)
        let key = try Kdf.derive(secret: pinInput(deviceSecret: deviceSecret, pin: pin), salt: salt, settings: kdf)
        let blob = try Aead.seal(key: key, plaintext: masterKey, aad: pinAad(vaultId: vaultId, deviceId: deviceId))

        return KeySlot(name: pinSlot, salt: salt.base64EncodedString(), blob: blob.base64EncodedString())
    }

    public static func unwrapWithPin(
        slot: KeySlot, deviceSecret: Data, pin: String,
        vaultId: String, deviceId: String, kdf: KdfSettings
    ) throws -> Data {
        guard slot.name == pinSlot else { throw VaultError.noSlot(pinSlot) }

        let (salt, blob) = try decode(slot)
        let key = try Kdf.derive(secret: pinInput(deviceSecret: deviceSecret, pin: pin), salt: salt, settings: kdf)

        return try Aead.open(key: key, blob: blob, aad: pinAad(vaultId: vaultId, deviceId: deviceId))
    }

    /// deviceSecret(32) || PIN as 4 ASCII digits. Plain concatenation of fixed-length parts,
    /// the same shape as the Windows side minus the USB secret it does not have.
    public static func pinInput(deviceSecret: Data, pin: String) throws -> Data {
        guard deviceSecret.count == secretSize else {
            throw VaultError.corrupt("the device secret must be \(secretSize) bytes")
        }
        guard pin.count == pinLength, pin.allSatisfy({ $0.isASCII && $0.isNumber }) else {
            throw VaultError.badPhrase("The PIN must be \(pinLength) digits.")
        }

        return deviceSecret + Data(pin.utf8)
    }

    private static func pinAad(vaultId: String, deviceId: String) -> Data {
        Data("pv1:slot:ios-pin:\(vaultId):\(deviceId)".utf8)
    }

    private static func decode(_ slot: KeySlot) throws -> (salt: Data, blob: Data) {
        guard let salt = Data(base64Encoded: slot.salt), let blob = Data(base64Encoded: slot.blob) else {
            throw VaultError.corrupt("a key slot is not valid base64")
        }
        return (salt, blob)
    }
}
