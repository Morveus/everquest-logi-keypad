namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Diagnostics;
    using System.Threading;

    // Runs update-spell-icons.ps1, either on demand (full run, from the "MAJ" key) or
    // on a timer (quick run: cached grid only, skips silently on a bad moment).
    internal static class IconUpdater
    {
        public const Int32 DefaultIntervalSeconds = 30;

        private static readonly Object Sync = new Object();
        private static Boolean _running;
        private static Timer _timer;
        private static Plugin _plugin;

        public static Boolean IsRunning => _running;
        public static Boolean AutoUpdateEnabled { get; private set; }

        // Raised when a run actually rewrote something, so keys can redraw.
        public static event EventHandler IconsChanged;

        public static Boolean IsGameRunning()
        {
            try
            {
                return Process.GetProcessesByName("eqgame").Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Returns the script's exit code, or -1 if it could not be started.
        // 0 = ran, 1 = error, 2 = nothing to do (game closed, minimized, bad moment).
        public static Int32 Run(Boolean quick)
        {
            lock (Sync)
            {
                if (_running)
                {
                    return -1;
                }
                _running = true;
            }

            try
            {
                var args = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{EverQuestPlugin.UpdateScript}\"";
                if (quick)
                {
                    args += " -Quick";
                }
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = EverQuestPlugin.AppDir
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        return 1;
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                _plugin?.Log.Warning($"Icon update failed to start: {ex.Message}");
                return -1;
            }
            finally
            {
                _running = false;
            }
        }

        public static void SetAutoUpdate(Plugin plugin, Boolean enabled, Int32 intervalSeconds = DefaultIntervalSeconds)
        {
            _plugin = plugin;
            AutoUpdateEnabled = enabled;

            _timer?.Dispose();
            _timer = null;

            if (!enabled)
            {
                plugin?.Log.Info("Auto-update disabled");
                return;
            }

            var period = TimeSpan.FromSeconds(intervalSeconds);
            _timer = new Timer(_ => Tick(), null, period, period);
            plugin?.Log.Info($"Auto-update enabled ({intervalSeconds} s)");
        }

        private static void Tick()
        {
            // Cheap guard: no game, nothing to read - do not even spawn PowerShell.
            if (_running || !IsGameRunning())
            {
                return;
            }

            var code = Run(quick: true);
            if (code == 0)
            {
                IconsChanged?.Invoke(null, EventArgs.Empty);
            }
        }

        public static void Shutdown()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
