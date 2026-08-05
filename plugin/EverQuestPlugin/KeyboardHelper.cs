namespace Loupedeck.EverQuestPlugin
{
    using System;

    // Sends ALT + <digit-row key> through the SDK's keyboard API — the same path the
    // built-in "keyboard shortcut" action uses. VirtualKeyCode.Key1..Key9 are the
    // physical top-row keys, so on AZERTY this produces ALT+&, ALT+é, ALT+" ...
    internal static class KeyboardHelper
    {
        // Gem 10 is ALT+0, matching EverQuest's own default bindings: the number row runs
        // out there. Gems 11 to 14 exist only with alternate-advancement unlocks and have
        // no default binding in the game either, so there is nothing to guess - those keys
        // show their icon and send nothing until a binding is known.
        public static void SendAltDigit(Plugin plugin, Int32 gem)
        {
            if (gem < 1 || gem > 10 || plugin?.ClientApplication == null)
            {
                return;
            }

            var key = gem == 10
                ? VirtualKeyCode.Key0
                : (VirtualKeyCode)((Int32)VirtualKeyCode.Key1 + gem - 1);
            plugin.ClientApplication.SendKeyboardShortcut(key, ModifierKey.Alt);
        }
    }
}
