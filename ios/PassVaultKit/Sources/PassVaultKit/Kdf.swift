import Clibsodium
import Foundation

/// Argon2id settings, read from the vault header rather than hard-coded.
public struct KdfSettings: Codable, Equatable, Sendable {
    public let algo: String
    public let iterations: Int
    public let memoryKiB: Int
    public let parallelism: Int
    public let outputBytes: Int

    public static let saltSize = 16

    /// 64 MiB, 3 passes, single threaded. Must match KdfSettings.Default on the C# side.
    public static let standard = KdfSettings(
        algo: "argon2id", iterations: 3, memoryKiB: 65536, parallelism: 1, outputBytes: 32
    )

    public init(algo: String, iterations: Int, memoryKiB: Int, parallelism: Int, outputBytes: Int) {
        self.algo = algo
        self.iterations = iterations
        self.memoryKiB = memoryKiB
        self.parallelism = parallelism
        self.outputBytes = outputBytes
    }

    /// libsodium wants memory in BYTES. The header stores KiB, like the Windows library does.
    /// Getting this wrong still "works" on both sides while being 1024x weaker, so it lives
    /// in one place and nowhere else.
    var memoryBytes: Int { memoryKiB * 1024 }
}

public enum Kdf {
    private static let ready: Bool = {
        sodium_init() >= 0
    }()

    public static func derive(secret: Data, salt: Data, settings: KdfSettings) throws -> Data {
        guard ready else { throw VaultError.corrupt("libsodium would not start") }
        guard settings.algo == "argon2id" else { throw VaultError.unsupported("key derivation: \(settings.algo)") }
        guard salt.count == KdfSettings.saltSize else { throw VaultError.corrupt("salt must be 16 bytes") }

        // libsodium's argon2id is single lane. The format pins parallelism to 1 for exactly
        // this reason - anything else and this side simply cannot reproduce the key.
        guard settings.parallelism == 1 else { throw VaultError.unsupported("parallelism must be 1") }

        var out = [UInt8](repeating: 0, count: settings.outputBytes)
        let secretBytes = [UInt8](secret)
        let saltBytes = [UInt8](salt)

        // Every pointer stays inside its own closure. Returning one out of
        // withMemoryRebound would be a dangling pointer, so the calls nest instead.
        let result = out.withUnsafeMutableBufferPointer { outBuffer -> Int32 in
            secretBytes.withUnsafeBufferPointer { secretBuffer in
                secretBuffer.baseAddress!.withMemoryRebound(
                    to: CChar.self, capacity: secretBytes.count
                ) { secretPointer in
                    saltBytes.withUnsafeBufferPointer { saltBuffer in
                        crypto_pwhash(
                            outBuffer.baseAddress!,
                            UInt64(settings.outputBytes),
                            secretPointer,
                            UInt64(secretBytes.count),
                            saltBuffer.baseAddress!,
                            UInt64(settings.iterations),
                            settings.memoryBytes,
                            crypto_pwhash_ALG_ARGON2ID13
                        )
                    }
                }
            }
        }

        guard result == 0 else { throw VaultError.corrupt("Argon2id failed - probably out of memory") }
        return Data(out)
    }

    public static func derive(password: String, salt: Data, settings: KdfSettings) throws -> Data {
        try derive(secret: Data(password.utf8), salt: salt, settings: settings)
    }
}
