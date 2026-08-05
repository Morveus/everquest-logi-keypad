namespace EverQuestStreamDeck
{
    using System;
    using System.Collections.Generic;

    // Which keystroke a key sends when pressed.
    //
    // "auto" covers the case that needs no thought: gems 1 to 10 are ALT + the matching
    // number-row key, which is what EverQuest binds them to out of the box. Gems 11 to 14
    // only exist with alternate-advancement unlocks and the game ships NO default binding
    // for them, so there is nothing sensible to guess - the player binds them in-game and
    // tells the key here, rather than the plugin inventing a shortcut that casts nothing.
    internal static class KeyBinding
    {
        public const String Auto = "auto";
        public const String None = "none";

        // Set 1 scan codes. Scan codes rather than virtual keys because EverQuest reads
        // the physical key: ALT+& on AZERTY and ALT+1 on QWERTY are the same gem.
        private static readonly Dictionary<String, UInt16> Named = new Dictionary<String, UInt16>(StringComparer.OrdinalIgnoreCase)
        {
            ["alt+1"] = 0x02, ["alt+2"] = 0x03, ["alt+3"] = 0x04, ["alt+4"] = 0x05, ["alt+5"] = 0x06,
            ["alt+6"] = 0x07, ["alt+7"] = 0x08, ["alt+8"] = 0x09, ["alt+9"] = 0x0A, ["alt+0"] = 0x0B,
            ["alt+f1"] = 0x3B, ["alt+f2"] = 0x3C, ["alt+f3"] = 0x3D, ["alt+f4"] = 0x3E,
            ["alt+f5"] = 0x3F, ["alt+f6"] = 0x40, ["alt+f7"] = 0x41, ["alt+f8"] = 0x42,
            ["alt+f9"] = 0x43, ["alt+f10"] = 0x44, ["alt+f11"] = 0x57, ["alt+f12"] = 0x58,
        };

        // Returns false when the key should send nothing at all.
        public static Boolean TryResolve(String binding, Int32 gem, out UInt16 scan)
        {
            scan = 0;
            if (String.IsNullOrWhiteSpace(binding) || binding == Auto)
            {
                // Gems past the number row have no automatic answer.
                if (gem < 1 || gem > 10) { return false; }
                return Named.TryGetValue(gem == 10 ? "alt+0" : $"alt+{gem}", out scan);
            }
            if (binding == None) { return false; }
            return Named.TryGetValue(binding, out scan);
        }

        // What the Property Inspector offers, in the order it offers it.
        public static IEnumerable<String> All
        {
            get
            {
                yield return Auto;
                yield return None;
                foreach (var k in Named.Keys) { yield return k; }
            }
        }
    }
}
