# EverQuest Spells — plugin internals

C# plugin for Logi Plugin Service / MX Creative Console. **Self-contained**: a single
DLL that captures the game window, recognises the icons, draws the keys and sends the
keystrokes. No external script, no spawned process.

## Actions

- **Spell 1 … Spell 9** (group *EverQuest Spells*): each key shows the icon read from the
  game and sends **ALT + the physical number-row key**. Sent by key *position*, so it is
  ALT+1…ALT+9 on QWERTY and ALT+&, ALT+é, ALT+"… on AZERTY.
- **Refresh icons**: forces a full read, including relocating the bar. Doubles as the
  status light — it turns red when a read fails, so a wedged plugin is visible without
  opening a log.
- **Auto refresh**: toggles the background refresh. On by default, every 5 s
  (`IconUpdater.DefaultIntervalSeconds`). The setting survives a restart.

## Assigning the keys

In Options+ → MX Creative Console, click **ALL ACTIONS** at the top of the right-hand
panel: it is filtered to *System Actions* by default and the plugin will not appear until
you do. The actions are in the **EverQuest Spells** group.

The plugin is universal (no associated application), so its actions are available in any
application profile. If the group does not appear after a reload, close Options+
completely and reopen it — the service keeps the plugin loaded.

## Building

```bash
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release
```

The build writes an `EverQuestPlugin.link` file into the service's plugin folder and
triggers `loupedeck:plugin/EverQuest/reload`: hot reload, no need to restart Options+.
Log: `%LOCALAPPDATA%\Logi\LogiPluginService\Logs\plugin_logs\EverQuest.log`

If Options+ lives somewhere unusual, pass the folder explicitly:

```bash
dotnet build plugin/EverQuestPlugin/EverQuestPlugin.csproj -c Release -p:PluginApiDir="D:\Logi\LogiPluginService\"
```

> **Careful**: that `.link` file holds an absolute path. Building a *copy* of the repo
> points the service at the copy. To undo, rebuild from the right folder or edit the file.

## Files

| File | Role |
|---|---|
| `EverQuestPlugin.cs` | `Plugin` class: data folder, reader and timer startup |
| `SpellBarReader.cs` | The core: capture, locate, recognise, state, key images |
| `EqGame.cs` | Install discovery, character UI settings, icon packs, window capture |
| `EqIconLib.cs` | TGA decoder, normalized cross-correlation, periodicity detection |
| `IconUpdater.cs` | Schedules reads (timer, overlap guard) |
| `SpellCommand.cs` | The nine spell keys |
| `UpdateIconsCommand.cs` / `AutoUpdateCommand.cs` | The two service keys |
| `KeyboardHelper.cs` | Sends ALT + number row through the SDK keyboard API |
| `EverQuestApplication.cs` | `ClientApplication` class required by the loader |

## Logi SDK gotchas, learned the hard way

- **Target `net10.0`.** The service's `PluginApi.dll` is .NET 10; `net8.0` (as in the
  official DemoPlugin) fails to compile against it with CS1705.
- **A `ClientApplication` class is mandatory**, even with no associated application. The
  loader wants *exactly one* `Plugin` class **and** *exactly one* `ClientApplication`;
  without it the service refuses the plugin with a bare "Cannot load plugin".
- **Never copy the service's assemblies** next to the plugin (`<Private>false</Private>`).
  `PluginApi`, `System.Drawing.Common` and its two internal dependencies
  (`System.Private.Windows.Core`, `System.Private.Windows.GdiPlus`) are provided by the
  service: reference them from its folder, never from NuGet.
- **Keystrokes: use the SDK API**, `ClientApplication.SendKeyboardShortcut(
  VirtualKeyCode.Key1..Key9, ModifierKey.Alt)`. `Key1..Key9` are the *physical* number-row
  keys. Safe here: the API only activates the associated application when
  `HasNoApplication` is false.
  *Trap avoided*: a hand-rolled `SendInput` failed silently — an `INPUT` struct declared
  with only the keyboard union is 32 bytes where Windows expects 40, so `cbSize` was
  invalid and the call was rejected without error.
- **Full-bleed key image** needs both: `DefaultIconTemplate.ict` with an image area of
  `0,0,100,100` and its text item at `isVisible: false` (the sample template reserves 30 %
  of the height for a label), and `GetCommandImage` drawing with
  `DrawImage(img, 0, 0, builder.Width, builder.Height)`.
- **Never hand out a shared `BitmapImage` you later dispose.** The host may be drawing it;
  the draw throws and Options+ silently falls back to showing the action's name as text.
  Keep the encoded bytes and build a fresh image per request.
- **Repaint after restoring state.** Rebuilding the key images at startup is not enough —
  if the first read then reports "nothing changed", nothing ever asks the keys to repaint
  and they keep the host's default rendering.
- **Keep nothing static that belongs to a plugin instance.** The service loads the new
  instance *before* unloading the old one: with a static timer, the outgoing instance
  disposed the incoming one's, leaving the plugin silently idle while the old one kept
  working.
- **Write numbers with `InvariantCulture`.** `$"{x}"` yields "22,25" on a French machine,
  which the invariant parser then rejects — the calibration was lost on every restart and
  the plugin re-ran a 55-second bar location every cycle.
- **Diagnosing a silent service failure**: the Logi binaries are obfuscated (strings
  replaced by `by.(id)`). They can be read by loading `LoupedeckService.dll` through
  reflection and calling its string decryptor.

## Recognition tuning

Constants at the top of `SpellBarReader.cs`, with what each one prevents:

| Constant | Purpose |
|---|---|
| `WatchScore` 0.85 | Below this a gem is re-identified |
| `ChangeScore` 0.90 | Confidence required to *replace* a displayed icon — this is what stops a gem on cooldown from flipping the art |
| `Hysteresis` 0.05 | How much better a challenger must be than the incumbent |
| `MinMargin` 0.02 | Best minus second-best; rejects ambiguous matches between spell ranks sharing art |
| `GridScore` 0.85 | Average needed to trust a freshly located grid |
| `GoodScore` 0.95 | Above this the geometry is locked and needs no refinement |
| `LocateRetrySeconds` 120 | Back-off after a failed location, so a hidden bar cannot burn a core |

Two approaches were tried and reverted, both measured — do not retry them without
reading this first. Widening the pitch search range makes the periodicity lock onto a
harmonic and the bar is read several gems too low. Picking "the topmost alignment that
still scores" reads it several gems too high, because the best match among 2 262 icons
stays high on plain background: it says which icon is closest, never whether an icon is
there at all.
