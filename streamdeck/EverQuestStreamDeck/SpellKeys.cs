namespace EverQuestStreamDeck
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using EqSpells.Core;

    // Tracks which keys are on screen and what each one is showing.
    //
    // The Stream Deck model is the opposite of Logitech's: instead of the plugin
    // declaring nine fixed actions, the player drags ONE action onto as many keys as
    // they like and picks a gem for each. So the mapping is discovered at runtime -
    // willAppear announces a key, willDisappear takes it away, and the same gem may be
    // on several keys at once (or on none).
    internal sealed class SpellKeys
    {
        private sealed class KeyState
        {
            public Int32 Gem;
            public String Binding = KeyBinding.Auto;
            public String LastImageKey;   // what we last pushed, to avoid pointless traffic
        }

        private readonly ConcurrentDictionary<String, KeyState> _keys = new ConcurrentDictionary<String, KeyState>();
        private readonly StreamDeckClient _client;
        private readonly SpellBarReader _reader;
        private readonly IPluginLog _log;

        public SpellKeys(StreamDeckClient client, SpellBarReader reader, IPluginLog log)
        {
            this._client = client;
            this._reader = reader;
            this._log = log;
        }

        public Boolean Any => !this._keys.IsEmpty;

        public void Remember(String context, JsonElement payload)
        {
            if (context == null) { return; }
            var state = this._keys.GetOrAdd(context, _ => new KeyState { Gem = 1 });
            var settings = GetSettings(payload);
            state.Gem = ReadInt(settings, "gem", state.Gem);
            state.Binding = ReadString(settings, "binding", state.Binding);
            // Force the next push: the gem may have changed under the same context.
            state.LastImageKey = null;
        }

        public void Forget(String context)
        {
            if (context != null) { this._keys.TryRemove(context, out _); }
        }

        public async Task PressAsync(String context, CancellationToken token)
        {
            if (context == null || !this._keys.TryGetValue(context, out var state)) { return; }
            if (!KeyBinding.TryResolve(state.Binding, state.Gem, out var scan))
            {
                this._log?.Info($"Gem {state.Gem} has no binding; nothing sent");
                await this._client.LogAsync($"gem {state.Gem}: no binding configured", token).ConfigureAwait(false);
                return;
            }
            if (!Win32Keyboard.SendAltScan(scan))
            {
                this._log?.Warning($"SendInput rejected the keystroke for gem {state.Gem}");
            }
        }

        // Push the current art to every key. Called after a read changes something, and
        // on a slow heartbeat as a safety net - the same deliberately dumb repaint the
        // Logitech host needed, for the same reason: it costs nothing and it guarantees
        // the keys eventually show what the plugin already knows.
        public async Task RepaintAsync(Boolean force, CancellationToken token)
        {
            foreach (var pair in this._keys)
            {
                if (token.IsCancellationRequested) { return; }
                var state = pair.Value;
                var gem = state.Gem;

                Byte[] png = null;
                String key;
                if (gem < 1 || gem > SpellBarReader.MaxGemCount)
                {
                    key = "invalid";
                }
                else if (this._reader.IsGemEmpty(gem))
                {
                    // An empty slot gets a blank key, distinct from a gem we simply have
                    // not identified yet - that one keeps the action's own artwork.
                    key = "empty";
                }
                else
                {
                    png = this._reader.GetIconPng(gem);
                    key = png == null ? "unknown" : gem.ToString(CultureInfo.InvariantCulture) + ":" + png.Length;
                }

                if (!force && key == state.LastImageKey) { continue; }
                // Record the push only if it actually went out: remembering a frame the
                // socket dropped would freeze this key on stale art until the next forced
                // repaint, and "the keys only fix themselves eventually" is exactly the
                // class of bug the Logitech host taught us to design out.
                if (await this._client.SetImageAsync(pair.Key, png, token).ConfigureAwait(false))
                {
                    state.LastImageKey = key;
                }
            }
        }

        private static JsonElement GetSettings(JsonElement payload) =>
            payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("settings", out var s)
                ? s
                : default;

        // The Property Inspector posts everything as strings, so a slot arrives as "3".
        private static Int32 ReadInt(JsonElement o, String name, Int32 fallback)
        {
            if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) { return fallback; }
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) { return n; }
            if (v.ValueKind == JsonValueKind.String &&
                Int32.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            {
                return p;
            }
            return fallback;
        }

        private static String ReadString(JsonElement o, String name, String fallback) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : fallback;
    }
}
