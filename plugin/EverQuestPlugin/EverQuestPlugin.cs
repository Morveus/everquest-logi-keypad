namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.IO;

    public class EverQuestPlugin : Plugin
    {
        // API-only plugin, not tied to a monitored application.
        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        // Where update-spell-icons.ps1 lives and writes its output. Derived from this
        // assembly's own location (…\app\plugin\EverQuestPlugin\bin\Release\bin) by walking
        // up to the folder that actually holds the script, so the whole app folder can be
        // moved or copied to another machine without editing anything.
        public static readonly String AppDir = ResolveAppDir();

        private static String ResolveAppDir()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(EverQuestPlugin).Assembly.Location);
                for (var i = 0; i < 8 && !String.IsNullOrEmpty(dir); i++)
                {
                    if (File.Exists(Path.Combine(dir, "update-spell-icons.ps1")))
                    {
                        return dir;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch (Exception)
            {
                // Fall through to the conventional location.
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Everquest Logi", "app");
        }
        public static String IconsDir => Path.Combine(AppDir, "icons");
        public static String UpdateScript => Path.Combine(AppDir, "update-spell-icons.ps1");

        public override void Load()
        {
            this.Log.Info($"Icons dir: '{IconsDir}' (exists: {Directory.Exists(IconsDir)})");
            this.Log.Info($"Update script: '{UpdateScript}' (exists: {File.Exists(UpdateScript)})");
            IconUpdater.SetAutoUpdate(this, enabled: true);
        }

        public override void Unload()
        {
            IconUpdater.Shutdown();
        }
    }
}
