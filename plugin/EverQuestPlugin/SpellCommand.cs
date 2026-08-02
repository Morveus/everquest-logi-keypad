namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.IO;

    // One multi-instance command: parameters "1".."9" map to the nine spell gems.
    // Pressing the key sends ALT+<digit-row key>; the key displays icons\spell_N.png,
    // refreshed automatically whenever the extractor rewrites the files.
    public class SpellCommand : PluginDynamicCommand
    {
        private FileSystemWatcher _watcher;
        private System.Threading.Timer _debounce;

        // Unshifted characters of the AZERTY digit row, for labels only: ALT+& casts gem 1, etc.
        private static readonly String[] AzertyDigitChars = { "&", "é", "\"", "'", "(", "-", "è", "_", "ç" };

        public SpellCommand() : base()
        {
            for (var i = 1; i <= 9; i++)
            {
                this.AddParameter($"{i}", $"Sort {i} (ALT+{AzertyDigitChars[i - 1]})", "Sorts EverQuest");
            }

            try
            {
                if (Directory.Exists(EverQuestPlugin.IconsDir))
                {
                    this._watcher = new FileSystemWatcher(EverQuestPlugin.IconsDir, "spell_*.png")
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                    };
                    this._watcher.Changed += this.OnIconsChanged;
                    this._watcher.Created += this.OnIconsChanged;
                    this._watcher.Renamed += this.OnIconsChanged;
                    this._watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception)
            {
                // Watcher is a nicety; icon refresh still happens on plugin reload.
            }
        }

        private void OnIconsChanged(Object sender, FileSystemEventArgs e)
        {
            // The extractor writes 9 files in a burst; refresh once, shortly after the last write.
            this._debounce?.Dispose();
            this._debounce = new System.Threading.Timer(_ => this.ActionImageChanged(), null, 500, System.Threading.Timeout.Infinite);
        }

        protected override void RunCommand(String actionParameter)
        {
            if (Int32.TryParse(actionParameter, out var digit))
            {
                this.Plugin.Log.Info($"Casting gem {digit}: sending ALT+Key{digit}");
                KeyboardHelper.SendAltDigit(this.Plugin, digit);
            }
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var path = Path.Combine(EverQuestPlugin.IconsDir, $"spell_{actionParameter}@128.png");
            if (!File.Exists(path))
            {
                path = Path.Combine(EverQuestPlugin.IconsDir, $"spell_{actionParameter}.png");
            }

            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                if (File.Exists(path))
                {
                    try
                    {
                        // Stretch the icon over the whole key: no label, edge to edge.
                        var image = BitmapImage.FromFile(path);
                        builder.DrawImage(image, 0, 0, builder.Width, builder.Height);
                        return builder.ToImage();
                    }
                    catch (Exception ex)
                    {
                        this.Plugin.Log.Warning($"Cannot draw '{path}': {ex.Message}");
                    }
                }
                builder.DrawText($"Sort {actionParameter}");
                return builder.ToImage();
            }
        }
    }
}
