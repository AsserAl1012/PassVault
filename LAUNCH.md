# Launching PassVault

How to get PassVault into the Microsoft Store, and how to get people to use it and pay for
it — with no money to spend.

Researched September 2026, then fact-checked: 86 claims confirmed, none disproved, 5 left
uncertain. Prices, fees and rules change, so check the linked source before acting on any
number. Anything marked *(unconfirmed)* is a best reading of thin evidence.

---

## The short version

- **Go through the Microsoft Store.** It is the only free way to get rid of the "Windows
  protected your PC" warning. The Store signs the app for you, the developer account is free,
  and Microsoft takes nothing when you sell the licence yourself.
- **The two app changes the Store needed are done** (1.0.2): this PC's files moved out of
  AppData, and the TPM key now lives inside `machine.pvm`, so a Store uninstall loses nothing.
- **The PIN is fixed** (1.0.2): the PC's chip now checks it and counts wrong guesses, so
  someone with the PC and stick together can no longer try every PIN in a few hours.
- **Five decisions are yours**, listed at the end. The biggest is whether to open-source it:
  most free ways to get noticed and trusted depend on that.

---

## Part 1 — The Microsoft Store

### Why the Store and not a signed installer

| | Store (MSIX package) | Our Inno installer |
|---|---|---|
| Who signs it | Microsoft, free | Us — a certificate costs about £120–240 a year |
| "Windows protected your PC" | Never shown | Shown on every new version |
| Smart App Control (Windows 11) | Should pass *(unconfirmed — test it)* | Blocks it, and there is no per-app exception |
| Cost | Free | Certificate every year |

- The cheaper signing service (Azure Artifact Signing, about $10 a month) only takes
  *individual* developers in the USA and Canada. SignPath signs free, but only for projects
  that are fully open source.
- Once listed, the website's download button can be the Store badge. Visitors get a trusted
  install with no warning. This only works while the Store app is **free** — so sell the
  licence ourselves.
- **Selling the licence outside the Store is allowed.** Microsoft takes nothing. We tick the
  "third-party purchase" box in Partner Center and put the price in the Store description.
- winget users get the Store build too, through winget's built-in Store source.

