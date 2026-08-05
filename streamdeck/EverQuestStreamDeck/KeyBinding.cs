namespace EverQuestStreamDeck
{
    using System;
    using System.Collections.Generic;

    // Which keystroke a key sends when pressed.
    //
    // "auto" covers what EverQuest binds out of the box: gems 1 to 12 are ALT + the
    // physical number row, all twelve keys of it - on AZERTY that reads ALT+& through
    // ALT+=, on QWERTY ALT+1 through ALT+=. Verified against the game's own Keys
    // options. Gems 13 and 14 ship with no default binding at all, so the player binds
    // them in-game and picks the same key here from the full list below.
    internal static class KeyBinding
    {
        public const String Auto = "auto";
        public const String None = "none";
        // Gems EverQuest itself binds by default; auto works up to here.
        public const Int32 AutoMax = 12;

        // Set 1 scan codes, named by their QWERTY-position label. Scan codes rather than
        // virtual keys because EverQuest reads the physical key: the name "alt+p" means
        // the key AT the P position, whatever the layout prints on it.
        private static readonly Dictionary<String, UInt16> Named = new Dictionary<String, UInt16>(StringComparer.OrdinalIgnoreCase)
        {
            // Number row, left to right - including the two keys past the digits.
            ["alt+1"] = 0x02, ["alt+2"] = 0x03, ["alt+3"] = 0x04, ["alt+4"] = 0x05, ["alt+5"] = 0x06,
            ["alt+6"] = 0x07, ["alt+7"] = 0x08, ["alt+8"] = 0x09, ["alt+9"] = 0x0A, ["alt+0"] = 0x0B,
            ["alt+minus"] = 0x0C, ["alt+equals"] = 0x0D,
            // Letter rows.
            ["alt+q"] = 0x10, ["alt+w"] = 0x11, ["alt+e"] = 0x12, ["alt+r"] = 0x13, ["alt+t"] = 0x14,
            ["alt+y"] = 0x15, ["alt+u"] = 0x16, ["alt+i"] = 0x17, ["alt+o"] = 0x18, ["alt+p"] = 0x19,
            ["alt+["] = 0x1A, ["alt+]"] = 0x1B,
            ["alt+a"] = 0x1E, ["alt+s"] = 0x1F, ["alt+d"] = 0x20, ["alt+f"] = 0x21, ["alt+g"] = 0x22,
            ["alt+h"] = 0x23, ["alt+j"] = 0x24, ["alt+k"] = 0x25, ["alt+l"] = 0x26,
            ["alt+;"] = 0x27, ["alt+'"] = 0x28,
            ["alt+z"] = 0x2C, ["alt+x"] = 0x2D, ["alt+c"] = 0x2E, ["alt+v"] = 0x2F, ["alt+b"] = 0x30,
            ["alt+n"] = 0x31, ["alt+m"] = 0x32, ["alt+,"] = 0x33, ["alt+."] = 0x34, ["alt+/"] = 0x35,
            // Function row.
            ["alt+f1"] = 0x3B, ["alt+f2"] = 0x3C, ["alt+f3"] = 0x3D, ["alt+f4"] = 0x3E,
            ["alt+f5"] = 0x3F, ["alt+f6"] = 0x40, ["alt+f7"] = 0x41, ["alt+f8"] = 0x42,
            ["alt+f9"] = 0x43, ["alt+f10"] = 0x44, ["alt+f11"] = 0x57, ["alt+f12"] = 0x58,
        };

        // The number row in physical order, for resolving "auto" by gem index.
        private static readonly UInt16[] NumberRow =
        {
            0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D,
        };

        // Returns false when the key should send nothing at all.
        public static Boolean TryResolve(String binding, Int32 gem, out UInt16 scan)
        {
            scan = 0;
            if (String.IsNullOrWhiteSpace(binding) || binding == Auto)
            {
                if (gem < 1 || gem > AutoMax) { return false; }
                scan = NumberRow[gem - 1];
                return true;
            }
            if (binding == None) { return false; }
            return Named.TryGetValue(binding, out scan);
        }
    }
}
