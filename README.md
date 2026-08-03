# EverQuest Spells — Logi MX Creative Console plugin

(disclaimer : this is 100% vibe coded)

Your nine spell gems, live on your keypad.

This plugin watches the EverQuest window, recognises which spells you have memorised,
and paints the matching icons on the first nine keys of a Logitech MX Creative Console.
Pressing a key casts the spell. Memorise a different spell and the key follows within
five seconds — no configuration, no re-mapping, nothing to maintain.

It is a single DLL. It does not modify the game, inject anything into it, or read its
memory: it takes a passive screenshot of the window and compares what it sees against
the game's own icon files.

---

## Why

Mapping nine spell icons by hand is tedious, and you have to redo it every time you
change your spell set. This automates exactly that, and nothing else.

## Requirements

- Windows
- [Logi Options+](https://www.logitech.com/software/logi-options-plus.html) with Logi Plugin Service
- A Logitech MX Creative Console (or a Loupedeck CT / Live)
- EverQuest, windowed or borderless (not exclusive fullscreen)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) — to build

## Install

```bash
git clone https://github.com/Morveus/everquest-logi-keypad.git
cd everquest-logi-keypad
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release
```

The build registers the plugin with Logi Plugin Service and reloads it — no restart of
Options+ needed. If Options+ is installed somewhere unusual, point the build at it:

```bash
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release -p:PluginApiDir="D:\Logi\LogiPluginService\"
```

> The build registers the *source folder* you built from. Keep the clone where it is, or
> rebuild after moving it.

## Assign the keys

In Options+, select your MX Creative Console. In the actions panel on the right, click
**ALL ACTIONS** at the top — it is filtered to *System Actions* by default and the
plugin will not show up until you do. Then find the **Sorts EverQuest** group and drag:

| Action | What it does |
|---|---|
| **Sort 1 … Sort 9** | Shows the spell icon, sends ALT + the matching number-row key |
| **Mettre à jour les icônes** | Forces a full re-read; also the status light (red = stuck) |
| **Mise à jour auto** | Turns the background refresh on/off (on by default) |

The keystroke is sent by *physical key position*, so it is ALT+1…ALT+9 on QWERTY and
ALT+&, ALT+é, ALT+"… on AZERTY — whichever your EverQuest binds expect. Change your in-game
gem binds to match, or fork and edit `KeyboardHelper.cs` if you use a different modifier.

The first run has to find the spell bar from scratch, which takes about a minute. After
that the calibration is remembered and each check costs a few tens of milliseconds.

---

## How it recognises the icons

No machine learning, no OCR. The problem is *closed*: every possible answer already
exists on disk, in the game's own icon sheets. So the question is never "what is this
image?" but "which of these 2 262 known images is it?".

**1. Find the game.** Via the running `eqgame` process, else the uninstall registry
entries, else the usual install locations on every fixed drive. No hardcoded path.

**2. Read the character's UI file.** `UI_<character>_<server>.ini` gives the active skin
and the spell bar's horizontal position.

**3. Capture the window** with `PrintWindow(PW_RENDERFULLCONTENT)` — works on the DirectX
surface, does not need focus, and never touches the game.

**4. Pick the icon pack.** EverQuest ships three distinct icon sets under
`Textures\Alternate 1..3` (the `uifiles` folders are byte-identical copies of two of
them). Each is scored against the capture and the best one wins.

**5. Compare.** Each icon — from the game files and from the screen — is resampled to
24×24 RGB (1 728 values), then mean-centred and normalised. The score is their dot
product: **normalized cross-correlation**. That normalisation is what makes it work:
EverQuest's UI is semi-transparent, so on-screen icons are darkened and washed out, and
NCC is blind to any uniform change in brightness or contrast. It compares *structure*.
A correct match scores 0.96–0.99; a wrong one, 0.2–0.6.

**6. Locate the bar by its geometry, not its content.** The gems form a run of identical
cells at a fixed vertical pitch, so comparing every row to the row one pitch below finds
the bar over the whole window height in about 8 ms.

## Cost

| | |
|---|---|
| Idle check (nothing changed) | **~0.1 s**, about 2 % of one core at a 5-second interval |
| Full bar location | ~55 s, only on first run or if you move the bar |

An idle check does not re-answer the full question. It compares each gem against the
descriptor of the icon *already shown* — nine dot products. Only a gem that no longer
matches is re-identified against the library.

Two things it deliberately refuses to do:

- **A gem on cooldown never changes the display.** Its score drops exactly like a real
  spell change would, but it then resembles no icon strongly enough to pass the
  replacement threshold, so the icon stays put.
- **An unreadable gem is ignored**, not treated as a change — an empty slot or a spell
  being scribed is a flat patch, and a flat patch carries no information.

## Where it stores things

Only in its own folder,
`%LOCALAPPDATA%\Logi\LogiPluginService\PluginData\EverQuest`: the calibration
(`barstate.txt`), the auto-refresh preference, and a copy of the nine icons for
inspection. Deleting that folder is safe — everything is recomputed.

## Known limits

Honest list, in rough order of how likely you are to hit them:

- **Display scaling.** The gem pitch is searched in a 38–47 px window, measured at 100 %
  Windows scaling. Other DPI settings or EverQuest UI scales will fall outside it and
  the bar will not be found. Widening the range was tried and makes it worse — it locks
  onto a harmonic and reads the bar several gems off. This needs a proper fix.
- **Exactly nine gems, stacked vertically.** A spell window arranged horizontally or in
  two columns is not supported.
- **Custom skins.** The 40 px cell size and the gem-socket grey are assumed. A third-party
  skin with different metrics will degrade or break recognition.
- **Nothing proves it found the *spell* bar.** A hotbar of nine spell icons at a similar
  pitch would pass the same test.
- **Exclusive fullscreen** cannot be captured. Use windowed or borderless.
- **The UI is in French** — action names, key labels and log messages.

## Build notes

Non-obvious things, learned the hard way, in case you fork this:

- Target **`net10.0`**. The service's `PluginApi.dll` is .NET 10; the official DemoPlugin's
  `net8.0` no longer compiles against it.
- The assembly must contain **exactly one `Plugin` class and exactly one
  `ClientApplication` class**, even for a plugin with no associated application. Without
  the latter the service refuses to load it, with only "Cannot load plugin" in the log.
- **Do not copy the service's assemblies** next to the plugin (`<Private>false</Private>`).
  A duplicate `PluginApi.dll` makes the loader reject the plugin.
- To fill a key edge to edge, `DefaultIconTemplate.ict` needs an image area of
  `0,0,100,100` and its text item set to `isVisible: false` — the sample template
  reserves 30 % of the height for a label.
- Keep **nothing static that belongs to a plugin instance**. The service loads the new
  instance *before* unloading the old one, so a static timer gets disposed by the
  outgoing instance and the plugin goes silently idle.
- Write numbers with `InvariantCulture`. String interpolation uses the machine locale,
  and `"22,25"` will not parse back.

## Licence

MIT — see [LICENSE](LICENSE).

This project ships no EverQuest assets. It reads the icons from your own installation at
runtime. EverQuest is a trademark of Daybreak Game Company LLC; this is an unofficial,
unaffiliated fan project.