Sources: [signing options](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options),
[Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies),
[Store web installer](https://learn.microsoft.com/en-us/windows/apps/distribute-through-store/how-to-use-store-web-installer-for-distribution).

### Two changes to the app first — done in 1.0.2

A Store app is "packaged". Windows quietly redirects anything a packaged app creates in
AppData into a private folder, and **deletes that folder when the app is uninstalled**. That is
exactly when the spare copy is needed. We already saw this redirection for real, on this PC.

1. **Move PassVault's PC files out of AppData.** `machine.pvm`, `device.bin` and the spare
   vault move from `%LOCALAPPDATA%\PassVault` to `%USERPROFILE%\PassVault`. Nothing outside
   AppData is redirected. Not Documents: Windows 11 backs that up to OneDrive by default. The
   app moves the old folder across once, for anyone who already has it.
2. **Keep the TPM key with our own files.** Windows stores our TPM key as a file in
   `AppData\Local\Microsoft\Crypto`, so it can be redirected and deleted too. It was on this
   PC; on a PC where that folder already existed it might not be, so it would vary from PC to
   PC. Where it happens, `device.bin` could never be opened again after a reinstall. The
   fix — tested on this PC's TPM by the research —
   is to keep the chip-wrapped key in our own file. It is about 20 lines, needs no special
   permission, and still only works on this PC. Done: it lives in `machine.pvm`, next to the
   PIN slot, so the two can never be written out of step.

Microsoft does have a switch to turn the redirection off, but it is meant for games and the
Store refuses it for other apps. Do not plan around it.

Sources: [how packaged apps see AppData](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes),
[flexible virtualization](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization).

### The account: Individual or Company

**This cannot be changed after creating it**, so decide first.

- Microsoft says Individual accounts are for things *not* done "in relation to their trade or
  profession". Selling a licence is trade, so a **Company account is probably required**.
- A second reason: policy 10.8.3 says Individual accounts cannot require "financial
  information", and lists "passwords" and "recovery phrases". It is aimed at banking and crypto
  apps, but a reviewer could apply it to a password manager *(unconfirmed)*.
- **Before creating the account, email `reportapp@microsoft.com` and ask.**

What a Company account needs, as a UK sole trader:

| Need | Cost |
|---|---|
| Registration | Free (since May 2026) |
| A D-U-N-S number | Free option exists |
| An email on your own domain — Gmail is not accepted | A domain, about £10 a year. **The only unavoidable cost in this plan.** |
| Phone, address and email shown publicly (EU rules) | A P.O. box or voicemail number is accepted |

Always start at **storedeveloper.microsoft.com**. Other routes still show the old paid sign-up.

### What could get the Store submission refused

- **Testers without a USB stick or TPM.** The rules say the app must be testable. The first
  screen already says clearly when no stick is plugged in, and without a TPM we fall back to
  Windows' own protection. The certification notes must walk testers through it step by step.
- **Black screenshots.** Our screen protection blacks out testers' screenshots. Say so in the
  certification notes, and take the Store listing screenshots with
  `--allow-screen-capture`.
- **The second Start-menu tile** ("screen sharing allowed"). Microsoft's docs say packages
  with more than one app fail certification; Python's Store package has several anyway. Have a
  fallback ready — a right-click Jump List entry, or a switch on the unlock screen.
- **"Uses all system resources".** The Store shows this for every full desktop app. One line
  in the description should explain it, so it does not put off security-minded buyers.
- A **privacy policy page** is required, even though PassVault collects nothing.
- An **age rating** questionnaire, about 10 minutes, free.

Certification takes up to three working days. Expect about a week the first time.

### The steps, in order

1. The two code changes above, with tests.
2. A script that builds the Store package. The tools are already on this PC; no Visual Studio
   needed.
3. **Local test.** Needs Developer Mode switched on once (Settings → System → For
   developers). Test installing, uninstalling and reinstalling on a spare Windows account, never
   against the real vault.
4. The website and privacy page (see Part 2).
5. The account, then reserve the app name — *after* deciding about the name (below).
6. Listing text, screenshots, certification notes. Submit.

---

## Part 2 — Getting it noticed with no money

### The PIN — fixed in 1.0.2

It was: the chip unlocked this PC's secret without asking for the PIN, and the "10 wrong
tries" limit lived in the app. Someone with **both** the PC and the stick could skip the app and
try all 10,000 four-digit PINs in a few hours.

Now **the chip itself checks the PIN.** It counts wrong ones on its own — on this PC (and
Windows' usual settings), 32, then one more every two hours. So trying every 4-digit PIN takes
**over two years**, and 6 digits effectively forever. Checked on this PC's real chip: a wrong
PIN is refused by the chip and its counter goes up. PINs can now be 4 to 12 digits.

What is left, and must be said plainly in the threat model: someone with **admin rights** on
that PC can reset the chip's count, and then only the PIN's length protects you. That is the
reason for longer PINs.

### Who it is for

**People who do not want their passwords in anyone's cloud — Google's, Apple's, Microsoft's or
a password company's — and would never set up KeePass.**

Not for, yet: people who need their phone and browser autofill today. Say that plainly rather
than let them find out.

The pitch, in one line:

> Your passwords open only with three things together: your USB stick, your PC's security
> chip, and your PIN. No cloud, no account, no subscription — and you own it.

Two things to lead with:

- **LastPass**, fined £1.2 million by the ICO in December 2025. Its stolen vault backups were
  cracked offline. A copied PassVault vault file cannot be cracked that way: the file on the
  stick only holds the 12-word way in, which is far beyond guessing. The PIN's way in lives on
  the PC. (True today. Check it again when the iPhone app adds its own way in.)
  ([ICO](https://ico.org.uk/about-the-ico/media-centre/news-and-blogs/2025/12/password-manager-provider-fined/))
- **Subscriptions keep going up.** In 2026 Bitwarden Premium doubled, 1Password rose a third,
  and Dashlane ended its free plan. PassVault has no servers to pay for, so it never needs a
  subscription.

Security people will ask "why not KeePassXC with a key file?". The honest answer: KeePass can
do similar things, but you have to know how to set them up. PassVault does it with no setup,
keeps a spare copy automatically, and has a 12-word way back in. Do not claim it is the only
tool that can do this.

### Price

| Product | Price |
|---|---|
| Bitwarden Premium | about $20 a year (free plan too) |
| Proton Pass Plus | about $24 a year; lifetime $199 |
| RoboForm | about $30 a year |
| Keeper | $43 a year |
| 1Password | $48 a year, no free plan |
| Dashlane | $60 a year, free plan ended |
| KeePassXC | free |
| Lifetime licences elsewhere | $80–125 (Strongbox, KeePassium, Sticky Password) |

**Suggested: £19–25 at launch**, pitched as "less than one year of 1Password — once". Raise to
£29–39 after an outside security review, or once it has a reputation. Not below £10: that
reads as low value. Stay away from "lifetime deal" resale sites: they anchor the price low
(Enpass's lifetime licence is resold there for about $25).

Only about 1 in 5 password-manager users pays for one, and the free version is complete, so
do not plan on large numbers. The price band is a judgement: nobody publishes how many people
buy a one-time licence for a new, unaudited app.

**Always describe the licence as buying the paid features — never as a donation or "supporting
the developer".** Paddle's rules ban donations, and the Store's purchase rules point the same
way. Donations, if you want them, go through Ko-fi or GitHub Sponsors instead.

### Trust is the real barrier

People who avoid password managers mostly do not trust them (23%). Price comes much lower (9%).
([Security.org](https://www.security.org/digital-safety/password-manager-annual-report/))
Free things that build trust:

- **Publish a plain threat model** (the PIN is fixed) — what it protects against and what it
  does not, including malware on an unlocked PC. DESIGN.md is most of it already.
- **Say "not audited yet"** plainly, as LICENCE.md already does.
- **Open source** — the biggest single lever. See the decisions below.
- **A free bug bounty:** GitHub's private "Report a vulnerability" button, a `security.txt`
  file, and a thank-you "hall of fame" page instead of money.
- **Reproducible builds**, so anyone can check the download matches the source. .NET supports
  this.
- **A free security review:** NLnet's Restack fund gives grants that include audits. The
  **deadline is 3 November 2026**, and results must be open source. Ask NLnet first whether a
  desktop app fits. ([NLnet Restack](https://nlnet.nl/restack/))
- **Write the website and launch posts in your own words.** On Hacker News, AI-written copy
  gets mocked, and it was held against another password manager in 2026.

### Where to launch, and each place's rules

| Where | Allowed? | How |
|---|---|---|
| Hacker News — Show HN | Yes | Post it yourself, stay and answer for hours, never ask for upvotes. It is a lottery: most password-manager posts get 2 points. The two that did well in 2026 were open source. |
| r/software | Wednesdays only, with flair | Unless open source. Post something useful, like how the USB + chip design works. The free version meets their "fully usable without paying" rule. |
| r/Windows, r/Windows11 | Ask the mods first | By modmail. Link the free download; no selling. |
| r/PasswordManagers | Unknown | Read the rules by hand and message the mods first. Probably the best-targeted. |
| **r/privacy** | **No** | Bans self-promotion and closed-source apps, and has paused app posts. Posting risks a ban. |
| r/selfhosted, r/opensource, r/cybersecurity | No | Wrong fit. |
| Privacy Guides forum | Feedback only | Project Showcase, after verifying. They only list cross-platform managers, so it cannot be recommended there yet. |
| AlternativeTo | Yes | **Submit now:** the free queue takes months. There is an optional $5 to skip it. List it as an alternative to KeePass, KeePassXC, Bitwarden and 1Password. |
| Softpedia, MajorGeeks, Neowin | Yes | Launch week. A Softpedia "100% clean" badge helps with suspicion of an unsigned installer. |
| winget | Yes | Free, and works with the unsigned installer — but Smart App Control still blocks it. |
| Product Hunt | Yes | Secondary. Without followers it will probably not be featured. |
| Small YouTube channels | Yes | The ones already comparing offline managers. The spare copy answers their "what if the file corrupts" worry. |
| Mastodon, Lemmy | Carefully | A personal account posting honestly about building it. Not an advert account. |

Also: about a fifth of KeePass downloads come from Germany. **A German translation is worth
doing early.**

### Taking payment, from the UK

- **Always sell through a "merchant of record".** They are legally the seller, and handle UK
  and EU VAT. Selling direct — your own Stripe, or a Ko-fi shop — makes you responsible for
  EU VAT from the very first sale.
- **Paddle** is the best fit: UK-based, 5% + 50 cents a sale, takes sole traders, handles all
  the tax. **Stripe Managed Payments** is slightly cheaper for UK cards, but has an
  eligibility review and shows "LINK.COM" on buyers' statements.
- On a £20 sale you keep about **£15**.
- **Licences are emailed by hand.** PassVault's licence is signed and checked offline, and no
  payment service can make one without a server. So: the sale email arrives, run LicenceMaker,
  email the licence back. That is fine at small numbers.
- **Donations:** Ko-fi — switch off its default 5% fee in settings. GitHub Sponsors takes
  nothing, but needs open source.

UK basics ([GOV.UK](https://www.gov.uk/guidance/tax-free-allowances-on-property-and-trading-income)):

- Nothing to register until sales pass **£1,000 in a tax year** (6 April to 5 April). Then
  register for Self Assessment. Keep a simple log of sales from the first one anyway.
- VAT registration only above **£90,000** a year.
- The ICO data protection fee: probably exempt if buyer details are only used for the sale.
  Run the ICO's online checker.
- **The website must show your name, a geographic address and an email.** For a sole trader,
  that probably means your home address. Decide how you feel about that before launch.

### The name

"PassVault" is already used by other password managers: an offline Android app on F-Droid
that also blocks screenshots, a paid cloud service at passvault.me, and an iPhone app. With no
advertising, you rely on people finding you by name, and a shared name weakens that. A bad
review of another "PassVault" could land on you. **Decide before reserving the Store name or
posting anywhere**, and check the UK trademark register (UKIPO).

---

## Decisions for you

| | Decision | My recommendation |
|---|---|---|
| 1 | **Open source now?** | **Yes**, under GPL, like KeePassium: sell the licence for convenience and support. It opens free signing (SignPath), the NLnet audit grant, GitHub Sponsors, and most of the channels above. "Source published if abandoned" does not count for any of them. |
| 2 | **Rename?** | **Yes**, before the Store name and any post. |
| 3 | **Company Store account?** | **Yes**, after asking Microsoft. It costs one domain. |
| 4 | **Price** | £19–25 at launch. |
| 5 | **Your address on the website** | Your call. It is the law's requirement, not Microsoft's. |

## Order of work

1. ~~**Fix the PIN**~~ — done in 1.0.2.
2. ~~**The two Store changes**~~ — done in 1.0.2.
3. **Build and test the Store package** — needs Developer Mode on, once.
4. **Your decisions** — name, open source, account, price, address.
5. **Website, privacy page, threat model.** AlternativeTo submission early, because of its queue.
6. **Store submission.** winget alongside.
7. **Launch posts**, only once the threat model and website are ready.
