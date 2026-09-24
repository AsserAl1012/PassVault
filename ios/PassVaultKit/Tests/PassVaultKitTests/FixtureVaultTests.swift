import XCTest
@testable import PassVaultKit

/// Opens tests/fixture-vault.pv - a real vault file written by the Windows app. If the two
/// sides ever disagree about a single byte of the format, this is what catches it.
///
/// The data in it is made up and its phrase is public on purpose.
final class FixtureVaultTests: XCTestCase {

    private func fixture() throws -> (vault: Data, info: [String: Any]) {
        let info = try XCTUnwrap(
            try JSONSerialization.jsonObject(with: Data(contentsOf: SharedFiles.fixtureInfo)) as? [String: Any]
        )
        return (try Data(contentsOf: SharedFiles.fixtureVault), info)
    }

    func testTheHeaderReadsTheSameAsTheWindowsSideWroteIt() throws {
        let (vault, info) = try fixture()
        let header = try VaultFile.readHeader(vault)

        XCTAssertEqual(header.formatVersion, VaultFile.format)
        XCTAssertEqual(header.vaultId, info["vaultId"] as? String)
        XCTAssertEqual(header.epoch, info["epoch"] as? Int)
        XCTAssertEqual(header.revision, Int64(try XCTUnwrap(info["revision"] as? Int)))
        XCTAssertEqual(header.kdf, .standard)
        XCTAssertTrue(header.hasSlot(KeyWrap.recoverySlot))
    }

    func testThePhraseOpensAVaultWrittenOnWindows() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)

        let masterKey = try VaultFile.unlockWithPhrase(vault, phrase: phrase)

        XCTAssertEqual(masterKey.count, 32)
    }

    func testEveryCredentialComesBackExactly() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)
        let expected = try XCTUnwrap(info["entries"] as? [[String: Any]])

        let masterKey = try VaultFile.unlockWithPhrase(vault, phrase: phrase)
        let entries = try VaultFile.readEntries(vault, masterKey: masterKey)

        XCTAssertEqual(entries.count, expected.count)

        for (entry, want) in zip(entries, expected) {
            XCTAssertEqual(entry.id, want["id"] as? String)
            XCTAssertEqual(entry.name, want["name"] as? String)
            XCTAssertEqual(entry.username, want["username"] as? String)
            XCTAssertEqual(entry.email, want["email"] as? String)
            XCTAssertEqual(entry.password, want["password"] as? String)
            XCTAssertEqual(entry.notes, want["notes"] as? String)
            XCTAssertEqual(entry.updatedAt, (want["updatedAt"] as? NSNumber)?.int64Value)
            XCTAssertEqual(entry.deleted, want["deleted"] as? Bool)
        }
    }

    func testANewlineInsideAFieldSurvives() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)

        let entries = try VaultFile.readEntries(vault, masterKey: try VaultFile.unlockWithPhrase(vault, phrase: phrase))
        let withNotes = try XCTUnwrap(entries.first { $0.name == "bank.example" })

        XCTAssertEqual(withNotes.notes, "second line\nwith a newline")
    }

    func testDeletedEntriesAreKeptAsTombstones() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)

        let entries = try VaultFile.readEntries(vault, masterKey: try VaultFile.unlockWithPhrase(vault, phrase: phrase))

        XCTAssertEqual(entries.filter { $0.deleted }.count, 1)
        XCTAssertEqual(entries.filter { !$0.deleted }.count, 2)
    }

    func testMessyTypingStillOpensIt() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)
        let messy = "  " + phrase.uppercased().replacingOccurrences(of: " ", with: "   ") + "\t"

        XCTAssertNoThrow(try VaultFile.unlockWithPhrase(vault, phrase: messy))
    }

    func testTheWrongPhraseIsRejected() throws {
        let (vault, _) = try fixture()

        // A valid 12-word phrase, just not this vault's.
        let other = "zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo zoo wrong"
        XCTAssertNoThrow(try Mnemonic.parse(other))
        XCTAssertThrowsError(try VaultFile.unlockWithPhrase(vault, phrase: other))
    }

    func testAnEditedHeaderBreaksTheFile() throws {
        let (vault, info) = try fixture()
        let phrase = try XCTUnwrap(info["phrase"] as? String)
        let masterKey = try VaultFile.unlockWithPhrase(vault, phrase: phrase)

        // The header is authenticated, so changing the epoch - or just as easily the Argon2
        // cost - must make the vault refuse to open rather than quietly accept it.
        var tampered = vault
        let needle = Array("\"epoch\":1".utf8)
        let at = try XCTUnwrap(indexOf(needle, in: [UInt8](tampered)))
        tampered[at + needle.count - 1] = UInt8(ascii: "9")

        XCTAssertThrowsError(try VaultFile.readEntries(tampered, masterKey: masterKey))
    }

    func testRandomBytesAreNotMistakenForAVault() throws {
        XCTAssertThrowsError(try VaultFile.readHeader(try Random.bytes(200))) { error in
            XCTAssertEqual(error as? VaultError, .notAVault)
        }
    }

    func testRollbackIsSpotted() {
        let header = VaultHeader(
            formatVersion: 1, vaultId: "x", epoch: 1, revision: 4,
            kdf: .standard, slots: []
        )

        XCTAssertTrue(VaultFile.isRollback(header, highestEpochSeen: 1, highestRevisionSeen: 5))
        XCTAssertFalse(VaultFile.isRollback(header, highestEpochSeen: 1, highestRevisionSeen: 4))
        XCTAssertFalse(VaultFile.isRollback(header, highestEpochSeen: 1, highestRevisionSeen: 3))
        XCTAssertTrue(VaultFile.isRollback(header, highestEpochSeen: 2, highestRevisionSeen: 1))
    }

    private func indexOf(_ needle: [UInt8], in haystack: [UInt8]) -> Int? {
        guard needle.count <= haystack.count else { return nil }
        for start in 0...(haystack.count - needle.count) where Array(haystack[start..<(start + needle.count)]) == needle {
            return start
        }
        return nil
    }
}
