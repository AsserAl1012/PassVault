# PassVault

A password, username and email manager for Windows, with an iPhone app later.

To open it on Windows you need three things: **your USB stick**, **the PC you set up**, and
**your 4-digit PIN**. If you lose the stick, a **12-word phrase** gets everything back.

[DESIGN.md](DESIGN.md) explains exactly how that works, in plain words, including the
parts of the idea that don't work and what we do instead.

## Where things are

```
DESIGN.md                  how it works - frozen, both apps follow it
tests/vectors.json         fixed inputs and answers, read by the C# AND Swift tests
src/PassVault.Core/        all the logic, no screens
  Crypto/Aead.cs           AES-256-GCM
  Crypto/Kdf.cs            Argon2id settings and derivation
  Crypto/Mnemonic.cs       the 12-word phrase
  Crypto/KeyWrap.cs        locking the master key away, one way in per slot
  Model/Entry.cs           one saved credential
  Vault/VaultFile.cs       reading and writing vault.pv
  Usb/Keyfile.cs           the little file on the stick
  Usb/UsbDetect.cs         finding plugged-in sticks
  Usb/VaultStorage.cs      reading and writing the stick safely
src/PassVault.Cli/         console tool to drive it until the window exists
src/PassVault.Tests/       the tests
```

## Run the tests

```bash
dotnet test
```

## Progress

- [x] **Step 1** — crypto core: master key, 12-word phrase, AES-256-GCM vault, PIN wrapping
- [x] **Step 2** — find the USB stick, write the keyfile, bind a stick to a vault
- [ ] **Step 3** — the machine file: TPM device secret, PIN setup and unlock, attempt limit
- [ ] **Step 4** — the app window: list, search, add and edit credentials
- [ ] **Step 5** — re-key after a theft, backup export, rollback refusal
- [ ] **Step 6** — iPhone app (Face ID + PIN, read-only to start). Needs a Mac to build.
- [ ] **Step 7** — sync server

## What works right now

Steps 1 and 2 — there is no window yet, but the console tool does the lot:

```bash
dotnet run --project src/PassVault.Cli -- find
dotnet run --project src/PassVault.Cli -- init "E:\"
dotnet run --project src/PassVault.Cli -- add "E:\"
dotnet run --project src/PassVault.Cli -- list "E:\"
```

- make a new vault with a random master key and a 12-word phrase
- lock that master key away behind the phrase, and behind USB + machine + PIN
- find plugged-in sticks, set one up, save and reload credentials on it
- survive the stick being pulled out mid-save
- spot a damaged file, an edited header, or an old vault being put back
- re-key after a theft: new master key, new phrase, entries kept, old phrase dead

The PIN is not wired up yet — that is step 3. For now the only way in is the 12 words.
