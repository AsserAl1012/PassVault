# How PassVault looks

Dark graphite, hairline edges, and one brass colour that appears **only** where something is
live or where something commits. Nothing else on a Windows desktop looks quite like it, and
almost all of it lives in one file — `src/PassVault.App/App.xaml`.

## The rules

**One accent, one meaning.** Brass `#D8A249` means *this is live* or *this commits*. It is not
decoration and it is never a warning. Danger is red, success is green, everything else is grey.

**Ink on brass is `#0E0F12`, never white.** White on brass is 2.29:1 — unreadable. Near-black on
brass is 8.38:1.

**Nothing overshoots.** No BackEase, no ElasticEase, no BounceEase, anywhere. Travel never
exceeds 10px. Text is never scaled — WPF scaled text goes blurry.

**Colours are declared as `<Color>` first, brushes derived from them.** A brush handed out by
StaticResource is shared and frozen; animating it throws *"Cannot animate on an immutable object
instance"*. Templates that animate a colour mint their own brush from the Color.

**Animate Opacity, a brush's Color, and RenderTransform. Never Width, Height, Margin or
BorderThickness** — those force a full measure-and-arrange pass every frame. Where a border
appears to thicken on focus, it is a second always-present 2px Border fading its Opacity.

**Disabled uses explicit colours, never Opacity.** Fading a whole Border drags its label towards
invisible and reads as a rendering bug rather than a state.

## Colours

| Token | Hex | Used for |
|---|---|---|
| Void | `#0E0F12` | window background; ink on brass |
| Surface | `#15171B` | panels, list, detail pane |
| Raised | `#1C1F24` | inputs, buttons, top bar |
| Active | `#23272E` | hover, selected row |
| Press | `#121419` | a button being pressed |
| Hairline | `#2A2E35` | every resting 1px border |
| Bevel | `#3E444D` | 1px top-edge highlight |
| Accent | `#D8A249` | live, or commits |
| Danger | `#EC6A60` | errors |
| Success | `#4FB286` | stick connected, saved |

Text contrast, worst case (on Active `#23272E`): primary `#E8EAED` 12.4:1, secondary `#9AA1AB`
5.8:1, accent 6.6:1, danger 4.9:1, success 5.7:1 — all pass AA. Disabled `#5D646E` is 2.5:1 and
deliberately fails; WCAG exempts inactive controls, and anything darker stops reading as "off".

## Type

- UI — `Segoe UI Variable Text, Segoe UI`, 11–16px
- Display — `Segoe UI Variable Display, Segoe UI`, 18px and up
- Mono — `Cascadia Mono, Consolas` for passwords, recovery words, PIN

**Cascadia Mono, not Cascadia Code.** Code has programming ligatures, and a ligature in a
password field fuses two characters into one glyph that is not what was typed.

Every window root carries `TextFormattingMode="Display"`, `TextRenderingMode="ClearType"`,
`UseLayoutRounding` and `SnapsToDevicePixels`. WPF defaults to `Ideal`, which is right at 20px+
but leaves 11–13px text visibly soft on a dark background — and softness is most of the
difference between "developer UI" and "product".

> **Icon glyphs are the one exception.** `Display` mode breaks symbol fonts — MDL2 glyphs come
> out as empty boxes. Icons go in a `TextBlock` using the `Glyph` style, which sets
> `TextFormattingMode="Ideal"`. Never put a glyph in a Button's string `Content`.

## Motion

Seven named entries. Anything not on this list does not animate.

| Name | Duration | Easing | Used for |
|---|---|---|---|
| Tick | 90ms | CubicEase Out | press, glyph swap, a pin seating |
| Shift | 160ms | CubicEase Out | hover, focus ring, selection bar |
| Travel | 240ms | CubicEase InOut | panel change, band state change |
| Settle | 320ms | CubicEase Out | window open |
| Throw | 220ms | CubicEase In | the shear line, once per unlock |
| Breathe | 1100ms | QuadraticEase InOut, forever | the one working pin, nothing else |
| Drain | linear | — | clocks. Easing a timer lies about elapsed time. |

## Done so far

- [x] The token system, and control templates for Button, TextBox and PasswordBox — radius 3,
      a 1px top bevel so raised controls catch light from above, real hover colour animation,
      a pressed state that puts the light out and moves 1px down
- [x] Type and icon pass
- [x] Selection is grey plus a brass bar, not a blue wash
- [x] Brass taken off "Add credential" — adding is not committing
- [x] **The state band** — one control on the unlock and vault windows. Carries the glyph, a
      state line, a detail line and a trailing slot, and is the only thing in the product that
      changes background colour. It replaced both the unlock pill and the grey bottom line.
- [x] **The auto-lock countdown.** The vault used to lock after 5 minutes with no warning at
      all, and the idle timer only ticked every 20 seconds. It now ticks every second and
      reads *"locks in 4:12"*, going brass in the last minute.
- [x] **The unlock second.** A four-pin lock. Each pin seats on something that genuinely
      happened; during Argon2 one pin breathes ±2px. Using the 12 words leaves the second pin
      up on purpose — your phrase does not use this PC's chip, so it would be a lie to seat it.
- [x] **Delete confirms in the footer**, cross-fading in place, backing out after 8 seconds of
      no answer. Replaces a light-grey Win32 MessageBox in the middle of a dark product.
- [x] **Message lifetimes.** A success clears itself after 4 seconds; anything you must act on
      stays until you act; no timer ever clears an error.

## Still to do

1. **Pin the unlock body to a fixed height.** The busy panel is now top-aligned like the rest,
   so it no longer leaps down the window — but the panels are still different heights, so the
   content below shifts between steps. Measure the tallest at load rather than hard-coding a
   number, which would clip the 12-word grid at 150% text scaling.
2. **The recovery window** has not had the band or the new footer treatment yet.
3. **Window open.** Root grid fading in over Settle with a 10px lift, started on
   `ContentRendered` rather than `Loaded` so the first frame is already composed. Never animate
   `Window.Opacity` — Win32 promotes it to a layered window, which flickers and degrades text.
