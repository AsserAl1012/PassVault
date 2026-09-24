import Foundation

/// What the vault file says about itself. Readable without unlocking anything.
public struct VaultHeader: Codable, Equatable, Sendable {
    public let formatVersion: Int
    public let vaultId: String
    public let epoch: Int
    public let revision: Int64
    public let kdf: KdfSettings
    public let slots: [KeySlot]

    public init(
        formatVersion: Int, vaultId: String, epoch: Int, revision: Int64,
        kdf: KdfSettings, slots: [KeySlot]
    ) {
        self.formatVersion = formatVersion
        self.vaultId = vaultId
        self.epoch = epoch
        self.revision = revision
        self.kdf = kdf
        self.slots = slots
    }

    public func slot(_ name: String) throws -> KeySlot {
        let found = slots.filter { $0.name == name }
        guard found.count == 1, let slot = found.first else { throw VaultError.noSlot(name) }
        return slot
    }

    public func hasSlot(_ name: String) -> Bool { slots.contains { $0.name == name } }
}

/// Reading vault.pv. The layout is frozen in DESIGN.md:
/// "PVLT" || formatVersion(2) || headerLength(4) || header || nonce(12) || ciphertext || tag(16)
///
/// Version 1 of the phone app only reads. Writing lives on the Windows side.
public enum VaultFile {
    public static let format = 1
    private static let prefixSize = 10
    private static let magic: [UInt8] = Array("PVLT".utf8)

    public static func readHeader(_ file: Data) throws -> VaultHeader {
        try unpack(file).header
    }

    /// Open the vault with the 12 words. Returns the master key.
    public static func unlockWithPhrase(_ file: Data, phrase: String) throws -> Data {
        let header = try readHeader(file)
        let slot = try header.slot(KeyWrap.recoverySlot)

        return try KeyWrap.unwrapWithPhrase(
            slot: slot, phrase: phrase, vaultId: header.vaultId, kdf: header.kdf
        )
    }

    public static func readEntries(_ file: Data, masterKey: Data) throws -> [Entry] {
        let parts = try unpack(file)
        let json = try Aead.open(key: masterKey, blob: parts.body, aad: parts.headerBytes)

        do {
            return try JSONDecoder().decode([Entry].self, from: json)
        } catch {
            throw VaultError.corrupt("the credentials inside could not be read")
        }
    }

    /// True if this vault is older than one this device has already seen - someone putting
    /// an old copy back to undo a password change.
    public static func isRollback(_ header: VaultHeader, highestEpochSeen: Int, highestRevisionSeen: Int64) -> Bool {
        header.epoch < highestEpochSeen
            || (header.epoch == highestEpochSeen && header.revision < highestRevisionSeen)
    }

    // ---- the bytes ----

    private static func unpack(_ input: Data) throws -> (header: VaultHeader, headerBytes: Data, body: Data) {
        // Normalise the indices - a Data that came from a slice does not start at 0.
        let file = Data(input)

        guard file.count >= prefixSize, Array(file.prefix(4)) == magic else { throw VaultError.notAVault }

        let version = Int(uint16LE(file, 4))
        guard version == format else { throw VaultError.newerFormat(version) }

        let headerLength = Int(uint32LE(file, 6))
        guard headerLength >= 0, prefixSize + headerLength <= file.count else {
            throw VaultError.corrupt("the header length does not fit")
        }

        // The header is authenticated as the exact bytes on disk, never re-encoded -
        // Swift and .NET do not write identical JSON.
        let headerBytes = file.subdata(in: prefixSize..<(prefixSize + headerLength))
        let body = file.subdata(in: (prefixSize + headerLength)..<file.count)

        do {
            let header = try JSONDecoder().decode(VaultHeader.self, from: headerBytes)
            return (header, headerBytes, body)
        } catch {
            throw VaultError.corrupt("the header could not be read")
        }
    }

    private static func uint16LE(_ d: Data, _ at: Int) -> UInt16 {
        UInt16(d[at]) | (UInt16(d[at + 1]) << 8)
    }

    private static func uint32LE(_ d: Data, _ at: Int) -> UInt32 {
        UInt32(d[at])
            | (UInt32(d[at + 1]) << 8)
            | (UInt32(d[at + 2]) << 16)
            | (UInt32(d[at + 3]) << 24)
    }
}
