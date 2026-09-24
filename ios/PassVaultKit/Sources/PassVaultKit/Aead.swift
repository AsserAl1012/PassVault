import CryptoKit
import Foundation

/// AES-256-GCM. Every sealed blob is nonce(12) || ciphertext || tag(16).
///
/// That is exactly what CryptoKit calls `combined`, which is why the format was chosen -
/// this side needs no byte shuffling at all.
public enum Aead {
    public static let keySize = 32
    public static let nonceSize = 12
    public static let tagSize = 16

    /// Encrypt. A fresh random nonce every time, never a counter.
    public static func seal(key: Data, plaintext: Data, aad: Data) throws -> Data {
        guard key.count == keySize else { throw VaultError.badKeySize(key.count) }

        let box = try AES.GCM.seal(
            plaintext,
            using: SymmetricKey(data: key),
            nonce: AES.GCM.Nonce(),
            authenticating: aad
        )

        guard let combined = box.combined else { throw VaultError.corrupt("could not seal") }
        return combined
    }

    /// Decrypt. Throws if the key, the blob or the aad is wrong.
    public static func open(key: Data, blob: Data, aad: Data) throws -> Data {
        guard key.count == keySize else { throw VaultError.badKeySize(key.count) }
        guard blob.count >= nonceSize + tagSize else { throw VaultError.corrupt("sealed blob is too short") }

        do {
            let box = try AES.GCM.SealedBox(combined: blob)
            return try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: aad)
        } catch {
            throw VaultError.wrongKey
        }
    }
}
