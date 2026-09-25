# PassVault

A password, username and email manager for Windows, with an iPhone app later.

To open it on Windows you need three things: **your USB stick**, **the PC you set up**, and
**your 4-digit PIN**. If you lose the stick, a **12-word phrase** gets everything back.

**[MANUAL.md](MANUAL.md)** — how to use it. Start here.
**[DEVELOPING.md](DEVELOPING.md)** — how to build, run, package and sell it.
**[DESIGN.md](DESIGN.md)** — how the crypto and the file format work, including the parts of
the original idea that don't work and what is done instead.
**[UI.md](UI.md)** — the design system. **[LICENCE.md](LICENCE.md)** — what buying it gets you.

## Where things are

```
DESIGN.md                  how it works - frozen, both apps follow it
tests/vectors.json         fixed inputs and answers, read by the C# AND Swift tests
tests/fixture-vault.pv     a real vault file both apps must open (fake data, public phrase)
src/PassVault.Core/        all the logic, no screens
  Crypto/Aead.cs           AES-256-GCM
  Crypto/Kdf.cs            Argon2id settings and derivation
  Crypto/Mnemonic.cs       the 12-word phrase
  Crypto/KeyWrap.cs        locking the master key away, one way in per slot
  Model/Entry.cs           one saved credential
  Import/CsvImport.cs      reading exports from browsers and other password managers
  Vault/VaultFile.cs       reading and writing vault.pv
  Machine/DeviceSecret.cs  this PC's 32-byte secret, sealed in the TPM
  Machine/MachineFile.cs   machine.pvm - what this one PC knows
  Machine/Enrolment.cs     setting a PC up, and the two ways to unlock
  Machine/Recovery.cs      replacing a stolen stick, and backup files
  Usb/Keyfile.cs           the little file on the stick
  Usb/UsbDetect.cs         finding plugged-in sticks
  Usb/VaultStorage.cs      reading and writing the stick safely
src/PassVault.App/         the Windows app (the window you actually use)
  UnlockWindow.xaml        setup, PIN unlock, recovery
  MainWindow.xaml          search, list, add and edit
  RecoveryWindow.xaml      replacing a lost, stolen or broken stick
src/PassVault.Cli/         console tool, handy for scripting and testing
src/PassVault.Tests/       the tests
ios/                       the iPhone app - see ios/README.md (needs a Mac to build)
brand/                     the logo, the .ico, installer artwork (regenerate with tools/IconMaker)
installer/PassVault.iss    the Inno Setup script
tools/IconMaker/           draws the logo as geometry and writes the .ico
tools/LicenceMaker/        the seller's tool - issues licences, never shipped
LICENCE.md                 what buying PassVault actually gets you
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
- [x] **Step 5** — re-key after a theft, backup export, rollback refusal
- [~] **Step 6** — iPhone app: written and reviewed, **never compiled**. Needs a Mac. See [ios/README.md](ios/README.md)
- [ ] **Step 7** — sync server (LAN version designed and reviewed; blocked on network, see notes)
- [x] **Brand, installer and licensing** — see LICENCE.md and installer/

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
- replace a lost, stolen or broken stick: new keys, new words, old stick dead
- save a backup file that plus your 12 words gets everything back
- make a strong password with one button
- keep its windows out of screenshots, recordings and screen sharing
- import from 14 browsers and password managers, keeping 2FA secrets and leaving cards behind
