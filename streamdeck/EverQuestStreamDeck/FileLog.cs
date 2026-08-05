namespace EverQuestStreamDeck
{
    using System;
    using System.IO;
    using System.Text;

    using EqSpells.Core;

    // Stream Deck's own logMessage goes into the software's log, which is rotated and
    // shared with every other plugin. Diagnosing this one wants its own file, kept
    // small enough that it never needs attention.
    internal sealed class FileLog : IPluginLog
    {
        private const Int64 MaxBytes = 512 * 1024;

        private readonly Object _sync = new Object();
        private readonly String _path;

        public FileLog(String dataDir)
        {
            this._path = Path.Combine(dataDir, "plugin.log");
        }

        public void Info(String message) => this.Write("INFO ", message);

        public void Warning(String message) => this.Write("WARN ", message);

        public void Error(String message) => this.Write("ERROR", message);

        private void Write(String level, String message)
        {
            try
            {
                lock (this._sync)
                {
                    // Truncate rather than rotate: this log is for the last session's
                    // behaviour, and a plugin that quietly fills a disk is worse than one
                    // that forgets its own history.
                    var info = new FileInfo(this._path);
                    if (info.Exists && info.Length > MaxBytes) { File.Delete(this._path); }

                    var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                    File.AppendAllText(this._path, $"{stamp} | {level} | {message}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never be the thing that breaks the plugin.
            }
        }
    }
}
