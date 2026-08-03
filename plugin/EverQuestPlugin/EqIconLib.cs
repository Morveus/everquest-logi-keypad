using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace EqIcon
{
    // Minimal TGA decoder: supports type 2 (uncompressed truecolor) and type 10 (RLE truecolor), 24/32 bpp.
    public static class Tga
    {
        public static Bitmap Load(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            int idLen = d[0];
            int imageType = d[2];
            int w = d[12] | (d[13] << 8);
            int h = d[14] | (d[15] << 8);
            int bpp = d[16];
            bool topDown = (d[17] & 0x20) != 0;
            int bytesPp = bpp / 8;
            if (imageType != 2 && imageType != 10)
                throw new Exception("Unsupported TGA type " + imageType + " in " + path);
            if (bytesPp != 3 && bytesPp != 4)
                throw new Exception("Unsupported TGA bpp " + bpp + " in " + path);

            int off = 18 + idLen;
            byte[] pix = new byte[w * h * 4]; // BGRA
            int count = w * h;

            if (imageType == 2)
            {
                for (int i = 0; i < count; i++)
                {
                    int s = off + i * bytesPp;
                    int t = i * 4;
                    pix[t] = d[s];
                    pix[t + 1] = d[s + 1];
                    pix[t + 2] = d[s + 2];
                    pix[t + 3] = bytesPp == 4 ? d[s + 3] : (byte)255;
                }
            }
            else // RLE
            {
                int i = 0, s = off;
                while (i < count)
                {
                    byte hdr = d[s++];
                    int n = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0)
                    {
                        byte b = d[s], g = d[s + 1], r = d[s + 2];
                        byte a = bytesPp == 4 ? d[s + 3] : (byte)255;
                        s += bytesPp;
                        for (int k = 0; k < n; k++, i++)
                        {
                            int t = i * 4;
                            pix[t] = b; pix[t + 1] = g; pix[t + 2] = r; pix[t + 3] = a;
                        }
                    }
                    else
                    {
                        for (int k = 0; k < n; k++, i++)
                        {
                            int t = i * 4;
                            pix[t] = d[s]; pix[t + 1] = d[s + 1]; pix[t + 2] = d[s + 2];
                            pix[t + 3] = bytesPp == 4 ? d[s + 3] : (byte)255;
                            s += bytesPp;
                        }
                    }
                }
            }

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : (h - 1 - y);
                System.Runtime.InteropServices.Marshal.Copy(pix, srcRow * w * 4, bd.Scan0 + y * bd.Stride, w * 4);
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
    }

    // RGBA float image for fast repeated sampling.
    public class FloatImg
    {
        public int W, H;
        public float[] R, G, B, A;

        // Where this buffer starts inside the source image. Converting a whole game
        // window is millions of pixels per poll; converting just the spell bar is a few
        // thousand. Patch() takes source coordinates and subtracts this offset, so
        // callers keep working in window coordinates either way.
        // NOTE: FindBar / FindGridPeriodic assume a zero offset (full-window buffer).
        public int OffsetX, OffsetY;

        public static FloatImg FromBitmap(Bitmap bmp) => FromBitmap(bmp, 0, 0, bmp.Width, bmp.Height);

        // Convert only the given source rectangle (clamped to the bitmap).
        public static FloatImg FromBitmap(Bitmap bmp, int rx, int ry, int rw, int rh)
        {
            rx = Math.Max(0, Math.Min(rx, bmp.Width - 1));
            ry = Math.Max(0, Math.Min(ry, bmp.Height - 1));
            rw = Math.Max(1, Math.Min(rw, bmp.Width - rx));
            rh = Math.Max(1, Math.Min(rh, bmp.Height - ry));

            var f = new FloatImg { W = rw, H = rh, OffsetX = rx, OffsetY = ry };
            int n = f.W * f.H;
            f.R = new float[n]; f.G = new float[n]; f.B = new float[n]; f.A = new float[n];
            BitmapData bd = bmp.LockBits(new Rectangle(rx, ry, f.W, f.H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] row = new byte[f.W * 4];
            for (int y = 0; y < f.H; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, f.W * 4);
                for (int x = 0; x < f.W; x++)
                {
                    int i = y * f.W + x;
                    f.B[i] = row[x * 4];
                    f.G[i] = row[x * 4 + 1];
                    f.R[i] = row[x * 4 + 2];
                    f.A[i] = row[x * 4 + 3];
                }
            }
            bmp.UnlockBits(bd);
            return f;
        }

        // Composite this image over a solid background color, in place (alpha -> 255).
        public void CompositeOver(float bgR, float bgG, float bgB)
        {
            for (int i = 0; i < R.Length; i++)
            {
                float a = A[i] / 255f;
                R[i] = R[i] * a + bgR * (1 - a);
                G[i] = G[i] * a + bgG * (1 - a);
                B[i] = B[i] * a + bgB * (1 - a);
                A[i] = 255;
            }
        }

        // Bilinear-resampled RGB patch (region sx,sy,sw,sh -> size x size), concatenated [R..G..B..].
        public float[] Patch(float sx, float sy, float sw, float sh, int size)
        {
            float[] outp = new float[size * size * 3];
            int plane = size * size;
            for (int oy = 0; oy < size; oy++)
            {
                for (int ox = 0; ox < size; ox++)
                {
                    float fx = sx - OffsetX + (ox + 0.5f) * sw / size - 0.5f;
                    float fy = sy - OffsetY + (oy + 0.5f) * sh / size - 0.5f;
                    int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
                    float ax = fx - x0, ay = fy - y0;
                    float ar = 0, ag = 0, ab = 0;
                    for (int dy = 0; dy <= 1; dy++)
                    {
                        int yy = Math.Min(Math.Max(y0 + dy, 0), H - 1);
                        for (int dx = 0; dx <= 1; dx++)
                        {
                            int xx = Math.Min(Math.Max(x0 + dx, 0), W - 1);
                            float wgt = (dx == 1 ? ax : 1 - ax) * (dy == 1 ? ay : 1 - ay);
                            int i = yy * W + xx;
                            ar += R[i] * wgt; ag += G[i] * wgt; ab += B[i] * wgt;
                        }
                    }
                    int t = oy * size + ox;
                    outp[t] = ar; outp[plane + t] = ag; outp[2 * plane + t] = ab;
                }
            }
            return outp;
        }
    }

    public class LibIcon
    {
        public string Sheet;
        public int Index;      // index within sheet (row-major)
        public int CellX, CellY;
        public int CellSize;   // 24 (gemicons) or 40 (Spells)
        public float[] Norm24; // 24x24x3 zero-mean unit-norm
        public float[] Norm8;  // 8x8x3 coarse fingerprint
    }

    public class GemMatch
    {
        public int Gem;               // 0-based gem slot
        public float IconX, IconY, IconSize; // where the icon was sampled in the capture
        public string Sheet;
        public int Index;
        public float Score;           // NCC of best match (1.0 = identical structure)
        public float Margin;          // best minus second-best (may be ~0 when duplicates exist in sheets)
    }

    public class BarFit
    {
        public float X, Y0, Scale, Stride; // icon left, first icon top, icon size px, vertical stride px
        public float TotalScore;
        public List<GemMatch> Gems = new List<GemMatch>();
    }

    public static class Matcher
    {

        public static bool Normalize(float[] v)
        {
            float mean = 0;
            for (int i = 0; i < v.Length; i++) mean += v[i];
            mean /= v.Length;
            float ss = 0;
            for (int i = 0; i < v.Length; i++) { v[i] -= mean; ss += v[i] * v[i]; }
            if (ss < 1e-3f) return false;
            float inv = (float)(1.0 / Math.Sqrt(ss));
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
            return true;
        }

        public static float Dot(float[] a, float[] b)
        {
            float s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        public static List<LibIcon> LoadLibrary(string uiDir, string pattern, int cellSize, float bgR, float bgG, float bgB)
        {
            var icons = new List<LibIcon>();
            foreach (string f in Directory.GetFiles(uiDir, pattern))
            {
                Bitmap sheetBmp = Tga.Load(f);
                FloatImg sheet = FloatImg.FromBitmap(sheetBmp);
                sheetBmp.Dispose();
                sheet.CompositeOver(bgR, bgG, bgB);
                int cols = sheet.W / cellSize, rows = sheet.H / cellSize;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        var li = new LibIcon
                        {
                            Sheet = f, // full path so extraction can crop from the exact source file
                            Index = r * cols + c,
                            CellX = c * cellSize,
                            CellY = r * cellSize,
                            CellSize = cellSize
                        };
                        li.Norm24 = sheet.Patch(li.CellX, li.CellY, cellSize, cellSize, 24);
                        li.Norm8 = sheet.Patch(li.CellX, li.CellY, cellSize, cellSize, 8);
                        bool ok24 = Normalize(li.Norm24);
                        bool ok8 = Normalize(li.Norm8);
                        if (ok24 && ok8) icons.Add(li); // skip flat/empty cells
                    }
                }
            }
            return icons;
        }

        // Sum of best NCC scores over gems 0..nGems-1 for a candidate grid, dropping the single worst.
        // Grid model: icon g sampled at (x, y0 + g*stride, size, size).
        static float ComboScore(FloatImg screen, List<LibIcon> lib, float x, float y0, float size, float stride, int nGems, int fpSize)
        {
            float sum = 0, worst = float.MaxValue;
            for (int g = 0; g < nGems; g++)
            {
                float[] p = screen.Patch(x, y0 + g * stride, size, size, fpSize);
                float best = 0;
                if (Normalize(p))
                {
                    foreach (var li in lib)
                    {
                        float s = Dot(p, fpSize == 8 ? li.Norm8 : li.Norm24);
                        if (s > best) best = s;
                    }
                }
                sum += best;
                if (best < worst) worst = best;
            }
            return nGems > 3 ? sum - worst : sum;
        }

        // How well a library explains the icons at an already-known grid: mean of each
        // gem's best NCC. Used to pick which icon pack the game is actually drawing.
        public static float ScorePack(FloatImg screen, List<LibIcon> lib, BarFit fit)
        {
            float total = 0;
            int n = 0;
            foreach (var m in fit.Gems)
            {
                float[] p = screen.Patch(m.IconX, m.IconY, m.IconSize, m.IconSize, 24);
                if (!Normalize(p)) { continue; }
                float best = -1;
                foreach (var li in lib)
                {
                    float s = Dot(p, li.Norm24);
                    if (s > best) { best = s; }
                }
                total += best;
                n++;
            }
            return n == 0 ? 0 : total / n;
        }

        // Locate the spell bar by its geometry instead of its content: the gems form a run of
        // identical-looking cells repeating at a fixed vertical stride. Comparing rows to the
        // rows one stride below finds that run over the full window height in milliseconds,
        // where sweeping the icon library over every position takes minutes.
        // Returns { y0, stride, quality }.
        public static float[] FindGridPeriodic(FloatImg screen, int xLo, int xHi,
            float sMin, float sMax, float sStep, int minGems)
        {
            xLo = Math.Max(0, xLo);
            xHi = Math.Min(screen.W, xHi);
            int bins = 8;
            int width = xHi - xLo;
            if (width < bins) { return new float[] { 0, 0, 0 }; }

            // One mean-removed, unit-norm descriptor per row. Rows with no horizontal
            // structure (flat background) stay invalid and can never match, which keeps
            // large uniform areas from looking "periodic".
            var desc = new float[screen.H][];
            for (int y = 0; y < screen.H; y++)
            {
                var v = new float[bins];
                for (int b = 0; b < bins; b++)
                {
                    int x0 = xLo + b * width / bins;
                    int x1 = xLo + (b + 1) * width / bins;
                    float sum = 0;
                    int cnt = 0;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * screen.W + x;
                        sum += 0.299f * screen.R[i] + 0.587f * screen.G[i] + 0.114f * screen.B[i];
                        cnt++;
                    }
                    v[b] = cnt > 0 ? sum / cnt : 0;
                }
                desc[y] = Normalize(v) ? v : null;
            }

            float bestY = 0, bestS = sMin, bestQ = -1;
            for (float s = sMin; s <= sMax + 1e-4f; s += sStep)
            {
                int si = (int)Math.Round(s);
                int span = (int)Math.Round((minGems - 1) * s);
                if (span <= 0 || span >= screen.H) { continue; }

                // sim[y]: does the content at y repeat one stride lower?
                var sim = new float[screen.H];
                for (int y = 0; y + si < screen.H; y++)
                {
                    var a = desc[y];
                    var b = desc[y + si];
                    sim[y] = (a == null || b == null) ? 0 : Dot(a, b);
                }

                // Running mean over a window covering the whole expected run of gems.
                float acc = 0;
                for (int y = 0; y < span && y < sim.Length; y++) { acc += sim[y]; }
                for (int y0 = 0; y0 + span < screen.H - si; y0++)
                {
                    float q = acc / span;
                    if (q > bestQ) { bestQ = q; bestY = y0; bestS = s; }
                    acc -= sim[y0];
                    acc += sim[y0 + span];
                }
            }
            return new[] { bestY, bestS, bestQ };
        }

        // Best NCC for a single cell, used to test whether another gem sits above the one
        // we found (the sweep can land mid-bar, which would shift every icon).
        public static float ScoreCell(FloatImg screen, List<LibIcon> lib, float x, float y, float size)
        {
            float[] p = screen.Patch(x, y, size, size, 24);
            if (!Normalize(p)) { return 0; }
            float best = -1;
            foreach (var li in lib)
            {
                float s = Dot(p, li.Norm24);
                if (s > best) { best = s; }
            }
            return best;
        }

        // Coarse sweep over a wide area with explicit steps, used to locate the bar when the
        // cached grid no longer applies (fresh machine, bar moved, resolution changed).
        // Returns { x, y0, size, stride, score }. Keep nGems small and the library short:
        // cost is (positions x sizes x strides) * nGems * lib.Count.
        public static float[] FindBarCoarse(FloatImg screen, List<LibIcon> lib,
            float xMin, float xMax, float xStep,
            float yMin, float yMax, float yStep,
            float szMin, float szMax, float szStep,
            float strMin, float strMax, float strStep,
            int nGems)
        {
            float bx = xMin, by = yMin, bsz = szMin, bst = strMin, best = -1e9f;
            for (float st = strMin; st <= strMax + 1e-4f; st += strStep)
            {
                // Do not run the grid off the bottom of the capture.
                float maxY = Math.Min(yMax, screen.H - (nGems - 1) * st - szMax - 1);
                for (float sz = szMin; sz <= szMax + 1e-4f; sz += szStep)
                {
                    for (float x = xMin; x <= xMax + 1e-4f; x += xStep)
                    {
                        for (float y = yMin; y <= maxY + 1e-4f; y += yStep)
                        {
                            float sc = ComboScore(screen, lib, x, y, sz, st, nGems, 8);
                            if (sc > best) { best = sc; bx = x; by = y; bsz = sz; bst = st; }
                        }
                    }
                }
            }
            return new[] { bx, by, bsz, bst, best / Math.Max(1, nGems > 3 ? nGems - 1 : nGems) };
        }

        // Build a small library from an explicit list of "sheet|index" keys (the icons we
        // already know are on the bar). Locating with 9 known icons instead of ~2600 is
        // roughly 300x cheaper, which is what makes a full-height sweep affordable.
        public static List<LibIcon> Subset(List<LibIcon> lib, string[] keys)
        {
            var wanted = new HashSet<string>(keys);
            var outp = new List<LibIcon>();
            foreach (var li in lib)
            {
                if (wanted.Contains(li.Sheet + "|" + li.Index))
                {
                    outp.Add(li);
                }
            }
            return outp;
        }

        // Find the icon grid of the spell bar: search icon x, first icon y, icon size, vertical stride.
        public static BarFit FindBar(FloatImg screen, List<LibIcon> lib,
            float xMin, float xMax, float yMin, float yMax,
            float szMin, float szMax, float strMin, float strMax)
        {
            // Phase 0: locate x, y and icon size with the first 3 gems (stride error is
            // negligible over 3 gems, so mid stride is safe). Catches the right basin.
            float mSt = (strMin + strMax) / 2;
            float bx = xMin, by = yMin, bsz = szMin, b0 = -1e9f;
            for (float sz = szMin; sz <= szMax + 1e-4f; sz += 1f)
                for (float x = xMin; x <= xMax + 1e-4f; x += 1f)
                    for (float y = yMin; y <= yMax + 1e-4f; y += 1f)
                    {
                        float sc = ComboScore(screen, lib, x, y, sz, mSt, 3, 8);
                        if (sc > b0) { b0 = sc; bx = x; by = y; bsz = sz; }
                    }

            // Phase 1: around that solution, scan the stride precisely on all 9 gems.
            float bst = mSt, best = -1e9f;
            float px = bx, py = by, psz = bsz;
            for (float st = strMin; st <= strMax + 1e-4f; st += 0.25f)
                for (float sz = psz - 2; sz <= psz + 2.01f; sz += 1f)
                    for (float x = px - 2; x <= px + 2.01f; x += 1f)
                        for (float y = py - 2; y <= py + 2.01f; y += 1f)
                        {
                            float sc = ComboScore(screen, lib, x, y, sz, st, 9, 8);
                            if (sc > best) { best = sc; bx = x; by = y; bsz = sz; bst = st; }
                        }

            // Phase 2: sub-pixel refinement, still on 8px fingerprints (cheap and sufficient).
            float b2 = -1e9f;
            float rx = bx, ry = by, rsz = bsz, rst = bst;
            for (float st = bst - 0.25f; st <= bst + 0.26f; st += 0.125f)
                for (float sz = bsz - 1f; sz <= bsz + 1.01f; sz += 0.5f)
                    for (float x = bx - 0.5f; x <= bx + 0.51f; x += 0.25f)
                        for (float y = by - 0.5f; y <= by + 0.51f; y += 0.25f)
                        {
                            float sc = ComboScore(screen, lib, x, y, sz, st, 9, 8);
                            if (sc > b2) { b2 = sc; rx = x; ry = y; rsz = sz; rst = st; }
                        }

            // Phase 3: final per-gem match with margins.
            var fit = new BarFit { X = rx, Y0 = ry, Scale = rsz, Stride = rst, TotalScore = b2 };
            for (int g = 0; g < 9; g++)
            {
                float iy = ry + g * rst;
                float[] p = screen.Patch(rx, iy, rsz, rsz, 24);
                var m = new GemMatch { Gem = g, IconX = rx, IconY = iy, IconSize = rsz };
                if (Normalize(p))
                {
                    float best3 = -1, second = -1;
                    LibIcon bestIcon = null;
                    foreach (var li in lib)
                    {
                        float s = Dot(p, li.Norm24);
                        if (s > best3) { second = best3; best3 = s; bestIcon = li; }
                        else if (s > second) second = s;
                    }
                    m.Sheet = bestIcon.Sheet;
                    m.Index = bestIcon.Index;
                    m.Score = best3;
                    m.Margin = best3 - second;
                }
                fit.Gems.Add(m);
            }
            return fit;
        }
    }
}
