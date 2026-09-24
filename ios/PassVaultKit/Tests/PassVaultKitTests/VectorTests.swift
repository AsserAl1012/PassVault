import XCTest
@testable import PassVaultKit

/// These read the same tests/vectors.json the C# tests read. They are what catches the two
/// apps drifting apart, which you would otherwise only find out during a real recovery.
final class VectorTests: XCTestCase {

    private func vectors() throws -> [String: Any] {
        let json = try JSONSerialization.jsonObject(with: Data(contentsOf: SharedFiles.vectors))
        return try XCTUnwrap(json as? [String: Any])
    }

    private func hex(_ string: String) -> Data {
        var bytes = [UInt8]()
        var index = string.startIndex
        while index < string.endIndex {
            let next = string.index(index, offsetBy: 2)
            bytes.append(UInt8(string[index..<next], radix: 16)!)
            index = next
        }
        return Data(bytes)
    }

    private func hex(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    func testKdfSettingsStillMatchTheFrozenOnes() throws {
        let kdf = try XCTUnwrap(vectors()["kdf"] as? [String: Any])
        let standard = KdfSettings.standard

        XCTAssertEqual(kdf["algo"] as? String, standard.algo)
        XCTAssertEqual(kdf["iterations"] as? Int, standard.iterations)
        XCTAssertEqual(kdf["memoryKiB"] as? Int, standard.memoryKiB)
        XCTAssertEqual(kdf["parallelism"] as? Int, standard.parallelism)
        XCTAssertEqual(kdf["outputBytes"] as? Int, standard.outputBytes)

        // The number this side must hand libsodium. Mixing KiB and bytes up still "works"
        // on both platforms while being 1024 times weaker, so it is pinned here.
        XCTAssertEqual(kdf["memoryBytesForLibsodium"] as? Int, standard.memoryBytes)
        XCTAssertEqual(standard.memoryBytes, 67_108_864)
    }

    func testOfficialBip39VectorsStillPass() throws {
        let cases = try XCTUnwrap(vectors()["mnemonic"] as? [[String: Any]])
        XCTAssertFalse(cases.isEmpty)

        for one in cases {
            let entropy = hex(try XCTUnwrap(one["entropyHex"] as? String))
            let phrase = try XCTUnwrap(one["phrase"] as? String)

            XCTAssertEqual(try Mnemonic.fromEntropy(entropy), phrase)
            XCTAssertEqual(try Mnemonic.parse(phrase), entropy)
        }
    }

    func testPhraseIsCleanedUpTheSameWayOnBothPlatforms() throws {
        let cases = try XCTUnwrap(vectors()["normalise"] as? [[String: Any]])

        for one in cases {
            let input = try XCTUnwrap(one["input"] as? String)
            let expected = try XCTUnwrap(one["output"] as? String)
            XCTAssertEqual(Mnemonic.normalise(input), expected, "input was \(input.debugDescription)")
        }
    }

    func testArgon2idGivesTheSameKeyAsTheFrozenVector() throws {
        let vector = try XCTUnwrap(vectors()["argon2id"] as? [String: Any])

        let key = try Kdf.derive(
            secret: hex(try XCTUnwrap(vector["secretHex"] as? String)),
            salt: hex(try XCTUnwrap(vector["saltHex"] as? String)),
            settings: .standard
        )

        XCTAssertEqual(hex(key), vector["expectedKeyHex"] as? String)
    }

    func testABlobSealedByTheFrozenVectorStillOpens() throws {
        let vector = try XCTUnwrap(vectors()["aead"] as? [String: Any])

        let plaintext = try Aead.open(
            key: hex(try XCTUnwrap(vector["keyHex"] as? String)),
            blob: hex(try XCTUnwrap(vector["blobHex"] as? String)),
            aad: Data((try XCTUnwrap(vector["aadUtf8"] as? String)).utf8)
        )

        XCTAssertEqual(String(decoding: plaintext, as: UTF8.self), vector["plaintextUtf8"] as? String)
    }

    func testTheWindowsPinKeyDerivesTheSameHere() throws {
        // The phone has its own PIN slot shape, but the derivation underneath must agree,
        // so this checks Argon2id over the exact bytes Windows uses.
        let vector = try XCTUnwrap(vectors()["pinSlot"] as? [String: Any])

        let key = try Kdf.derive(
            secret: hex(try XCTUnwrap(vector["pinInputHex"] as? String)),
            salt: hex(try XCTUnwrap(vector["saltHex"] as? String)),
            settings: .standard
        )

        XCTAssertEqual(hex(key), vector["expectedKeyHex"] as? String)
    }

    func testSealedBlobsAreNonceCiphertextTag() throws {
        let key = try Random.bytes(32)
        let plaintext = Data("hunter2 is a terrible password".utf8)
        let aad = Data("pv1:test".utf8)

        let blob = try Aead.seal(key: key, plaintext: plaintext, aad: aad)

        XCTAssertEqual(blob.count, Aead.nonceSize + plaintext.count + Aead.tagSize)
        XCTAssertEqual(try Aead.open(key: key, blob: blob, aad: aad), plaintext)
    }

    func testTheSameInputTwiceGivesDifferentBytes() throws {
        let key = try Random.bytes(32)
        let plaintext = Data("same every time".utf8)
        let aad = Data("pv1:test".utf8)

        // If this ever fails the nonce is not random, and two saved vaults leak each other.
        XCTAssertNotEqual(
            try Aead.seal(key: key, plaintext: plaintext, aad: aad),
            try Aead.seal(key: key, plaintext: plaintext, aad: aad)
        )
    }

    func testAWrongKeyOrAadIsRejected() throws {
        let key = try Random.bytes(32)
        let aad = Data("pv1:test".utf8)
        let blob = try Aead.seal(key: key, plaintext: Data("secret".utf8), aad: aad)

        XCTAssertThrowsError(try Aead.open(key: try Random.bytes(32), blob: blob, aad: aad))
        XCTAssertThrowsError(try Aead.open(key: key, blob: blob, aad: Data("pv1:other".utf8)))
    }
}
