// swift-tools-version: 5.9
import PackageDescription

// The iPhone half of PassVault's vault format. Everything in here must produce the exact
// same bytes as the C# side in src/PassVault.Core - DESIGN.md is the contract, and the
// tests read the same tests/vectors.json the C# tests read.
let package = Package(
    name: "PassVaultKit",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [
        .library(name: "PassVaultKit", targets: ["PassVaultKit"])
    ],
    dependencies: [
        // libsodium, for Argon2id. Apple has no Argon2 of its own.
        .package(url: "https://github.com/jedisct1/swift-sodium.git", from: "0.9.1")
    ],
    targets: [
        .target(
            name: "PassVaultKit",
            dependencies: [.product(name: "Sodium", package: "swift-sodium")],
            resources: [.process("Resources/english.txt")]
        ),
        // The shared vectors and the fixture vault are NOT copied in here. The tests read
        // ../../../../tests directly, so there is exactly one copy of each in the repo and
        // editing one on the Windows side breaks these tests loudly instead of quietly.
        .testTarget(
            name: "PassVaultKitTests",
            dependencies: ["PassVaultKit"]
        )
    ]
)
