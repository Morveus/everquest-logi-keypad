namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.Drawing.Imaging;
    using System.IO;

    using EqIcon;

    internal enum ReadOutcome
    {
        NotRunning,     // EverQuest is not running (or minimized)
        NoChange,       // every gem still shows the icon we already have
        Updated,        // at least one icon changed
        Unreadable,     // the bar could not be located with confidence
    }

    // Reads the nine spell gems straight from the EverQuest window, in-process.
    // Holds the icon library, the bar geometry and the current key images in memory,
    // so a polling cycle costs a window capture plus nine dot products.
    internal sealed class SpellBarReader
    {
        public const Int32 GemCount = 9;

        // A gem must still resemble its current icon by at least this much to be
        // considered unchanged. Below it, that gem is re-identified.
        private const Single WatchScore = 0.85f;
        // Confidence required to *replace* a displayed icon. A gem drawn with a recast
        // overlay matches random icons around 0.8, so this threshold keeps it stable.
        private const Single ChangeScore = 0.90f;
        // A challenger must beat the incumbent by this margin to take its place.
        private const Single Hysteresis = 0.05f;
        // Many EQ spells share art across ranks. If the best and second-best library
        // icons are this close, the match carries no real information: keep what we show
        // rather than pick confidently-wrong art.
        private const Single MinMargin = 0.02f;
        // Average score required to trust a grid.
        private const Single GridScore = 0.85f;
        // More mismatching gems than this means the geometry is probably wrong,
        // not the spells: re-locate the whole bar instead of re-identifying gems.
        private const Int32 MaxTargetedGems = 4;
        // Locating the bar from scratch sweeps the library over the window: expensive and
        // rare. Do not retry it more often than this after a failure.
        private const Int32 LocateRetrySeconds = 120;
        // Consecutive cycles where nothing could be identified before we suspect the
        // geometry rather than the game state. At 5 s a cycle this is ~2 minutes, which
        // a long fight can exceed but a moved window will too - and the Refresh key is
        // always the immediate way out.
        private const Int32 UnreadableStreakBeforeRelocate = 24;
        // Above this, the geometry is considered locked and no refinement is needed.
        private const Single GoodScore = 0.95f;

        private sealed class Grid
        {
            public Single X, Y0, Size, Stride;
        }

        private sealed class GemState
        {
            public String Sheet;          // full path of the source sheet
            public Int32 Index = -1;
            public Single[] Norm24;       // descriptor of what is currently displayed
            public Byte[] Png;            // encoded key image
            public Single Score;
        }

        private readonly Plugin _plugin;
        private readonly Object _sync = new Object();
        private readonly Object _pngLock = new Object();   // guards GemState.Png only
        private readonly GemState[] _gems = new GemState[GemCount];

        private List<LibIcon> _lib;
        private String _gameDir;
        private String _iconDir;
        private EqGame.UiSettings _ui;
        private Grid _grid;
        // Grid as established by the last full locate. RefineGrid hill-climbs towards the
        // icons currently displayed; if those were ever wrong it would optimise towards
        // the wrong thing, so the refinement is never allowed to wander far from here.
        private Grid _anchorGrid;
        private DateTime _lastLocateFailedUtc = DateTime.MinValue;
        private Int32 _unreadableStreak;

        public String DataDir { get; }
        public String IconsDir => Path.Combine(this.DataDir, "icons");
        public String LastStatus { get; private set; } = "never run";

        public event EventHandler IconsChanged;
        // Raised after every read so a key can show that the plugin is stuck.
        public event EventHandler<ReadOutcome> StatusChanged;

        private ReadOutcome Report(ReadOutcome outcome)
        {
            this.StatusChanged?.Invoke(this, outcome);
            return outcome;
        }

        public SpellBarReader(Plugin plugin, String dataDir)
        {
            this._plugin = plugin;
            this.DataDir = dataDir;
            for (var i = 0; i < GemCount; i++)
            {
                this._gems[i] = new GemState();
            }
            this.LoadState();
        }

        // Returns a fresh BitmapImage each call; the caller owns and disposes it.
        // Handing out a shared instance and disposing it on the next update raced with
        // the SDK drawing the key: the draw threw and Options+ showed the action name.
        //
        // Uses its own tiny lock, NOT _sync: Update() holds _sync for the whole read,
        // which can run a minute when the bar has to be located from scratch, and key
        // rendering must never queue behind that.
        public BitmapImage GetImage(Int32 gem)
        {
            if (gem < 1 || gem > GemCount) { return null; }
            Byte[] png;
            lock (this._pngLock)
            {
                png = this._gems[gem - 1].Png;
            }
            if (png == null) { return null; }
            try { return BitmapImage.FromArray(png); }
            catch (Exception) { return null; }
        }

        // --- Main entry point --------------------------------------------------

        public ReadOutcome Update(Boolean forceFull)
        {
            lock (this._sync)
            {
                var hwnd = EqGame.FindWindow();
                if (hwnd == IntPtr.Zero || EqGame.IsMinimized(hwnd))
                {
                    this.LastStatus = "EverQuest not running";
                    return this.Report(ReadOutcome.NotRunning);
                }

                using (var bmp = EqGame.Capture(hwnd))
                {
                    if (bmp == null)
                    {
                        this.LastStatus = "capture failed";
                        return this.Report(ReadOutcome.NotRunning);
                    }

                    if (!this.EnsureLibrary())
                    {
                        this.LastStatus = "icon pack not found";
                        return this.Report(ReadOutcome.Unreadable);
                    }

                    // Cheap path: does every gem still show what we already display?
                    // Only the spell bar is converted to floats here - a few thousand
                    // pixels instead of the window's several million.
                    if (!forceFull && this._grid != null)
                    {
                        var margin = 8;
                        var strip = FloatImg.FromBitmap(bmp,
                            (Int32)(this._grid.X - margin),
                            (Int32)(this._grid.Y0 - margin),
                            (Int32)(this._grid.Size + 2 * margin),
                            (Int32)((GemCount - 1) * this._grid.Stride + this._grid.Size + 2 * margin));

                        // The stored grid is only as precise as the last full locate, and
                        // sub-pixel drift alone drops every score enough to look like a
                        // change. Re-lock it on the icons we already know before judging.
                        var scores = this.GemScores(strip, this._grid);
                        if (WorstScore(scores) < GoodScore)
                        {
                            this.RefineGrid(strip);
                            scores = this.GemScores(strip, this._grid);
                        }

                        // Every gem unreadable while we do know what they should look like
                        // means the capture itself is blank, not that the spells changed.
                        // Treating that as "unchanged" would freeze stale icons forever.
                        var readable = 0;
                        var known = 0;
                        for (var i = 0; i < GemCount; i++)
                        {
                            if (!Single.IsNaN(scores[i])) { readable++; }
                            if (this._gems[i].Norm24 != null) { known++; }
                        }
                        if (readable == 0 && known > 0)
                        {
                            this.LastStatus = "capture unreadable";
                            return this.Report(ReadOutcome.Unreadable);
                        }

                        var suspicious = new List<Int32>();
                        for (var i = 0; i < GemCount; i++)
                        {
                            if (!Single.IsNaN(scores[i]) && scores[i] < WatchScore) { suspicious.Add(i); }
                            else if (this._gems[i].Norm24 == null) { suspicious.Add(i); }
                        }
                        if (suspicious.Count == 0)
                        {
                            this._unreadableStreak = 0;
                            this.LastStatus = "unchanged";
                            return this.Report(ReadOutcome.NoChange);
                        }
                        // Re-identify whatever no longer matches, however many. Assuming
                        // "more than N gems differ, so the geometry must be wrong" was a
                        // mistake: swapping a whole spell set is routine, and it sent the
                        // plugin into a minute-long relocate that produced no icons at all.
                        // Only fall through to relocating when the gems cannot be
                        // identified at all, which is what a bad grid actually looks like.
                        var n = this.ReidentifyGems(strip, suspicious);
                        if (n > 0)
                        {
                            this._unreadableStreak = 0;
                            this.LastStatus = $"{n} icon(s) updated";
                            this.SaveState();
                            this.IconsChanged?.Invoke(this, EventArgs.Empty);
                            return this.Report(ReadOutcome.Updated);
                        }
                        // Nothing could be identified. In combat EverQuest tints and
                        // darkens the gems for minutes on end, so this is the normal
                        // state of a busy fight - not a sign the geometry moved. Keep
                        // showing what we have and only consider relocating after a long
                        // sustained streak; the Refresh key forces it at any time.
                        this._unreadableStreak++;
                        if (this._unreadableStreak < UnreadableStreakBeforeRelocate)
                        {
                            this.LastStatus = $"gem(s) in transition ({this._unreadableStreak})";
                            return this.Report(ReadOutcome.NoChange);
                        }
                    }

                    // Expensive path: (re)locate the bar over the whole window, then
                    // identify all nine gems. Sweeping the library over the window takes
                    // tens of seconds, so after a failure (bar hidden, character select,
                    // login screen) back off instead of burning a core every cycle.
                    if (!forceFull && this._lastLocateFailedUtc != DateTime.MinValue &&
                        (DateTime.UtcNow - this._lastLocateFailedUtc).TotalSeconds < LocateRetrySeconds)
                    {
                        this.LastStatus = "spell bar not found (retry deferred)";
                        return this.Report(ReadOutcome.Unreadable);
                    }

                    var screen = FloatImg.FromBitmap(bmp);
                    var grid = this.LocateBar(screen);
                    if (grid == null)
                    {
                        this._lastLocateFailedUtc = DateTime.UtcNow;
                        this.LastStatus = "spell bar not found";
                        return this.Report(ReadOutcome.Unreadable);
                    }
                    this._lastLocateFailedUtc = DateTime.MinValue;
                    this._grid = grid;
                    this._anchorGrid = new Grid { X = grid.X, Y0 = grid.Y0, Size = grid.Size, Stride = grid.Stride };

                    var all = new List<Int32>();
                    for (var i = 0; i < GemCount; i++) { all.Add(i); }
                    var changed = this.ReidentifyGems(screen, all);
                    this.SaveState();
                    this.LastStatus = changed > 0 ? $"{changed} icon(s) updated" : "unchanged";
                    if (changed > 0)
                    {
                        this.IconsChanged?.Invoke(this, EventArgs.Empty);
                        return this.Report(ReadOutcome.Updated);
                    }
                    return this.Report(ReadOutcome.NoChange);
                }
            }
        }

        // --- Watch pass --------------------------------------------------------

        // How well each gem still matches the icon it currently displays.
        // NaN means "no information": a flat patch (empty slot, spell being scribed) or
        // a gem we have never identified. It must not be read as "this gem changed".
        private Single[] GemScores(FloatImg img, Grid g)
        {
            var outp = new Single[GemCount];
            for (var i = 0; i < GemCount; i++)
            {
                var st = this._gems[i];
                if (st.Norm24 == null) { outp[i] = Single.NaN; continue; }
                var p = img.Patch(g.X, g.Y0 + i * g.Stride, g.Size, g.Size, 24);
                outp[i] = Matcher.Normalize(p) ? Matcher.Dot(p, st.Norm24) : Single.NaN;
            }
            return outp;
        }

        private static Single WorstScore(Single[] scores)
        {
            var worst = Single.MaxValue;
            var any = false;
            foreach (var s in scores)
            {
                if (Single.IsNaN(s)) { continue; }
                any = true;
                if (s < worst) { worst = s; }
            }
            return any ? worst : Single.NaN;
        }

        private static Single MeanScore(Single[] scores)
        {
            Single sum = 0;
            var n = 0;
            foreach (var s in scores)
            {
                if (Single.IsNaN(s)) { continue; }
                sum += s;
                n++;
            }
            return n == 0 ? Single.NaN : sum / n;
        }

        // Refinement may only nudge the geometry, never walk it to another bar.
        private Boolean WithinAnchor(Grid g)
        {
            var a = this._anchorGrid;
            if (a == null) { return true; }
            const Single maxDrift = 3f;
            return Math.Abs(g.X - a.X) <= maxDrift
                && Math.Abs(g.Y0 - a.Y0) <= maxDrift
                && Math.Abs(g.Size - a.Size) <= maxDrift
                && Math.Abs(g.Stride - a.Stride) <= 1f;
        }

        // Nudge the grid so it best explains the icons we already display. Nine known
        // templates instead of the whole library makes this a few milliseconds, and it
        // keeps the geometry locked sub-pixel between full locates.
        private void RefineGrid(FloatImg strip)
        {
            var g = this._grid;
            var bestScore = MeanScore(this.GemScores(strip, g));
            if (Single.IsNaN(bestScore)) { return; }
            var best = g;

            foreach (var dx in new[] { -0.5f, -0.25f, 0f, 0.25f, 0.5f })
            {
                foreach (var dy in new[] { -0.5f, -0.25f, 0f, 0.25f, 0.5f })
                {
                    foreach (var dsz in new[] { -0.5f, 0f, 0.5f })
                    {
                        foreach (var dst in new[] { -0.125f, 0f, 0.125f })
                        {
                            var cand = new Grid
                            {
                                X = g.X + dx,
                                Y0 = g.Y0 + dy,
                                Size = g.Size + dsz,
                                Stride = g.Stride + dst,
                            };
                            if (!this.WithinAnchor(cand)) { continue; }
                            var s = MeanScore(this.GemScores(strip, cand));
                            if (!Single.IsNaN(s) && s > bestScore) { bestScore = s; best = cand; }
                        }
                    }
                }
            }
            this._grid = best;
        }

        // --- Identification ----------------------------------------------------

        // Re-identify the given gems against the library. Returns how many icons changed.
        private Int32 ReidentifyGems(FloatImg screen, List<Int32> gems)
        {
            var changed = 0;
            foreach (var i in gems)
            {
                var st = this._gems[i];
                var p = screen.Patch(this._grid.X, this._grid.Y0 + i * this._grid.Stride, this._grid.Size, this._grid.Size, 24);
                if (!Matcher.Normalize(p)) { continue; }

                LibIcon best = null;
                Single bestScore = -1, secondScore = -1;
                foreach (var li in this._lib)
                {
                    var s = Matcher.Dot(p, li.Norm24);
                    if (s > bestScore) { secondScore = bestScore; bestScore = s; best = li; }
                    else if (s > secondScore) { secondScore = s; }
                }
                if (best == null) { continue; }

                // Stickiness: how well does what we already show explain this capture?
                var incumbent = st.Norm24 != null ? Matcher.Dot(p, st.Norm24) : -1f;

                var sameAsBefore = st.Sheet != null && st.Sheet == best.Sheet && st.Index == best.Index;
                if (sameAsBefore)
                {
                    st.Score = bestScore;
                    // The descriptor and the image are what the cheap path relies on. A
                    // state restored from disk has neither, and forgetting to fill them
                    // here makes every gem look suspicious forever, which sends every
                    // cycle down the expensive path.
                    st.Norm24 = best.Norm24;
                    if (st.Png == null) { this.ApplyIcon(i, best, bestScore); }
                    continue;
                }
                // Only swap on a confident, clearly better match. A gem mid-recast never
                // reaches this bar, so its icon stays put.
                if (bestScore < ChangeScore || bestScore < incumbent + Hysteresis) { continue; }
                if (bestScore - secondScore < MinMargin) { continue; }

                if (this.ApplyIcon(i, best, bestScore)) { changed++; }
            }
            return changed;
        }

        private Boolean ApplyIcon(Int32 gemIndex, LibIcon icon, Single score)
        {
            try
            {
                using (var bmp40 = CropSheet(icon))
                {
                    if (bmp40 == null) { return false; }
                    var png = EncodeScaledPng(bmp40, 128);
                    var st = this._gems[gemIndex];
                    lock (this._pngLock) { st.Png = png; }
                    st.Sheet = icon.Sheet;
                    st.Index = icon.Index;
                    st.Norm24 = icon.Norm24;
                    st.Score = score;
                    this.WriteDebugPng(gemIndex + 1, png);
                }
                return true;
            }
            catch (Exception ex)
            {
                this._plugin?.Log.Warning($"Cannot build icon for gem {gemIndex + 1}: {ex.Message}");
                return false;
            }
        }

        private static Bitmap CropSheet(LibIcon icon)
        {
            using (var full = Tga.Load(icon.Sheet))
            {
                if (full == null) { return null; }
                var cell = icon.CellSize;
                var cols = full.Width / cell;
                var sx = (icon.Index % cols) * cell;
                var sy = (icon.Index / cols) * cell;
                var outp = new Bitmap(cell, cell, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(outp))
                {
                    g.DrawImage(full, new Rectangle(0, 0, cell, cell), sx, sy, cell, cell, GraphicsUnit.Pixel);
                }
                return outp;
            }
        }

        // Nearest-neighbour upscale keeps the pixel-art crisp on the key display.
        private static Byte[] EncodeScaledPng(Bitmap src, Int32 size)
        {
            using (var big = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(src, 0, 0, size, size);
                }
                using (var ms = new MemoryStream())
                {
                    big.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        private void WriteDebugPng(Int32 gemNumber, Byte[] png)
        {
            try
            {
                Directory.CreateDirectory(this.IconsDir);
                File.WriteAllBytes(Path.Combine(this.IconsDir, $"spell_{gemNumber}.png"), png);
            }
            catch (Exception)
            {
                // Debug output only; never fail the update because of it.
            }
        }

        // --- Locating the bar --------------------------------------------------

        private Grid LocateBar(FloatImg screen)
        {
            // 1. Revalidate the grid we already know.
            if (this._grid != null)
            {
                var fit = Matcher.FindBar(screen, this._lib,
                    this._grid.X - 1, this._grid.X + 1, this._grid.Y0 - 1, this._grid.Y0 + 1,
                    this._grid.Size - 1, this._grid.Size + 1, this._grid.Stride - 0.25f, this._grid.Stride + 0.25f);
                if (Average(fit) >= GridScore) { return ToGrid(fit); }
            }

            // 2. Locate by structure: the gems repeat at a fixed vertical stride, which
            //    is found over the full window height in milliseconds.
            var seedX = this._ui?.BarXPercent != null ? (Int32)(screen.W * this._ui.BarXPercent.Value / 100.0) : 12;
            var xLo = Math.Max(0, seedX - 25);
            var xHi = seedX + 35;

            // Proven range, deliberately narrow. Widening it let the periodicity lock
            // onto a harmonic and the bar was read four gems too low; and picking the
            // "topmost alignment that still scores" read it four gems too high, because
            // best-match-over-2262-icons stays high on plain background - it says which
            // icon is closest, never whether an icon is there at all.
            var per = Matcher.FindGridPeriodic(screen, xLo, xHi + 45, 38f, 47f, 0.5f, GemCount);
            var py = per[0];
            var ps = per[1];
            var szLo = (Int32)Math.Max(20, ps * 0.72f);
            var szHi = (Int32)(ps * 0.98f);

            var found = Matcher.FindBar(screen, this._lib,
                xLo, xHi + 20, py - ps / 2 - 4, py + ps / 2 + 4, szLo, szHi, ps - 1, ps + 1);

            // 3. Climb one gem at a time while the cell above still reads as an icon.
            for (var up = 0; up < 13; up++)
            {
                var yAbove = found.Y0 - found.Stride;
                if (yAbove < 0) { break; }
                if (Matcher.ScoreCell(screen, this._lib, found.X, yAbove, found.Scale) < 0.80f) { break; }
                found = Matcher.FindBar(screen, this._lib,
                    found.X - 1, found.X + 1, yAbove - 1, yAbove + 1,
                    found.Scale - 1, found.Scale + 1, found.Stride - 0.5f, found.Stride + 0.5f);
            }

            // 4. Now that the gems are located, check we are reading the icon pack the
            //    game actually draws; if not, switch and re-match against the winner.
            if (this.ChooseBestPack(screen, found))
            {
                found = Matcher.FindBar(screen, this._lib,
                    found.X - 0.5f, found.X + 0.5f, found.Y0 - 0.5f, found.Y0 + 0.5f,
                    found.Scale - 0.5f, found.Scale + 0.5f, found.Stride - 0.25f, found.Stride + 0.25f);
            }

            return Average(found) >= GridScore ? ToGrid(found) : null;
        }

        private static Grid ToGrid(BarFit f) => new Grid { X = f.X, Y0 = f.Y0, Size = f.Scale, Stride = f.Stride };

        private static Single Average(BarFit f)
        {
            if (f?.Gems == null || f.Gems.Count == 0) { return 0; }
            Single sum = 0;
            foreach (var g in f.Gems) { sum += g.Score; }
            return sum / f.Gems.Count;
        }

        // --- Library -----------------------------------------------------------

        private Boolean EnsureLibrary()
        {
            if (this._lib != null && this._lib.Count > 0) { return true; }

            this._gameDir = this._gameDir ?? EqGame.FindGameDir();
            if (this._gameDir == null) { return false; }
            this._ui = this._ui ?? EqGame.ReadUiSettings(this._gameDir);

            var packs = EqGame.FindIconPacks(this._gameDir, this._ui.Skin);
            if (packs.Count == 0)
            {
                // The remembered folder no longer holds icons (game moved or reinstalled).
                // Forget it so the next cycle rediscovers instead of failing forever.
                this._plugin?.Log.Warning($"No icon pack under '{this._gameDir}' - forgetting it");
                this._gameDir = null;
                this._iconDir = null;
                this._ui = null;
                return false;
            }

            // Reuse the pack chosen previously; otherwise start with the first and let
            // ChooseBestPack correct it once the bar is located.
            var dir = (this._iconDir != null && Directory.Exists(this._iconDir)) ? this._iconDir : packs[0];
            this._lib = Matcher.LoadLibrary(dir, "Spells*.tga", 40, 107, 107, 107);
            this._iconDir = dir;
            this._plugin?.Log.Info($"Icon pack '{Path.GetFileName(dir)}' loaded ({this._lib.Count} icons, {packs.Count} distinct pack(s))");
            return this._lib.Count > 0;
        }

        // Score every distinct pack against the located gems and keep the best.
        // Returns true when the active pack changed.
        private Boolean ChooseBestPack(FloatImg screen, BarFit fit)
        {
            var packs = EqGame.FindIconPacks(this._gameDir, this._ui.Skin);
            if (packs.Count < 2) { return false; }
            var bestDir = this._iconDir;
            var bestScore = Matcher.ScorePack(screen, this._lib, fit);
            List<LibIcon> bestLib = null;
            foreach (var dir in packs)
            {
                if (dir == this._iconDir) { continue; }
                var trial = Matcher.LoadLibrary(dir, "Spells*.tga", 40, 107, 107, 107);
                var s = Matcher.ScorePack(screen, trial, fit);
                if (s > bestScore + 0.01f) { bestScore = s; bestDir = dir; bestLib = trial; }
            }
            if (bestLib == null) { return false; }

            this._plugin?.Log.Info($"Switching icon pack to '{Path.GetFileName(bestDir)}' (score {bestScore:F3})");
            this._iconDir = bestDir;
            this._lib = bestLib;
            // Descriptors from the old pack are meaningless now.
            foreach (var st in this._gems) { st.Norm24 = null; st.Sheet = null; st.Index = -1; }
            return true;
        }

        // --- Persistence -------------------------------------------------------

        private String StatePath => Path.Combine(this.DataDir, "barstate.txt");

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(this.DataDir);
                var lines = new List<String>();
                if (this._grid != null)
                {
                    // Invariant culture on purpose: interpolation would use the machine's
                    // locale and write "22,25", which the invariant parser then rejects -
                    // silently losing the calibration on every restart.
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    lines.Add("grid=" + String.Join("|", new[]
                    {
                        this._grid.X.ToString("R", inv),
                        this._grid.Y0.ToString("R", inv),
                        this._grid.Size.ToString("R", inv),
                        this._grid.Stride.ToString("R", inv),
                    }));
                }
                if (this._iconDir != null) { lines.Add("iconDir=" + this._iconDir); }
                if (this._gameDir != null) { lines.Add("gameDir=" + this._gameDir); }
                for (var i = 0; i < GemCount; i++)
                {
                    var st = this._gems[i];
                    if (st.Sheet != null) { lines.Add($"gem{i + 1}={st.Sheet}|{st.Index}"); }
                }
                File.WriteAllLines(this.StatePath, lines);
            }
            catch (Exception) { }
        }

        private void LoadState()
        {
            try
            {
                if (!File.Exists(this.StatePath)) { return; }
                foreach (var line in File.ReadAllLines(this.StatePath))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    var key = line.Substring(0, eq);
                    var val = line.Substring(eq + 1);

                    if (key == "grid")
                    {
                        // Tolerate files written by an earlier build using a comma.
                        var p = val.Replace(',', '.').Split('|');
                        if (p.Length == 4 &&
                            Single.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                            Single.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                            Single.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) &&
                            Single.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                        {
                            this._grid = new Grid { X = x, Y0 = y, Size = s, Stride = t };
                        }
                    }
                    else if (key == "iconDir") { this._iconDir = val; }
                    else if (key == "gameDir") { this._gameDir = val; }
                    else if (key.StartsWith("gem"))
                    {
                        if (Int32.TryParse(key.Substring(3), out var n) && n >= 1 && n <= GemCount)
                        {
                            var cut = val.LastIndexOf('|');
                            if (cut > 0 && Int32.TryParse(val.Substring(cut + 1), out var idx))
                            {
                                this._gems[n - 1].Sheet = val.Substring(0, cut);
                                this._gems[n - 1].Index = idx;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        // Rebuild in-memory images and descriptors for gems restored from disk.
        public void RestoreImages()
        {
            lock (this._sync)
            {
                if (!this.EnsureLibrary()) { return; }
                var byKey = new Dictionary<String, LibIcon>();
                foreach (var li in this._lib) { byKey[li.Sheet + "|" + li.Index] = li; }

                var known = 0;
                foreach (var st0 in this._gems) { if (st0.Sheet != null) { known++; } }
                var restored = 0;
                for (var i = 0; i < GemCount; i++)
                {
                    var st = this._gems[i];
                    if (st.Sheet == null || st.Png != null) { continue; }
                    if (byKey.TryGetValue(st.Sheet + "|" + st.Index, out var li) && this.ApplyIcon(i, li, st.Score))
                    {
                        restored++;
                    }
                }
                // Without this the keys keep whatever the host drew before: the first
                // read after a reload finds everything already correct, reports
                // NoChange, and nothing ever asks them to repaint.
                this._plugin?.Log.Info($"RestoreImages: {known} gem(s) in saved state, {restored} rebuilt, library={this._lib?.Count ?? 0} from '{this._iconDir}'");
                if (restored > 0) { this.IconsChanged?.Invoke(this, EventArgs.Empty); }
            }
        }
    }
}
