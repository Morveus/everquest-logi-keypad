namespace Loupedeck.EverQuestPlugin
{
    using System;

    using EqSpells.Core;

    // Bridges the core's vendor-neutral logging onto Logi Plugin Service's own log.
    // The Stream Deck host supplies its equivalent; nothing else differs.
    internal sealed class LogiLog : IPluginLog
    {
        private readonly Plugin _plugin;

        public LogiLog(Plugin plugin) => this._plugin = plugin;

        public void Info(String message) => this._plugin?.Log.Info(message);

        public void Warning(String message) => this._plugin?.Log.Warning(message);

        public void Error(String message) => this._plugin?.Log.Error(message);
    }
}
