namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Threading.Tasks;

    // "Mise à jour" button: runs update-spell-icons.ps1 (captures the EQ window and
    // rewrites icons\spell_*.png). SpellCommand's file watcher then refreshes the keys.
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
            this._running = true;
            this._lastFailed = false;
            this.ActionImageChanged();

            _ = Task.Run(() =>
            {
                try
                {
                    // Full run: re-searches the bar if the cached grid no longer fits.
                    var code = IconUpdater.Run(quick: false);
                    this._lastFailed = code != 0;
                }
                catch (Exception)
                {
                    this._lastFailed = true;
                }
                finally
                {
                    this._running = false;
                    this.ActionImageChanged();
                }
            });
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
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
