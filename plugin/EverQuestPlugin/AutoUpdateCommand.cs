namespace Loupedeck.EverQuestPlugin
{
    using System;

    // Toggles the 30 s background refresh. Auto-update is on by default; this key only
    // exists so it can be turned off (e.g. to keep the machine completely idle).
    public class AutoUpdateCommand : PluginDynamicCommand
    {
        public AutoUpdateCommand()
            : base(displayName: "Mise à jour auto", description: "Active ou coupe le rafraîchissement automatique des icônes", groupName: "Sorts EverQuest")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            IconUpdater.SetAutoUpdate(this.Plugin, !IconUpdater.AutoUpdateEnabled);
            this.ActionImageChanged();
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                if (IconUpdater.AutoUpdateEnabled)
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
