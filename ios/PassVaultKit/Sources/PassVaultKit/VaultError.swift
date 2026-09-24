import Foundation

/// Everything that can go wrong, with a message meant for the person holding the phone.
public enum VaultError: LocalizedError, Equatable {
    case notAVault
    case corrupt(String)
    case wrongKey
    case badKeySize(Int)
    case unsupported(String)
    case badPhrase(String)
    case noSlot(String)
    case newerFormat(Int)
    case olderThanWeHaveSeen

    public var errorDescription: String? {
        switch self {
        case .notAVault:
            return "That file is not a PassVault vault."
        case .corrupt(let why):
            return "This vault file is damaged - \(why)."
        case .wrongKey:
            return "That did not open the vault."
        case .badKeySize(let got):
            return "A key must be 32 bytes, got \(got)."
        case .unsupported(let what):
            return "This vault uses something this app does not know: \(what)."
        case .badPhrase(let why):
            return why
        case .noSlot(let name):
            return "This vault has no \"\(name)\" way in."
        case .newerFormat(let version):
            return "This vault was written by a newer version of PassVault (format \(version)). Update the app."
        case .olderThanWeHaveSeen:
            return "This vault is older than the one this phone last saw. Someone may have put an old copy back, "
                 + "which would quietly undo password changes. Refusing to open it."
        }
    }
}
