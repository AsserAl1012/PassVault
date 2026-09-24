# PassVault — how to use it

Everything you can do with PassVault, in order, in plain words.

If you only read one thing: **write your 12 words on paper and keep them somewhere safe.**
Everything else in this manual can be undone. Losing those cannot.

---

## Contents

1. [What it is](#what-it-is)
2. [What you need](#what-you-need)
3. [Setting up, the first time](#setting-up-the-first-time)
4. [Every day after that](#every-day-after-that)
5. [Your credentials](#your-credentials)
6. [Backing up](#backing-up)
7. [When something goes wrong](#when-something-goes-wrong)
8. [Using a second PC](#using-a-second-pc)
9. [Updating](#updating)
10. [The licence](#the-licence)
11. [Where your files live](#where-your-files-live)
12. [What it protects you from, and what it does not](#what-it-protects-you-from-and-what-it-does-not)

---

## What it is

PassVault keeps your passwords in one encrypted file. To open that file you need **three
things at once**:

1. **Your USB stick** — the one you set up
2. **The PC you set up** — a secret sealed inside its TPM chip, which cannot be copied off it
3. **Your 4-digit PIN** — in your head

Missing any one of them and it does not open. There is no account, no cloud, and no server.
Nothing ever leaves your machine.

If your stick is lost or broken, your **12-word phrase** gets everything back.

---

## What you need

- **A USB stick.** Any one. It does not need to be special, encrypted or expensive. PassVault
  uses about 50KB of it and leaves everything else on it alone.
- **Windows 10 or 11.**
- **A pen and paper**, for the 12 words. Not a photo, not a note on your phone — paper.

---

## Setting up, the first time

### Step 1 — Open PassVault

It opens on the unlock screen. The band across the top tells you what it can see.

| The band says | What it means |
|---|---|
| **No stick plugged in** | Plug yours in. The screen notices on its own within 2 seconds. |
| **Empty stick found** | A stick is there with no vault on it. Ready to set up. |
| **Stick connected** | Your vault is on this stick. |

### Step 2 — Set up the stick

With an empty stick in, press **Set up this stick**.

PassVault creates your vault and shows you **12 words**.

### Step 3 — Write the 12 words down

This is the only time you will ever see them. They are not stored anywhere — not on the
stick, not on your PC, nowhere. If you lose them and your stick breaks, your passwords are
gone and nobody can recover them, including me.

Write them on paper, in order, numbered. Keep the paper somewhere you would keep a passport.

Tick **I have written them down**, then press **Continue**.

### Step 4 — Choose a PIN

Four digits, typed twice. This is what you will use every day.

Press **Finish setting up**. You will see the lock mechanism work for about a second while
your key is derived — that pause is deliberate, and it is what makes the PIN safe.

Your vault opens. You are done.

---

## Every day after that

1. Plug your stick in
2. Open PassVault
3. Type your PIN

It opens as soon as the fourth digit lands. No Enter needed.

**The four pins** you see are the four things being checked, and each one drops when that
thing is genuinely in hand: your stick, this PC's chip, your key, your vault. When you unlock
with your 12 words instead, the second pin stays up — because your phrase does not use this
PC's chip.

### If you type the PIN wrong

It tells you how many tries are left. After three wrong tries it starts making you wait
longer each time. After ten, the PIN switches off on that PC and you use your 12 words, which
switches it back on.

Nothing is deleted. Ever.

### Locking

- Press **Lock** any time
- It locks itself after **5 minutes** of you not touching it

The band at the bottom counts down — `locks in 4:12` — and turns brass in the last minute, so
it never surprises you.

---

## Your credentials

### Adding one

Press **Add credential**. Fill in what you have — you do not need all of it. A name is the
only thing required.

The little square next to the name becomes the site's real logo as soon as you type something
it recognises, like `github.com` or `steam`. Anything it does not recognise gets a coloured
letter instead, so every entry still has its own mark.

Press **Save**.

### Finding one

Type in the search box. It matches the site name, the username, the email **and** the notes —
so searching your email address finds every account using it.

### Changing one

Click it, change what you want, press **Save**. Save stays greyed out until you actually
change something.

To rename it, click the big title at the top of the panel and type.

### Copying a password

Press the **copy** button next to any field.

**The clipboard clears itself 20 seconds later.** You do not have to do anything.

### Seeing a password

Press the **eye** button. The dots are always the same length, so nobody looking over your
shoulder can tell how long your password is.

### Making a strong password

Press the **circular arrow** next to the password. PassVault makes a new 20-character password
and shows it, so you can see it worked. Press it again for a different one.

**It is not kept until you press Save.** When you are changing a password on a website, copy the
new one, change it on the website, and only press Save once the website has accepted it. If the
website's change fails, click another credential and choose **Discard**: your old password is
still there.

The passwords use capitals, small letters, numbers and a few symbols that almost every site
accepts. They leave out characters that are easy to mix up, like `0` and `O`, or `1`, `l` and `I`.

### Deleting one

Press **Delete**. The footer asks you to confirm, right there. If you ignore it for 8 seconds
it backs out on its own.

---

## Backing up

Press **Back up** and save the file somewhere.

That file **plus your 12 words** is a complete recovery. You do not need your stick or your
PC — just those two things.

**It is safe to put in OneDrive, Google Drive or Dropbox.** It is encrypted, and it does not
contain the USB secret, so a cloud breach gets a cloud provider nothing but noise.

> **Do not copy the whole stick to the cloud.** That would upload the USB secret along with
> the vault, which hands over one of your three ingredients. Use the Back up button, which
> writes only the safe part.

PassVault also keeps a **spare copy** of your vault on each PC automatically, updated every
time you unlock or save. That is what saves you if the stick snaps.

---

## When something goes wrong

### "Your PassVault stick is not plugged in"

Plug it in. If it is already in, check it appears in File Explorer. A stick Windows cannot
read — one formatted for Linux or a Mac — gets no drive letter and PassVault cannot see it.

### I forgot my PIN

Press **Use my 12 words instead** on the unlock screen. Type them in. You are back in, and
the PIN is switched back on.

### I lost my stick / it broke / it was stolen

Press **Stick lost, stolen or broken?** at the bottom of the unlock screen.

It walks you through five steps:

1. **Where your credentials come from** — your stick if it is here, otherwise the spare copy
   on this PC, or a backup file you point it at. It picks whichever is newest.
2. **Your current 12 words**
3. **Which stick to use from now on** — plug in a new one and pick it
4. **A PIN for this PC**
5. **Done** — you get **new** 12 words, and a list of every password that was on the old stick

**Write the new 12 words down. The old ones stop working.**

The old stick becomes a dead key the moment this finishes, even if somebody else has it.

### Why does it want me to change my passwords after a theft?

Because whoever took your stick may have copied the encrypted file off it. They cannot open
it — but if they ever also got your PIN and your PC, those old passwords would be exposed.
Changing them closes that off for good. The list is there to tick off as you go.

### "The vault on this stick is older than the one this machine last saw"

Somebody has put an old copy of your vault back, which would quietly undo password changes.
PassVault refuses to open it.

If you did this yourself — restored an old backup on purpose — use your 12 words, which skips
the check.

### "This stick was re-keyed after this machine was set up"

You replaced the stick on a different PC. Set this one up again with your new 12 words.

### I reinstalled Windows / replaced the motherboard

The TPM secret was tied to that machine and is gone. Use your 12 words to set the PC up
again and choose a new PIN. Your vault is untouched.

### PassVault will not open at all

Your vault is still there. In order:

1. Try your **12 words** instead of the PIN
2. Try the **spare copy** — press **Stick lost, stolen or broken?**, which finds it
3. Try a **backup file** — the same screen has **Use a backup file instead**

---

## Using a second PC

1. Plug your stick into the other PC
2. Open PassVault — it says **Set this PC up**
3. Type your 12 words once
4. Choose a PIN for that PC — it can be different

Each PC gets its own PIN and its own TPM secret. They do not share anything except the stick.

---

## Updating

**PassVault never checks for updates on its own, because it never contacts anything.** That
is the same promise that keeps your vault private, and it applies here too.

So updating is something you do, not something that happens to you.

**If you installed from the Microsoft Store**, Windows keeps it up to date for you. The Store
does the talking; PassVault still never opens a connection itself.

**If you installed the downloaded setup file**, download the new one and run it. It installs
over the top. You do **not** need to uninstall first.

**Your vault, your PIN and your licence all survive an update.** The installer only replaces
the program. Everything in `%LOCALAPPDATA%\PassVault` is left exactly as it was, and your
stick is never touched.

To see which version you have, press the button in the top bar — the one showing your name or
"Free version". The version is on the right of the band at the top.

### The honest part

Because nothing phones home, **there is no way to tell you about an important fix.** If a
security problem is ever found and fixed, you will only learn about it by looking.

That is a real cost of building it this way, and it is worth knowing rather than discovering.
Check for a new version now and then - once every few months is plenty.

## The licence

Press the button in the top bar — it says **Free version** until you own a copy, then your
name.

**The free version is complete and safe.** Unlimited credentials, the full encryption, your
12 words, backups, re-keying — none of that ever costs money.

Buying adds convenience: importing from other password managers, more than one vault, the
browser extension and sync when they exist.

Your licence never expires, has no account, and is checked on your own machine. Nothing can
switch it off. See [LICENCE.md](LICENCE.md) — including the promise that if this project is
ever abandoned, the source gets published so it cannot die on you.

---

## Where your files live

**On your stick:**

```
<drive>\PassVault\vault.pv       your encrypted vault
<drive>\PassVault\keyfile.pvk    the USB secret (52 bytes)
```

**On each PC:**

```
%LOCALAPPDATA%\PassVault\machine.pvm   this PC's PIN setup
%LOCALAPPDATA%\PassVault\device.bin    this PC's secret, sealed by the TPM
%LOCALAPPDATA%\PassVault\vault.pv      the spare copy of your vault
%LOCALAPPDATA%\PassVault\licence.txt   your licence, if you have one
```

Paste `%LOCALAPPDATA%\PassVault` into File Explorer's address bar to get there.

**Uninstalling PassVault does not delete any of this.** That is on purpose — the spare copy
is a backup, and an uninstaller should never quietly destroy a backup.

---

## What it protects you from, and what it does not

**It protects you from:**

- Someone stealing your stick — it is useless without your PC and your PIN
- Someone stealing your laptop — useless without your stick
- Someone copying your vault file — it is encrypted, and the phrase behind it is 128-bit
- A cloud breach — there is no cloud
- Someone putting an old copy of your vault back to undo a password change

**It does not protect you from:**

- Someone taking **your PC and your stick together** and later learning your PIN
- **Malware on your PC while the vault is open** — it can read everything on screen. Every
  password manager in the world shares this.
- **Losing your 12 words and your stick at the same time.** Nobody can help you then. Not me,
  not anyone. That is the point.

The full reasoning, including the things deliberately not built and why, is in
[DESIGN.md](DESIGN.md).
