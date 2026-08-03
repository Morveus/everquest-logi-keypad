namespace Loupedeck.EverQuestPlugin
{
    using System;

    // One multi-instance command: parameters "1".."9" map to the nine spell gems.
    // Pressing a key sends ALT + the matching digit-row key; the key displays the icon
    // read live from the game window.
    public class SpellCommand : PluginDynamicCommand
    {
        // Unshifted characters of the AZERTY digit row, for labels only.
        private static readonly String[] AzertyDigitChars = { "&", "é", "\"", "'", "(", "-", "è", "_", "ç" };

        public SpellCommand() : base()
        {
            for (var i = 1; i <= SpellBarReader.GemCount; i++)
            {
                this.AddParameter($"{i}", $"Sort {i} (ALT+{AzertyDigitChars[i - 1]})", "Sorts EverQuest");
            }
        }

        protected override Boolean OnLoad()
        {
            if (EverQuestPlugin.Reader != null)
            {
                EverQuestPlugin.Reader.IconsChanged += this.OnIconsChanged;
            }
            return true;
        }

        protected override Boolean OnUnload()
        {
            if (EverQuestPlugin.Reader != null)
            {
                EverQuestPlugin.Reader.IconsChanged -= this.OnIconsChanged;
            }
            return true;
        }

        private void OnIconsChanged(Object sender, EventArgs e) => this.ActionImageChanged();

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
                var img = EverQuestPlugin.Reader?.GetImage(gem);
                if (img != null)
                {
                    using (var builder = new BitmapBuilder(imageSize))
                    {
                        builder.Clear(BitmapColor.Black);
                        // Edge to edge: the icon is the label.
                        builder.DrawImage(img, 0, 0, builder.Width, builder.Height);
                        return builder.ToImage();
                    }
                }
            }
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                builder.DrawText($"Sort {actionParameter}");
                return builder.ToImage();
            }
        }
    }
}
