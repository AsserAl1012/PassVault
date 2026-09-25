# Building, running and shipping PassVault

Your side of it. Every command you need, and what each one is for.

The user-facing manual is [MANUAL.md](MANUAL.md). How it works underneath is
[DESIGN.md](DESIGN.md). How it looks and why is [UI.md](UI.md).

---

## What you need installed

| | | |
|---|---|---|
| **.NET 9 SDK** | required | `dotnet --list-sdks` should show 9.x |
| **Inno Setup 6** | to build the installer | `winget install JRSoftware.InnoSetup` |
| **A USB stick** | to test properly | any spare one |
| **A Mac** | only for the iPhone app | see [ios/README.md](ios/README.md) |

---

## Everyday commands

Run all of these from the repository root.

### Run the app

```bash
dotnet run --project src/PassVault.App
```

### Run the tests

```bash
dotnet test
```

203 tests, about 40 seconds. Most of that is Argon2 doing real work on purpose.

### Build everything

```bash
dotnet build
```

---

## The console tool

`PassVault.Cli` does everything the app does, without a window. Useful for testing, scripting
and setting up a stick quickly.

```bash
dotnet run --project src/PassVault.Cli -- find              # list sticks Windows can read
dotnet run --project src/PassVault.Cli -- init   "E:\"      # set up a stick, prints the 12 words
dotnet run --project src/PassVault.Cli -- enrol  "E:\"      # set this PC up: 12 words, then a PIN
dotnet run --project src/PassVault.Cli -- status "E:\"      # what is on the stick and on this PC
dotnet run --project src/PassVault.Cli -- list   "E:\"      # show credentials
dotnet run --project src/PassVault.Cli -- add    "E:\"      # add one
dotnet run --project src/PassVault.Cli -- forget            # undo this PC's setup
```

`list` and `add` use your PIN if the PC is set up, and your 12 words if it is not.

### Starting from a clean slate

To wipe everything and test the first-run experience:

```bash
dotnet run --project src/PassVault.Cli -- forget
```
```bash
rm -r "E:\PassVault"
```
```bash
rm -r "$env:LOCALAPPDATA\PassVault"
```

> `forget` also deletes this PC's TPM key. The vault on the stick is untouched — you just
> need your 12 words to set the PC up again.

---

## The logo and the icon

The logo is real geometry in `tools/IconMaker`, not a picture file. Every size is drawn
fresh rather than scaled from one bitmap.

```bash
dotnet run --project tools/IconMaker
```

Writes into `brand/`:

- `passvault.ico` — 16 through 256, what the app and installer use
- `passvault-16/32/48/256.png`
- `installer-large*.bmp`, `installer-small*.bmp` — the wizard artwork at 100/125/150/200%

Change the shape by editing the `Mark()` function, then run it again. **The scaled BMPs
matter** — without them Inno draws the artwork at 100% into a slot sized for the user's
actual scaling and clips it.

---

## Building the installer

Two commands, in this order.

**1. Publish the app, self-contained:**

```bash
dotnet publish src/PassVault.App/PassVault.App.csproj -c Release -r win-x64 --self-contained true -o dist/app -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=en
```

Self-contained means .NET is inside the app, so there is no runtime for the user to install
and nothing to agree to. Single-file with compression takes it from 136MB across 255 files to
62MB across 6. The leftover DLLs are WPF's native ones, which cannot go in the bundle.

**2. Compile the installer:**

```bash
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\PassVault.iss
```

Out comes `dist/PassVault-1.0.0-setup.exe`, about 52MB.

> Inno Setup installs per-user via winget, so it is under `%LOCALAPPDATA%\Programs`, not
> Program Files. If you installed it another way, adjust the path.

### Testing the installer without clicking through it

```bash
.\dist\PassVault-1.0.0-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```
```bash
& "$env:LOCALAPPDATA\Programs\PassVault\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES
```

After uninstalling, check `%LOCALAPPDATA%\PassVault` is **still there**. The uninstaller is
meant to leave vault data alone, and that is worth re-checking whenever the script changes.

### Releasing a new version

Change the version in **two** places or they will disagree:

- `src/PassVault.App/PassVault.App.csproj` — `Version`, `FileVersion`, `AssemblyVersion`
- `installer/PassVault.iss` — `AppVersion`

Leave `AppId` alone. It is how Windows knows an install is an upgrade rather than a second
copy.

### Upgrades, and what must survive one

Running a newer setup over an older install upgrades it in place. No uninstall first.

**Tested end to end, not assumed** - installed 1.0.0, set up a stick, a PIN, a credential and
a licence, then installed 1.0.1 over the top:

