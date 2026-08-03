namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    // Schedules the in-process reads. A cycle is a window capture plus nine dot
    // products, so polling can be brisk without spawning anything.
    //
    // Deliberately NOT static: the service can load a new plugin instance before
    // unloading the previous one, and shared static state meant the outgoing instance
    // disposed the incoming one's timer (leaving the plugin silently idle while the old
    // instance kept working). One updater per plugin instance avoids that entirely.
    internal sealed class IconUpdater : IDisposable
    {
        public const Int32 DefaultIntervalSeconds = 5;

        private readonly Object _sync = new Object();
        private readonly Plugin _plugin;
        private readonly SpellBarReader _reader;
        private Timer _timer;
        private Int32 _busy;          // 0/1, guards against overlapping cycles
        private Boolean _disposed;

        public Boolean AutoUpdateEnabled { get; private set; }

        public IconUpdater(Plugin plugin, SpellBarReader reader)
        {
            this._plugin = plugin;
            this._reader = reader;
        }

        // Runs a read off the calling thread. `full` forces relocating the bar.
        public Task<ReadOutcome> RunAsync(Boolean full)
        {
            return Task.Run(() =>
            {
                if (this._disposed) { return ReadOutcome.NotRunning; }
                if (Interlocked.CompareExchange(ref this._busy, 1, 0) != 0)
                {
                    return ReadOutcome.NoChange;   // a cycle is already in flight
                }
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = this._reader.Update(full);
                    sw.Stop();
                    if (full || r == ReadOutcome.Updated || sw.ElapsedMilliseconds > 1000)
                    {
                        this._plugin?.Log.Info($"read full={full} -> {r} ({this._reader.LastStatus}) in {sw.ElapsedMilliseconds} ms");
                    }
                    return r;
                }
                catch (Exception ex)
                {
                    this._plugin?.Log.Error($"Read failed: {ex.GetType().Name}: {ex.Message}");
                    return ReadOutcome.Unreadable;
                }
                finally
                {
                    Volatile.Write(ref this._busy, 0);
                }
            });
        }

        public void SetAutoUpdate(Boolean enabled, Int32 intervalSeconds = DefaultIntervalSeconds)
        {
            lock (this._sync)
            {
                if (this._disposed) { return; }
                this.AutoUpdateEnabled = enabled;
                this._timer?.Dispose();
                this._timer = null;
                if (!enabled)
                {
                    this._plugin?.Log.Info("Auto-update disabled");
                    return;
                }
                var period = TimeSpan.FromSeconds(intervalSeconds);
                this._timer = new Timer(_ => { _ = this.RunAsync(false); }, null, period, period);
                this._plugin?.Log.Info($"Auto-update enabled ({intervalSeconds} s)");
            }
        }

        public void Dispose()
        {
            lock (this._sync)
            {
                this._disposed = true;
                this._timer?.Dispose();
                this._timer = null;
            }
        }
    }
}
