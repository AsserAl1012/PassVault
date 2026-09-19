# PassVault — how it works

Plain-words spec. Both the Windows app and the iPhone app must follow this exactly,
or a vault saved on one won't open on the other. **This file is frozen** — changing it
later means breaking both apps at once, at the worst possible moment (someone's recovery).

---

## The short version

There is one **master key**. The vault of passwords is encrypted with it. The master key
itself is never stored in the clear — it's locked away twice, so there are two ways in:

1. **Daily way**: your USB stick + a secret held by your PC's chip + your 4-digit PIN.
2. **Recovery way**: your 12-word phrase. Works on any machine, even with the stick gone.

Lose the stick, use the words. Lose the PC, use the words. Lose the words *and* the stick,
you lose everything — that's the whole point.

---

## The three ingredients for the daily unlock

| Ingredient | Size | Where it lives | Can a thief get it? |
|---|---|---|---|
| USB secret | 32 bytes | plain file on the stick | Yes, if they hold the stick |
| Device secret | 32 bytes | sealed in your PC's TPM chip | No — it can't leave that machine |
| PIN | 4 digits | your head | No |

All three are needed. The USB secret is stored in plain sight on the stick on purpose —
we assume a thief who holds the stick for one minute has it. It's a *token you carry*,
not a secret. The real protection is that it's useless without the other two.

**Why the TPM matters:** without it, a thief who steals your laptop bag (stick *and* PC
together) only needs to guess 4 digits — 10,000 tries, done offline, same afternoon. With
the TPM, the unlock can only happen on that physical machine, so copying the folder or
imaging the disk gets them nothing.

**The cost:** if the TPM is cleared, the motherboard is replaced, or Windows is reinstalled,
that machine's PIN slot is dead. You then use your 12 words to set the machine up again.

---

## Files

### `vault.pv` — the vault itself

Lives on the USB stick **and** is mirrored onto every PC you set up. Also exportable as a
backup file. It's encrypted, so extra copies are safe copies.

```
"PVLT"            4 bytes, magic
formatVersion     uint16, little-endian, = 1
headerLength      uint32, little-endian
header            headerLength bytes of UTF-8 JSON
body              nonce(12) || ciphertext || tag(16)
```

The header JSON:

```json
{
  "formatVersion": 1,
  "vaultId": "<base64, 16 random bytes, never changes>",
  "epoch": 1,
  "revision": 1,
  "kdf": { "algo": "argon2id", "iterations": 3, "memoryKiB": 65536, "parallelism": 1, "outputBytes": 32 },
  "slots": [ { "name": "recovery", "salt": "<base64, 16 bytes>", "blob": "<base64>" } ]
}
```

- **epoch** goes up by 1 every time you re-key after a theft.
- **revision** goes up by 1 every time you save.
- Every PC and phone remembers the highest epoch and revision it has ever seen, and
  **refuses anything lower**. Without this, anyone with 20 seconds at your stick can put
  an old vault back — and the password you rotated after a breach is quietly the old one again.

### `keyfile.pvk` — on the USB stick

```
"PVKF" (4) || stickId (16 random bytes) || usbSecret (32 random bytes)
```

Not encrypted. The stick ID is only used to print a friendly "that is not your stick"
message — **never** as key material.

### `machine.pvm` — on each PC (never on the stick, never in the cloud)

Holds that machine's PIN slot, the failed-attempt count, and the highest epoch/revision
seen. Keeping this off the stick is what makes stick-theft safe: the thief has no blob to
test PIN guesses against.

---

## Encryption

**AES-256-GCM**, always. Every sealed blob is `nonce(12) || ciphertext || tag(16)` —
this matches Apple CryptoKit's combined format, so iOS needs no byte-shuffling.

- A **fresh random 12-byte nonce on every single encryption.** Never a counter. A counter
  plus any backup or restore repeats a nonce, and a repeated nonce with the same key leaks
  the contents of both files and lets someone forge a fake vault.
- On .NET 9, always `new AesGcm(key, tagSizeInBytes: 16)`.

### What gets authenticated alongside what (the "AAD")

| Sealed thing | AAD |
|---|---|
| vault body | the exact header bytes, as read from the file |
| recovery slot | `pv1:slot:recovery:<vaultId>` |
| PIN slot | `pv1:slot:pin:<vaultId>:<machineId>` |

The header is authenticated **as the exact bytes on disk**, never re-encoded — .NET and
Swift do not write identical JSON. This is what stops someone editing `memoryKiB` from
65536 down to 8 and making your PIN guessable at a speed we assumed was impossible.

Each unlock path opens **exactly one named slot**. We never loop over slots trying each
one until something works — that's how wrong-key-still-decrypts bugs get in.

---

## Key derivation — Argon2id

Settings are read from the header, not from constants in the code:

```
algo = argon2id, iterations = 3, memory = 65536 KiB (64 MiB), parallelism = 1,
salt = 16 bytes, output = 32 bytes
```

> **Watch the units.** Konscious on Windows takes memory in **KiB** (65536).
> libsodium on iOS takes it in **bytes** (67108864). Get this wrong and both sides still
> "work" while one is a thousand times weaker. `parallelism` must be 1 and the salt
> exactly 16 bytes, or libsodium can't reproduce the key at all.

### Recovery slot

```
key   = Argon2id(password = UTF-8 of the normalised phrase, salt = slot salt)
blob  = Seal(key, masterKey, aad = "pv1:slot:recovery:<vaultId>")
```

### PIN slot

```
input = usbSecret(32) || deviceSecret(32) || pin as 4 ASCII digits(4)   <- 68 bytes, plain concatenation
key   = Argon2id(password = input, salt = slot salt)
blob  = Seal(key, masterKey, aad = "pv1:slot:pin:<vaultId>:<machineId>")
```

Plain concatenation of fixed-length parts, **not** Argon2's "secret" parameter —
libsodium on iOS doesn't have that parameter, so it isn't portable.

---

## The 12-word phrase

Standard **BIP-39, 12 words, English**. 128 bits of entropy. The app generates it —
**the user never picks their own words**, because twelve self-chosen English words are
weak enough to guess.

The phrase is turned into a key from **the typed text**, not from the raw bits:

```
lowercase -> trim -> collapse runs of whitespace to one space -> NFKD normalise -> UTF-8
```

Example: `abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about`

**Why 12 and not 10:** BIP-39 only defines 12/15/18/21/24 words. At 12 there's a built-in
checksum, so if you mistype one word the app can tell you *"word 7 isn't a real word"*.
At 10 there is no checksum — a typo and a wrong phrase look identical, so the app can only
say "wrong", and you'd give up on a vault that was perfectly recoverable. Also 12 words
means standard test vectors and a ready-made library on both platforms instead of
hand-rolled bit-packing written twice.

---

## Things we deliberately do NOT build

These sound protective and aren't. Each was considered and dropped on purpose:

- **Deleting a stolen USB's contents remotely.** A USB stick has no processor, no clock
  and no radio. Nothing runs on it. This is not possible for anyone, at any price.
- **Locking the keyfile to the drive's serial number.** The serial is readable by anyone
  holding the drive and sits in any disk image, so it hides nothing. Meanwhile cheap sticks
  ship with blank or duplicate serials, so it would push real owners into recovery.
- **An expiry date on the stick ("dead-man switch").** We'd check it inside our own app,
  against a clock the thief controls. A thief simply never runs our app — he reads the
  bytes directly. The only person it ever locks out is you, back from a long trip.
- **Wiping a revoked stick on sight.** It can only ever fire on *your* machines, never the
  thief's, and it destroys data on a trigger an attacker can pull.
- **"Securely erasing" anything on disk.** Wear levelling, the NTFS journal, shadow copies
  and File History all make that claim false. Destruction only means something when it's a
  key the hardware really erases — which is what the TPM gives us.

### What we do instead when a stick is stolen: re-key

Enter your 12 words on a machine you still have. The app mints a brand-new master key, a
new USB secret and a new 12-word phrase, writes them to a fresh stick, and bumps the epoch.
The old stick is a dead key from that moment. The app then shows you every entry that was
on the stolen stick with a tick-box, so you can work through changing those passwords —
because the honest truth is a thief who held your stick may have copied the ciphertext,
and if he ever gets your PIN and your PC, those old passwords are exposed.

---

## Honest limits

1. A thief who takes **your PC and your stick together** and later learns your PIN gets in.
2. **Malware on your machine while the vault is unlocked** reads everything. Every password
   manager on earth shares this.
3. Anyone holding the stick can copy the encrypted vault and attack the 12-word phrase
   forever. 128 bits means "forever" is the right word — but re-keying after a theft is
   still the correct move.

---

## Cross-platform notes

- **iOS never touches a USB stick.** An iPhone can only read a USB drive at all if it's a
  keypad-unlock model. On iOS the master key is held in the Keychain with access control
  `.biometryCurrentSet` and accessibility `WhenPasscodeSetThisDeviceOnly`, sync off. The
  PIN path uses a second 32-byte device secret in its own Keychain item.
- **iOS ships read-only in v1.** It shows credentials; it doesn't edit them. That skips
  the whole merge problem until real sync is built.
- Every entry carries a UUID, a last-changed time in milliseconds, and deletions are kept
  as tombstones rather than removed — so sync can be added later without a format change.
- `tests/vectors.json` holds fixed inputs and expected outputs. Both the C# tests and the
  Swift tests read that same file. If the two platforms ever drift apart, that file catches it.
