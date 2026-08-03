namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Threading;

    // Forces a full read: relocates the spell bar and re-identifies all nine gems.
    // Useful after moving the bar, changing resolution or switching character.
    //
    // Also the plugin's status light. Without it the normal failure mode is "stale or
    // wrong icons with no indication", visible only by opening the log file.
    public class UpdateIconsCommand : PluginDynamicCommand
    {
        private Int32 _running;          // 0/1
        private volatile String _label = "SYNC";
        private volatile Boolean _failed;

        public UpdateIconsCommand()
            : base(displayName: "Refresh icons", description: "Re-reads the EverQuest spell bar and updates the key icons. Turns red when the plugin cannot read the bar.", groupName: "EverQuest Spells")
        {
        }

        protected override Boolean OnLoad()
        {
            var reader = EverQuestPlugin.Reader;
            if (reader != null) { reader.StatusChanged += this.OnStatusChanged; }
            return true;
        }

        protected override Boolean OnUnload()
        {
            var reader = EverQuestPlugin.Reader;
            if (reader != null) { reader.StatusChanged -= this.OnStatusChanged; }
            return true;
        }

        private void OnStatusChanged(Object sender, ReadOutcome outcome)
        {
            // Only a genuine failure changes the light; routine cycles stay quiet.
            var failed = outcome == ReadOutcome.Unreadable;
            if (failed == this._failed && Volatile.Read(ref this._running) == 0) { return; }
            this._failed = failed;
            this._label = failed ? "READ\nFAIL" : "SYNC";
            this.ActionImageChanged();
        }

        protected override void RunCommand(String actionParameter)
        {
            var updater = EverQuestPlugin.Updater;
            if (updater == null) { return; }
            if (Interlocked.CompareExchange(ref this._running, 1, 0) != 0) { return; }

            this._label = "SYNC...";
            this.ActionImageChanged();

            updater.RunAsync(full: true).ContinueWith(t =>
            {
                var bad = t.IsFaulted || t.Result == ReadOutcome.Unreadable || t.Result == ReadOutcome.NotRunning;
                this._failed = bad;
                this._label = t.IsFaulted ? "ERROR"
                    : t.Result == ReadOutcome.NotRunning ? "NO EQ"
                    : bad ? "READ\nFAIL" : "SYNC";
                Volatile.Write(ref this._running, 0);
                this.ActionImageChanged();
            });
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            using (var builder = new BitmapBuilder(imageSize))
            {
                builder.Clear(BitmapColor.Black);
                var colour = Volatile.Read(ref this._running) != 0
                    ? BitmapColor.White
                    : this._failed ? new BitmapColor(255, 80, 80) : new BitmapColor(120, 220, 120);
                builder.DrawText(this._label, colour);
                return builder.ToImage();
            }
        }
    }
}
