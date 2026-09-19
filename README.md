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
  Machine/DeviceSecret.cs  this PC's 32-byte secret, sealed in the TPM
  Machine/MachineFile.cs   machine.pvm - what this one PC knows
  Machine/Enrolment.cs     setting a PC up, and the two ways to unlock
  Usb/Keyfile.cs           the little file on the stick
  Usb/UsbDetect.cs         finding plugged-in sticks
  Usb/VaultStorage.cs      reading and writing the stick safely
src/PassVault.App/         the Windows app (the window you actually use)
  UnlockWindow.xaml        setup, PIN unlock, recovery
  MainWindow.xaml          search, list, add and edit
src/PassVault.Cli/         console tool, handy for scripting and testing
src/PassVault.Tests/       the tests
```

## Run the tests

```bash
dotnet test
```

## Progress

- [x] **Step 1** — crypto core: master key, 12-word phrase, AES-256-GCM vault, PIN wrapping
- [x] **Step 2** — find the USB stick, write the keyfile, bind a stick to a vault
- [x] **Step 3** — the machine file: TPM device secret, PIN setup and unlock, attempt limit
- [x] **Step 4** — the app window: list, search, add and edit credentials
- [ ] **Step 5** — re-key after a theft, backup export, rollback refusal
- [ ] **Step 6** — iPhone app (Face ID + PIN, read-only to start). Needs a Mac to build.
- [ ] **Step 7** — sync server

## What works right now

Run the app:

```bash
dotnet run --project src/PassVault.App
```

The window walks you through setting up a stick and this PC on first run. After that it is
stick in, PIN, done. Everything is also scriptable from the console tool:

```bash
dotnet run --project src/PassVault.Cli -- find
dotnet run --project src/PassVault.Cli -- init "E:\"
dotnet run --project src/PassVault.Cli -- enrol "E:\"
dotnet run --project src/PassVault.Cli -- add "E:\"
dotnet run --project src/PassVault.Cli -- list "E:\"
```

**Setting up, once:** `init` makes the stick and prints your 12 words. `enrol` sets up this
PC — you type the 12 words once and choose a PIN.

**Every day after that:** stick in, PIN, done.

- make a new vault with a random master key and a 12-word phrase
- lock that master key away behind the phrase, and behind USB + this PC's chip + PIN
- find plugged-in sticks, set one up, save and reload credentials on it
- unlock with the PIN alone, with all three ingredients genuinely required
- keep a spare copy of the encrypted vault on each PC, so a dead stick is not the end
- count wrong PINs, make you wait longer each time, switch the PIN off after ten
- survive the stick being pulled out mid-save
- spot a damaged file, an edited header, or an old vault being put back
- re-key after a theft: new master key, new phrase, entries kept, old phrase dead
- copy a username, email or password, with the clipboard wiped 20 seconds later
- lock on demand, and lock itself after 5 minutes idle
