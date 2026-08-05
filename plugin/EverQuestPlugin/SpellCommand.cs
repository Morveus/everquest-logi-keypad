namespace Loupedeck.EverQuestPlugin
{
    using System;

    using EqSpells.Core;

    // One multi-instance command: parameters "1".."14" map to the spell gems. A given
    // character shows only as many as it has unlocked; the rest stay blank. Pressing a
    // key sends ALT + the matching digit-row key; the key displays the icon read live
    // from the game window.
    public class SpellCommand : PluginDynamicCommand
    {
        public SpellCommand() : base()
        {
            for (var i = 1; i <= SpellBarReader.MaxGemCount; i++)
            {
                // Layout-neutral label on purpose: the keystroke is sent by physical key
                // position, so this is ALT+& on AZERTY and ALT+1 on QWERTY. Naming the
                // AZERTY character here would read as a bug on any other layout.
                var binding = i <= 9 ? $" (ALT+{i})" : i == 10 ? " (ALT+0)" : " (no default binding)";
                this.AddParameter($"{i}", $"Spell {i}{binding}", "EverQuest Spells");
            }
        }

        protected override Boolean OnLoad()
        {
            EverQuestPlugin.IconsRefreshed += this.OnIconsChanged;
            return true;
        }

        protected override Boolean OnUnload()
        {
            EverQuestPlugin.IconsRefreshed -= this.OnIconsChanged;
            return true;
        }

        // This command has parameters ("1".."9"), so the service needs to be told which
        // variation changed. The parameterless overload is for actions without
        // parameters: calling it here left every key on its default rendering until the
        // user pressed it, which is what forced a redraw.
        private void OnIconsChanged(Object sender, EventArgs e)
        {
            for (var i = 1; i <= SpellBarReader.MaxGemCount; i++)
            {
                this.ActionImageChanged(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        protected override void RunCommand(String actionParameter)
        {
            if (Int32.TryParse(actionParameter, out var digit))
            {
                KeyboardHelper.SendAltDigit(this.Plugin, digit);
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            if (Int32.TryParse(actionParameter, out var gem))
            {
                // The core hands over PNG bytes, never a live image: decoding here means
                // this method owns what it disposes. Sharing one BitmapImage and disposing
                // it on the next update used to race with the SDK drawing the key, and the
                // failed draw showed the action name instead of the icon.
                var png = EverQuestPlugin.Reader?.GetIconPng(gem);
                if (png != null)
                {
                    try
                    {
                        using (var img = BitmapImage.FromArray(png))
                        using (var builder = new BitmapBuilder(imageSize))
                        {
                            builder.Clear(BitmapColor.Black);
                            // Edge to edge: the icon is the label.
                            builder.DrawImage(img, 0, 0, builder.Width, builder.Height);
                            return builder.ToImage();
                        }
                    }
                    catch (Exception)
                    {
                        // Fall through to the text key rather than failing the repaint.
                    }
                }
            }
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                // An empty gem slot gets a blank key; only a gem we have never managed to
                // read keeps a label, so "empty" and "not known yet" stay distinguishable.
                if (Int32.TryParse(actionParameter, out var g) &&
                    EverQuestPlugin.Reader?.IsGemEmpty(g) != true)
                {
                    builder.DrawText($"Spell {actionParameter}");
                }
                return builder.ToImage();
            }
        }
    }
}
