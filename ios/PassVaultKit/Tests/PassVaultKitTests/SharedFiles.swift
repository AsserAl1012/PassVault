import Foundation

/// The one copy of the shared test files, up in the repo's own tests/ folder - the same
/// files the C# tests link to.
///
/// They are deliberately NOT bundled into this package. A copy would drift: someone edits
/// tests/vectors.json on the Windows side, the C# tests move with it, and these keep passing
/// against a stale copy - green on both sides while the two apps no longer agree. Reading
/// the real path means a rename over there breaks these tests loudly.
enum SharedFiles {
    static let folder = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()          // PassVaultKitTests
        .deletingLastPathComponent()          // Tests
        .deletingLastPathComponent()          // PassVaultKit
        .deletingLastPathComponent()          // ios
        .deletingLastPathComponent()          // repo root
        .appendingPathComponent("tests")

    static let vectors = folder.appendingPathComponent("vectors.json")
    static let fixtureInfo = folder.appendingPathComponent("fixture.json")
    static let fixtureVault = folder.appendingPathComponent("fixture-vault.pv")
}
