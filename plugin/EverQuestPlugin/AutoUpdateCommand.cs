namespace Loupedeck.EverQuestPlugin
{
    using System;

    // Toggles the background refresh. Auto-update is on by default; this key only
    // exists so it can be turned off (e.g. to keep the machine completely idle).
    public class AutoUpdateCommand : PluginDynamicCommand
    {
        public AutoUpdateCommand()
            : base(displayName: "Mise à jour auto", description: "Active ou coupe le rafraîchissement automatique des icônes", groupName: "Sorts EverQuest")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            // Read the static once: it can become null between two accesses on unload.
            var updater = EverQuestPlugin.Updater;
            if (updater == null) { return; }
            updater.SetAutoUpdate(!updater.AutoUpdateEnabled);
            EverQuestPlugin.SaveAutoUpdatePreference(updater.AutoUpdateEnabled);
            this.ActionImageChanged();
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                var updater = EverQuestPlugin.Updater;
                if (updater != null && updater.AutoUpdateEnabled)
                {
                    builder.DrawText("AUTO\nON", new BitmapColor(120, 220, 120));
                }
                else
                {
                    builder.DrawText("AUTO\nOFF", new BitmapColor(150, 150, 150));
                }
                return builder.ToImage();
            }
        }
    }
}
