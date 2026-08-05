namespace EqSpells.Core
{
    using System;

    // The recognition core must not know which keypad SDK is hosting it, so it logs
    // through this instead of through a vendor's Plugin object. Each host adapts its
    // own logger in a couple of lines.
    public interface IPluginLog
    {
        void Info(String message);
        void Warning(String message);
        void Error(String message);
    }
}
