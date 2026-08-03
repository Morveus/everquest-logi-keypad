namespace Loupedeck.EverQuestPlugin
{
    using System;

    // Forces a full read: relocates the spell bar and re-identifies all nine gems.
    // Useful after moving the bar, changing resolution or switching character.
    public class UpdateIconsCommand : PluginDynamicCommand
    {
        private Boolean _running;
        private Boolean _lastFailed;

        public UpdateIconsCommand()
            : base(displayName: "Mettre à jour les icônes", description: "Relit la barre de sorts EverQuest et met à jour les icônes des touches", groupName: "Sorts EverQuest")
        {
        }

        protected override void RunCommand(String actionParameter)
        {
            if (this._running)
            {
                return;
            }
            if (EverQuestPlugin.Updater == null) { return; }
            this._running = true;
            this._lastFailed = false;
            this.ActionImageChanged();

            EverQuestPlugin.Updater.RunAsync(full: true).ContinueWith(t =>
            {
                this._lastFailed = t.IsFaulted || t.Result == ReadOutcome.Unreadable;
                this._running = false;
                this.ActionImageChanged();
            });
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                if (this._running)
                {
                    builder.DrawText("MAJ...", BitmapColor.White);
                }
                else if (this._lastFailed)
                {
                    builder.DrawText("MAJ !", new BitmapColor(255, 80, 80));
                }
                else
                {
                    builder.DrawText("MAJ", new BitmapColor(120, 220, 120));
                }
                return builder.ToImage();
            }
        }
    }
}