| | |
|---|---|
| Installed Apps | one entry, 1.0.1 - not two copies |
| `vault.pv`, `machine.pvm`, `device.bin` | untouched |
| `licence.txt` | untouched, still verifies |
| The PIN | still opens the vault |
| The credential | still there, password intact |

Re-run that check whenever the installer script changes. The failure mode is silent and
severe: a user who loses `device.bin` loses their PIN, and one who loses `licence.txt` looks
unlicensed. Both would be blamed on the update, correctly.

### How users actually get updates

The app never checks for updates, because it never contacts anything - that is the same
promise that keeps the vault private. So the channel does it instead:

- **Microsoft Store** - Windows updates it automatically. The Store does the network, not
  PassVault, so the promise holds exactly as written.
- **winget** - `winget upgrade PassVault`. Same: the package manager does the network.
- **Direct download** - they run the new setup over the old install.

**Do not add an update checker to the app.** It would be the only outbound connection in the
product and it would undo the one claim that makes PassVault different. If you ever want one,
it has to be opt-in, off by default, and triggered by a button - and the Store route already
covers it for free.

---

## Licences

### Once, ever

```bash
dotnet run --project tools/LicenceMaker -- keygen
```

Writes your private key to `~/.passvault-signing-key` and prints the public half. Paste that
public half into `LicenceKey.PublicKey` in `src/PassVault.Core/Licensing/Licence.cs`.

> **Back up that private key.** Lose it and you cannot issue licences to existing buyers, and
> replacing it stops every licence you have ever issued from verifying. It is outside the
> repository on purpose and `.gitignore` covers the filename.

### Per buyer

```bash
dotnet run --project tools/LicenceMaker -- issue "Their Name"
```

Prints a block. Send it to them. They paste it into the app. It never expires and never
contacts anything.

### Checking one

```bash
dotnet run --project tools/LicenceMaker -- check path\to\licence.txt
```

Verifies it exactly the way the app does.

---

## What lives where

```
DESIGN.md         how the crypto and the file format work - FROZEN
MANUAL.md         the user manual
UI.md             the design system and the rules behind it
LICENCE.md        what buying it gets you
DEVELOPING.md     this file

src/PassVault.Core/       all the logic, no screens
  Crypto/                 AES-256-GCM, Argon2id, BIP-39, key wrapping
  Vault/VaultFile.cs      reading and writing vault.pv
  Usb/                    finding sticks, the keyfile, safe writes
  Machine/                the TPM secret, machine.pvm, enrolment, recovery
  Licensing/              the offline licence check
src/PassVault.App/        the Windows app
src/PassVault.Cli/        the console tool
src/PassVault.Tests/      203 tests
src/PassVault.Sync/       the LAN reachability probe (step 7, unfinished)

ios/                      the iPhone app - never compiled, needs a Mac
tests/                    vectors.json and a fixture vault, shared with the Swift tests
brand/                    the logo, the .ico, the installer artwork
installer/                the Inno Setup script
tools/IconMaker/          draws the logo
tools/LicenceMaker/       issues licences - never ship this
```

---

## Things that will trip you up

**The app's exe is `PassVault.exe`, not `PassVault.App.exe`.** `AssemblyName` is set in the
csproj so the installed file has the right name.

**Icon glyphs must go in a `TextBlock` with the `Glyph` style, never as a Button's `Content`.**
`TextFormattingMode="Display"` — which is what makes small text sharp — breaks symbol fonts,
and MDL2 glyphs come out as empty boxes. The `Glyph` style opts back into `Ideal`.

**Colours are declared as `<Color>` first, brushes derived from them.** A brush handed out by
`StaticResource` is shared and frozen, and animating it throws.

**`tests/vectors.json` and `tests/fixture-vault.pv` are read by both the C# and the Swift
tests, by path.** Do not copy them anywhere. That would let the two platforms drift apart
while both test suites stayed green, which is exactly what they exist to prevent.

**Never change `DESIGN.md`'s file format.** Both apps implement it, and the moment it breaks
is somebody's real recovery.

---

## Still to do

- **Check TPM and USB detection work inside an MSIX package.** This decides whether the free
  Microsoft Store route is possible, and nobody has documented it. Do this before any Store work.
- **Compile the iPhone app.** Needs a Mac. Run `swift test` in `ios/PassVaultKit` first — if
  that passes, the format agreement is proven and the rest is app code.
- **Sync.** Designed and reviewed, blocked on a network that isolates its clients. See the
  notes in `src/PassVault.Sync`.
- Three small UI items listed at the end of [UI.md](UI.md).
