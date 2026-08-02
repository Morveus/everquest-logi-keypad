namespace Loupedeck.EverQuestPlugin
{
    using System;

    // Sends ALT + <digit-row key> through the SDK's keyboard API — the same path the
    // built-in "keyboard shortcut" action uses. VirtualKeyCode.Key1..Key9 are the
    // physical top-row keys, so on AZERTY this produces ALT+&, ALT+é, ALT+" ...
    internal static class KeyboardHelper
    {
        public static void SendAltDigit(Plugin plugin, Int32 digit)
        {
            if (digit < 1 || digit > 9 || plugin?.ClientApplication == null)
            {
                return;
            }

            var key = (VirtualKeyCode)((Int32)VirtualKeyCode.Key1 + digit - 1);
            plugin.ClientApplication.SendKeyboardShortcut(key, ModifierKey.Alt);
        }
    }
}
