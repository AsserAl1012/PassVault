# The iPhone app

Written on Windows against the frozen format in [../DESIGN.md](../DESIGN.md). **None of it
has been compiled** — there is no Swift compiler on Windows. Everything here is ready for a
Mac, and the first thing to do on one is run the tests.

## What it does

Version 1 **only reads.** It shows your credentials; it does not change them. You make
changes in the Windows app and bring the vault file over again. That skips the entire
question of what happens when the same entry is edited in two places, which is the thing
that goes wrong in sync.

Three ways in, in the order you will use them:

1. **Face ID** — the master key sits in the Keychain behind `.biometryCurrentSet`.
2. **A 4-digit PIN** — for when Face ID says no.
3. **Your 12 words** — always works, and switches the PIN back on if it locked out.

## No USB, ever

An iPhone can only read a USB drive at all if it is a keypad-unlock model, and even then it
would be awkward. So the phone does not use the stick. It uses a copy of the encrypted vault
file, which you bring over with the **Back up** button in the Windows app.

The USB secret never touches the phone. That is deliberate: the phone is a second, weaker
door into the same vault, so it gets its own protections (Secure Enclave-backed Keychain,
Face ID, device passcode) instead of a copy of the Windows ones.

## Layout

```
PassVaultKit/            a Swift package - the format, and nothing to do with screens
  Sources/PassVaultKit/
    Aead.swift           AES-256-GCM via CryptoKit
    Kdf.swift            Argon2id via libsodium      <- check this first on a Mac
    Mnemonic.swift       the 12-word phrase
    KeyWrap.swift        unlocking the master key
    VaultFile.swift      reading vault.pv
    Entry.swift          one credential
  Tests/                 reads the SAME vectors.json and fixture vault the C# tests read

App/                     the SwiftUI app
  Sources/
    PassVaultApp.swift   locks itself the moment the app goes to the background
    Keychain.swift       the master key behind Face ID, plus this phone's own secret
    PhoneVault.swift     importing a vault, the three ways in, the rollback check
    UnlockView.swift     first run, Face ID, PIN, 12 words
    VaultView.swift      search, list, one credential
  project.yml            XcodeGen spec - there is no .xcodeproj in the repo on purpose
```

## Getting it running on a Mac

```bash
# 1. The part that proves the format matches. Do this first.
cd ios/PassVaultKit
swift test
```

That needs no Xcode project and no iPhone. It opens `tests/fixture-vault.pv` — a real vault
written by the Windows app — and checks every credential comes back exactly, plus the shared
`vectors.json`. **If that passes, the hard part is done.**

```bash
# 2. The app itself.
brew install xcodegen
cd ios/App
xcodegen
open PassVault.xcodeproj
```

Then set your own team under Signing & Capabilities and run it on your phone.

## What to check first, in order

This code has never been compiled. A three-way review went over it and fixed everything it
could prove from Windows, but four things genuinely cannot be settled without a Mac. They
are all in `Kdf.swift` or `Package.swift`, and all are a few lines:

1. **`import Clibsodium`** — `Package.swift` depends on swift-sodium's `Sodium` product, and
   `Clibsodium` comes along with it. That works today, but Xcode 16's explicit-module builds
   can reject it with *"No such module 'Clibsodium'"*. If so, add `Clibsodium` to the
   target's dependencies too.
2. **`crypto_pwhash_ALG_ARGON2ID13`** — a macro pointing at another macro in libsodium's
   headers. Swift usually imports that chain, not always. If it will not resolve, the value
   is `2`; swift-sodium hard-codes it in its own enum for the same reason.
3. **The libsodium xcframework slices** — `swift test` builds for macOS, and `Package.swift`
   declares `.macOS(.v13)`. A missing slice shows up as a *link* error, not a compile error,
   so do not go looking in the Swift.
4. **The concurrency rewrite** — `PhoneVault`'s unlock methods are `async` and push only the
   slow call off the main actor. Under Swift 6 strict concurrency this may want extra
   `Sendable` annotations. `Data`, `String`, `KeySlot` and `KdfSettings` are all already
   `Sendable`, so anything it complains about should be small.

## The one thing most likely to break

`Kdf.swift` calls libsodium's `crypto_pwhash` through swift-sodium. If the package's product
name or the C symbol import has moved, that file is where it will complain, and it is about
ten lines. The test `testArgon2idGivesTheSameKeyAsTheFrozenVector` tells you immediately
whether it is right — it derives a key from fixed bytes and compares against the hex the
Windows side produced.

**Watch the memory units.** libsodium takes memory in **bytes** (67108864). The header
stores **KiB** (65536), because that is what the Windows library wants. Mixing them up still
"works" on both sides while being a thousand times weaker, so it is converted in exactly one
place: `KdfSettings.memoryBytes`.

## Costs, honestly

You need a Mac only at this point. Cheapest real options:

- **Rent one by the hour** — Scaleway M1 is roughly €0.10/hour. A few hours covers this.
- **A used Mac mini M1** — around $300–400 if you will keep updating the app for years.
- A free Apple ID signs the app onto your own phone for 7 days at a time. A paid developer
  account ($99/year) makes that permanent and is the only way to reach the App Store.
