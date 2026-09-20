import Foundation
import Security

public enum Random {
    /// Cryptographic random bytes from the system. Never Int.random or arc4random for keys.
    public static func bytes(_ count: Int) throws -> Data {
        var data = Data(count: count)

        let status = data.withUnsafeMutableBytes { buffer -> Int32 in
            guard let address = buffer.baseAddress else { return errSecParam }
            return SecRandomCopyBytes(kSecRandomDefault, count, address)
        }

        guard status == errSecSuccess else {
            throw VaultError.corrupt("the system would not give us random bytes")
        }
        return data
    }
}
