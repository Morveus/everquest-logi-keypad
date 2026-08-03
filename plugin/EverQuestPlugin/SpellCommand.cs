namespace Loupedeck.EverQuestPlugin
{
    using System;

    // One multi-instance command: parameters "1".."9" map to the nine spell gems.
    // Pressing a key sends ALT + the matching digit-row key; the key displays the icon
    // read live from the game window.
    public class SpellCommand : PluginDynamicCommand
    {
        public SpellCommand() : base()
        {
            for (var i = 1; i <= SpellBarReader.GemCount; i++)
            {
                // Layout-neutral label on purpose: the keystroke is sent by physical key
                // position, so this is ALT+& on AZERTY and ALT+1 on QWERTY. Naming the
                // AZERTY character here would read as a bug on any other layout.
                this.AddParameter($"{i}", $"Sort {i} (ALT+{i})", "Sorts EverQuest");
            }
        }

        // Remember the exact reader we subscribed to: during a plugin reload the static
        // already points at the incoming instance, so unsubscribing through it would
        // detach from the wrong object and leak this handler on the outgoing one.
        private SpellBarReader _subscribed;

        protected override Boolean OnLoad()
        {
            this._subscribed = EverQuestPlugin.Reader;
            if (this._subscribed != null)
            {
                this._subscribed.IconsChanged += this.OnIconsChanged;
            }
            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this._subscribed != null)
            {
                this._subscribed.IconsChanged -= this.OnIconsChanged;
                this._subscribed = null;
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
                // GetImage hands over ownership: dispose it or every repaint leaks a
                // decoded 128x128 image until finalization.
                using (var img = EverQuestPlugin.Reader?.GetImage(gem))
                {
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
