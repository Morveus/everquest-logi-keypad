namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.IO;

    public class EverQuestPlugin : Plugin
    {
        // API-only plugin, not tied to a monitored application.
        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        // The actions look these up; they always point at the live instance.
        internal static SpellBarReader Reader { get; private set; }
        internal static IconUpdater Updater { get; private set; }

        private SpellBarReader _reader;
        private IconUpdater _updater;

        public override void Load()
        {
            var dataDir = this.ResolveDataDirectory();
            this._reader = new SpellBarReader(this, dataDir);
            this._updater = new IconUpdater(this, this._reader);
            Reader = this._reader;
            Updater = this._updater;
            this.Log.Info($"Data directory: {dataDir}");

            // Rebuild last session's key images first (so the keys are never blank), then
            // read the game once so the display is current before the first timer tick.
            var updater = this._updater;
            var reader = this._reader;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                reader.RestoreImages();
                return updater.RunAsync(full: false);
            });
            this._updater.SetAutoUpdate(LoadAutoUpdatePreference(dataDir));
        }

        public override void Unload()
        {
            this._updater?.Dispose();
            // Only clear the statics if a newer instance has not already taken over.
            if (ReferenceEquals(Updater, this._updater)) { Updater = null; }
            if (ReferenceEquals(Reader, this._reader)) { Reader = null; }
        }

        // Turning auto-refresh off must survive a restart, otherwise the toggle looks
        // broken after every reload of Options+.
        private static String _autoPrefPath;

        private static Boolean LoadAutoUpdatePreference(String dataDir)
        {
            _autoPrefPath = Path.Combine(dataDir, "autoupdate.txt");
            try
            {
                if (File.Exists(_autoPrefPath))
                {
                    return !String.Equals(File.ReadAllText(_autoPrefPath).Trim(), "off", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { }
            return true;
        }

        internal static void SaveAutoUpdatePreference(Boolean enabled)
        {
            try
            {
                if (_autoPrefPath != null) { File.WriteAllText(_autoPrefPath, enabled ? "on" : "off"); }
            }
            catch (Exception) { }
        }

        // Everything the plugin writes (calibration, debug icons) lives here. The SDK
        // gives each plugin its own folder; fall back to LocalAppData if unavailable.
        private String ResolveDataDirectory()
        {
            try
            {
                var dir = this.GetPluginDataDirectory();
                if (!String.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch (Exception)
            {
                // Fall through.
            }
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EverQuestSpellPlugin");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }
}
