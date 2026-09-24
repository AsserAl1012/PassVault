import CryptoKit
import Foundation

/// The 12-word recovery phrase. Standard BIP-39, English word list.
///
/// The phone only ever reads a phrase - the Windows app is what creates one - so there is
/// no generator here.
public enum Mnemonic {
    public static let defaultWordCount = 12
    private static let allowedWordCounts = [12, 15, 18, 21, 24]

    public static let wordList: [String] = loadWordList()

    private static let wordIndex: [String: Int] = {
        var map = [String: Int](minimumCapacity: wordList.count)
        for (i, word) in wordList.enumerated() { map[word] = i }
        return map
    }()

    private static func loadWordList() -> [String] {
        // Whether SwiftPM flattens the resource to the bundle root or keeps it under
        // Resources/ depends on the rule in Package.swift, so look in both.
        let found = Bundle.module.url(forResource: "english", withExtension: "txt")
            ?? Bundle.module.url(forResource: "english", withExtension: "txt", subdirectory: "Resources")

        guard let url = found,
              let text = try? String(contentsOf: url, encoding: .utf8)
        else {
            fatalError("The BIP-39 word list is missing from the bundle.")
        }

        let words = text
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }

        precondition(words.count == 2048, "The word list should have 2048 words, it has \(words.count).")
        return words
    }

    /// Turn raw entropy into words. Used by the tests to check the official vectors.
    public static func fromEntropy(_ entropy: Data) throws -> String {
        let bytes = [UInt8](entropy)
        guard bytes.count % 4 == 0, bytes.count >= 16, bytes.count <= 32 else {
            throw VaultError.corrupt("entropy must be 16, 20, 24, 28 or 32 bytes")
        }

        let hash = [UInt8](SHA256.hash(data: entropy))
        let entropyBits = bytes.count * 8
        let totalBits = entropyBits + entropyBits / 32

        func bit(_ i: Int) -> Bool {
            if i < entropyBits {
                return (bytes[i / 8] >> (7 - i % 8)) & 1 == 1
            }
            let j = i - entropyBits
            return (hash[j / 8] >> (7 - j % 8)) & 1 == 1
        }

        var words: [String] = []
        words.reserveCapacity(totalBits / 11)

        for start in stride(from: 0, to: totalBits, by: 11) {
            var index = 0
            for b in 0..<11 { index = (index << 1) | (bit(start + b) ? 1 : 0) }
            words.append(wordList[index])
        }

        return words.joined(separator: " ")
    }

    /// Check a typed phrase and hand back its entropy. The thrown message says exactly what
    /// is wrong, so a typo can be fixed instead of the vault being given up on.
    @discardableResult
    public static func parse(_ phrase: String) throws -> Data {
        let words = normalise(phrase).split(separator: " ").map(String.init)

        guard allowedWordCounts.contains(words.count) else {
            throw VaultError.badPhrase("A recovery phrase is \(defaultWordCount) words. You typed \(words.count).")
        }

        var indices: [Int] = []
        indices.reserveCapacity(words.count)

        for (i, word) in words.enumerated() {
            guard let index = wordIndex[word] else {
                throw VaultError.badPhrase("Word \(i + 1) (\"\(word)\") is not one of the 2048 words.")
            }
            indices.append(index)
        }

        let totalBits = words.count * 11
        let checksumBits = totalBits / 33
        let entropyBits = totalBits - checksumBits

        var bits = [Bool](repeating: false, count: totalBits)
        for (i, index) in indices.enumerated() {
            for b in 0..<11 { bits[i * 11 + b] = (index >> (10 - b)) & 1 == 1 }
        }

        var entropy = [UInt8](repeating: 0, count: entropyBits / 8)
        for i in 0..<entropyBits where bits[i] {
            entropy[i / 8] |= UInt8(1 << (7 - i % 8))
        }

        let hash = [UInt8](SHA256.hash(data: Data(entropy)))
        for i in 0..<checksumBits {
            let expected = (hash[i / 8] >> (7 - i % 8)) & 1 == 1
            if bits[entropyBits + i] != expected {
                throw VaultError.badPhrase("Every word is real, but they don't add up - one of them is the wrong word.")
            }
        }

        return Data(entropy)
    }

    /// The exact text the key is derived from: lowercase, single spaces, no leading or
    /// trailing space, NFKD. This must be byte-identical to Mnemonic.Normalise on the C#
    /// side or a correct phrase will not open the vault.
    public static func normalise(_ phrase: String) -> String {
        phrase
            .lowercased()
            .split(whereSeparator: { $0.isWhitespace })
            .joined(separator: " ")
            .decomposedStringWithCompatibilityMapping
    }
}
