# EverQuest Spells — Stream Deck plugin

Same recognition as the Logitech plugin, same `core/`, different host. The keys show the
spells currently memorised in EverQuest, read live from the game window, and cast them.

## How this host differs from the Logitech one

The two SDKs are built on opposite assumptions, and almost all of the porting work is
here rather than in the recognition:

| | Logi Plugin Service | Stream Deck |
|---|---|---|
| Plugin is | a DLL loaded into the service | its own process |
| Talks via | inherited `Plugin` class | WebSocket, one JSON object per message |
| Keys are | nine actions the plugin declares | one action the player drops as often as they like |
| Which gem | fixed by the action's parameter | a per-key setting, chosen in the Property Inspector |
| Keystrokes | `ClientApplication.SendKeyboardShortcut` | no API at all — Win32 `SendInput` |
| Key image | `BitmapBuilder` → `BitmapImage` | `setImage` with a base64 data URI |

The consequence worth knowing: on Stream Deck the plugin does not know its keys ahead of
time. It learns them from `willAppear`, forgets them on `willDisappear`, and the same gem
may sit on several keys at once — or on none.

## Files

| File | Role |
|---|---|
| `Program.cs` | Entry point: command-line handshake, wiring, lifetime |
| `StreamDeckClient.cs` | The WebSocket protocol, hand-written (five events, three commands) |
| `SpellKeys.cs` | Which key shows which gem, and pushing the art |
| `KeyBinding.cs` | Binding name → scan code |
| `Win32Keyboard.cs` | `SendInput`, with the two traps documented in place |
| `FileLog.cs` | `IPluginLog` → a small self-truncating log |
| `com.morveus.everquest.sdPlugin/` | What actually gets installed: manifest, Property Inspector, built binaries |

Everything else — capture, locating the bar, recognising the icons, the polling policy —
is `core/`, shared byte-for-byte with the Logitech plugin.

## Building

```bash
dotnet build streamdeck/EverQuestStreamDeck/EverQuestStreamDeck.csproj -c Release
```

The output lands directly in `com.morveus.everquest.sdPlugin/bin/`, which is where
`manifest.json` points. Install by copying that `.sdPlugin` folder to:

```
%APPDATA%\Elgato\StreamDeck\Plugins\
```

then restarting the Stream Deck software.

## Choosing a gem

Drop **Spell Gem** on a key and pick the slot in the Property Inspector. Drop **Refresh
Icons** somewhere too: it relocates the bar from scratch and recounts the slots — the way
out of any stale state, and it answers with the built-in check/warning overlay.

Gems 1 to 12 are on EverQuest's own default shortcuts (ALT + the full twelve-key number
row) and need no configuration. Gems 13 and 14 ship with **no** default binding — bind
them in the game's Keys options and pick the same physical key in the panel (keys are
named by QWERTY position, with the AZERTY equivalent shown), or the key will show the
spell and cast nothing.

Keystrokes are sent by physical key position, so ALT+1 on QWERTY and ALT+& on AZERTY are
the same key and both work without configuration.

## Devices

Any Stream Deck with enough keys works; the plugin does not care how many there are.
A full bar is fourteen gems, so a 15-key MK.2 fits one exactly, an XL or Studio leaves
room to spare, and an 8-key Stream Deck + or Neo shows whichever eight you assign.
