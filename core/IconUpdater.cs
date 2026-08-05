namespace EqSpells.Core
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    // Schedules the in-process reads. A cycle is a window capture plus nine dot
    // products, so polling can be brisk without spawning anything.
    //
    // Deliberately NOT static: a host can load a new plugin instance before unloading
    // the previous one, and shared static state meant the outgoing instance disposed the
    // incoming one's timer (leaving the plugin silently idle while the old instance kept
    // working). One updater per host instance avoids that entirely.
    internal sealed class IconUpdater : IDisposable
    {
        public const Int32 DefaultIntervalSeconds = 5;
        // How often the cached images are pushed to the keys again, independently of any
        // scanning. Cheap: nine image hand-overs, no capture and no matching.
        public const Int32 RepaintIntervalSeconds = 15;
        // While EverQuest is not running there is nothing to read and nothing can change,
        // so the plugin drops to this slower heartbeat and stops re-asserting images.
        public const Int32 IdleIntervalSeconds = 30;
        // Consecutive idle cycles before the icon library (~17 MB) is released. It costs
        // ~130 ms to reload, so this only pays off once the game has been gone a while.
        private const Int32 IdleCyclesBeforeRelease = 10;

        private readonly Object _sync = new Object();
        private readonly IPluginLog _log;
        private readonly SpellBarReader _reader;
        private Timer _timer;
        private Timer _repaintTimer;
        private Int32 _busy;          // 0/1, guards against overlapping cycles
        private Boolean _disposed;
        private Boolean _idle;        // last read found no game
        private Int32 _idleCycles;
        private Int32 _intervalSeconds = DefaultIntervalSeconds;

        public Boolean AutoUpdateEnabled { get; private set; }

        // Pushes the cached key images to the device again. Costs nothing but a redraw:
        // no capture, no matching. A safety net against the host dropping a repaint.
        public Action ForceRepaint { get; set; }

        public IconUpdater(IPluginLog log, SpellBarReader reader)
        {
            this._log = log;
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
                    this.ApplyIdlePolicy(r);
                    if (full || r == ReadOutcome.Updated || sw.ElapsedMilliseconds > 1000)
                    {
                        this._log?.Info($"read full={full} -> {r} ({this._reader.LastStatus}) in {sw.ElapsedMilliseconds} ms");
                    }
                    return r;
                }
                catch (Exception ex)
                {
                    this._log?.Error($"Read failed: {ex.GetType().Name}: {ex.Message}");
                    return ReadOutcome.Unreadable;
                }
                finally
                {
                    Volatile.Write(ref this._busy, 0);
                }
            });
        }

        // Slow down and stop repainting while the game is absent; speed back up as soon
        // as it returns. Also frees the icon library after a long absence.
        private void ApplyIdlePolicy(ReadOutcome outcome)
        {
            var idle = outcome == ReadOutcome.NotRunning;
            if (idle)
            {
                this._idleCycles++;
                if (this._idleCycles == IdleCyclesBeforeRelease)
                {
                    this._reader.ReleaseLibrary();
                    this._log?.Info("EverQuest gone; icon library released");
                }
            }
            else
            {
                this._idleCycles = 0;
            }
            if (idle == this._idle) { return; }

            this._idle = idle;
            lock (this._sync)
            {
                if (this._disposed || !this.AutoUpdateEnabled) { return; }
                this._intervalSeconds = idle ? IdleIntervalSeconds : DefaultIntervalSeconds;
                var period = TimeSpan.FromSeconds(this._intervalSeconds);
                this._timer?.Change(period, period);
                // Nothing can change on screen while the game is gone: no point pushing
                // the same images to the keys over and over.
                this._repaintTimer?.Change(
                    idle ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(RepaintIntervalSeconds),
                    idle ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(RepaintIntervalSeconds));
                this._log?.Info(idle
                    ? $"EverQuest not running: polling every {IdleIntervalSeconds} s, repaint paused"
                    : $"EverQuest back: polling every {DefaultIntervalSeconds} s");
            }
        }

        public void SetAutoUpdate(Boolean enabled, Int32 intervalSeconds = DefaultIntervalSeconds)
        {
            lock (this._sync)
            {
                if (this._disposed) { return; }
                this.AutoUpdateEnabled = enabled;
                this._timer?.Dispose();
                this._timer = null;
                this._repaintTimer?.Dispose();
                this._repaintTimer = null;
                if (!enabled)
                {
                    this._log?.Info("Auto-update disabled");
                    return;
                }
                var period = TimeSpan.FromSeconds(intervalSeconds);
                this._timer = new Timer(_ => { _ = this.RunAsync(false); }, null, period, period);

                // Independent, deliberately dumb loop: re-assert the images we already
                // hold. It fixes nothing about recognition - it only guarantees the keys
                // eventually show what the plugin already knows, whatever the host did.
                this._repaintTimer?.Dispose();
                this._repaintTimer = new Timer(
                    _ => { try { this.ForceRepaint?.Invoke(); } catch (Exception) { } },
                    null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(RepaintIntervalSeconds));

                this._log?.Info($"Auto-update enabled ({intervalSeconds} s, repaint every {RepaintIntervalSeconds} s)");
            }
        }

        public void Dispose()
        {
            lock (this._sync)
            {
                this._disposed = true;
                this._timer?.Dispose();
                this._timer = null;
                this._repaintTimer?.Dispose();
                this._repaintTimer = null;
            }
        }
    }
}
