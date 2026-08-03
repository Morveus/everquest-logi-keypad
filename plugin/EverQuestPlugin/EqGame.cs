namespace Loupedeck.EverQuestPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;

    // Everything that talks to the EverQuest installation and its window.
    internal static class EqGame
    {
        [DllImport("user32.dll")] private static extern Boolean GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern Boolean PrintWindow(IntPtr hWnd, IntPtr hdc, UInt32 flags);
        [DllImport("user32.dll")] private static extern Boolean IsIconic(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public Int32 Left, Top, Right, Bottom; }

        private const UInt32 PW_RENDERFULLCONTENT = 2;

        // --- Window ------------------------------------------------------------

        public static IntPtr FindWindow()
        {
            var found = IntPtr.Zero;
            try
            {
                // Process objects hold OS handles; polling every few seconds without
                // disposing them leaks thousands of handles a day.
                foreach (var p in Process.GetProcessesByName("eqgame"))
                {
                    using (p)
                    {
                        if (found == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero)
                        {
                            found = p.MainWindowHandle;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Falls through to "not running".
            }
            return found;
        }

        public static Boolean IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

        // Passive capture: works on the DirectX surface and does not require focus.
        public static Bitmap Capture(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r))
            {
                return null;
            }
            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384)
            {
                return null;
            }

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var ok = false;
            using (var g = Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try
                {
                    ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            // A failed PrintWindow leaves an all-black bitmap. Reporting that as a
            // successful capture makes "the game did not render" indistinguishable from
            // "nothing changed", and the plugin would sit on stale icons forever.
            if (!ok)
            {
                bmp.Dispose();
                return null;
            }
            return bmp;
        }

        // --- Installation ------------------------------------------------------

        private static Boolean LooksLikeEqDir(String dir) =>
            !String.IsNullOrEmpty(dir) &&
            File.Exists(Path.Combine(dir, "eqgame.exe")) &&
            Directory.Exists(Path.Combine(dir, "uifiles"));

        public static String FindGameDir()
        {
            // 1. The running process is the most reliable source, and it is running
            //    whenever there is anything to read.
            try
            {
                foreach (var p in Process.GetProcessesByName("eqgame"))
                {
                    var d = Path.GetDirectoryName(p.MainModule?.FileName);
                    if (LooksLikeEqDir(d))
                    {
                        return d;
                    }
                }
            }
            catch (Exception)
            {
                // Access to MainModule can be denied; fall through.
            }

            // 2. Uninstall entries.
            foreach (var root in new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            })
            {
                foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
                {
                    try
                    {
                        using (var key = hive.OpenSubKey(root))
                        {
                            if (key == null) { continue; }
                            foreach (var sub in key.GetSubKeyNames())
                            {
                                using (var k = key.OpenSubKey(sub))
                                {
                                    var name = k?.GetValue("DisplayName") as String;
                                    if (name == null || name.IndexOf("EverQuest", StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                                    foreach (var v in new[] { "InstallLocation", "DisplayIcon" })
                                    {
                                        var raw = (k.GetValue(v) as String)?.Trim('"');
                                        if (String.IsNullOrEmpty(raw)) { continue; }
                                        var d = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(raw) : raw;
                                        if (LooksLikeEqDir(d)) { return d; }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Registry access issues are not fatal.
                    }
                }
            }

            // 3. Usual install locations across all fixed drives.
            var rel = new[]
            {
                @"Daybreak Game Company\Installed Games\EverQuest Legends",
                @"Daybreak Game Company\Installed Games\EverQuest",
                @"Sony Online Entertainment\Installed Games\EverQuest",
                @"Program Files (x86)\Steam\steamapps\common\EverQuest",
                "EverQuest",
            };
            var bases = new List<String>
            {
                Environment.GetEnvironmentVariable("PUBLIC"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed) { bases.Add(d.RootDirectory.FullName); }
                }
            }
            catch (Exception) { }

            foreach (var b in bases)
            {
                if (String.IsNullOrEmpty(b)) { continue; }
                foreach (var r in rel)
                {
                    try
                    {
                        var d = Path.Combine(b, r);
                        if (LooksLikeEqDir(d)) { return d; }
                    }
                    catch (Exception) { }
                }
            }
            return null;
        }

        // --- Character UI settings --------------------------------------------

        public sealed class UiSettings
        {
            public String Skin = "default";
            public Double? BarXPercent;   // CastSpellWnd/XPos, reliable
            public String IniName = "";
        }

        // EQ writes one UI_<character>_<server>.ini per character; the most recently
        // touched one belongs to whoever is playing now.
        public static UiSettings ReadUiSettings(String gameDir)
        {
            var s = new UiSettings();
            try
            {
                FileInfo newest = null;
                foreach (var f in new DirectoryInfo(gameDir).GetFiles("UI_*.ini"))
                {
                    if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) { newest = f; }
                }
                if (newest == null) { return s; }
                s.IniName = newest.Name;

                var section = "";
                foreach (var line in File.ReadAllLines(newest.FullName))
                {
                    var t = line.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Trim('[', ']'); continue; }
                    var eq = t.IndexOf('=');
                    if (eq <= 0) { continue; }
                    var key = t.Substring(0, eq).Trim();
                    var val = t.Substring(eq + 1).Trim();

                    if (String.Equals(key, "UISkin", StringComparison.OrdinalIgnoreCase) && val.Length > 0)
                    {
                        s.Skin = val;
                    }
                    else if (section == "CastSpellWnd" && String.Equals(key, "XPos", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Double.TryParse(val.TrimEnd('%'), System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out var d))
                        {
                            s.BarXPercent = d;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Defaults are fine if the file cannot be read.
            }
            return s;
        }

        // --- Icon packs --------------------------------------------------------

        // EQ ships three distinct icon sets under Textures\Alternate 1..3; the
        // uifiles\<skin> folders are byte-identical copies of two of them. Return the
        // distinct ones, deduplicated by the content of their first sheet.
        public static List<String> FindIconPacks(String gameDir, String skin)
        {
            var candidates = new List<String>
            {
                Path.Combine(gameDir, @"Textures\Alternate 1"),
                Path.Combine(gameDir, @"Textures\Alternate 2"),
                Path.Combine(gameDir, @"Textures\Alternate 3"),
                Path.Combine(gameDir, "uifiles", skin ?? "default"),
                Path.Combine(gameDir, @"uifiles\default"),
            };

            var seen = new HashSet<String>();
            var result = new List<String>();
            foreach (var dir in candidates)
            {
                var probe = Path.Combine(dir, "Spells01.tga");
                if (!File.Exists(probe)) { continue; }
                String hash;
                try
                {
                    using (var md5 = MD5.Create())
                    using (var fs = File.OpenRead(probe))
                    {
                        hash = BitConverter.ToString(md5.ComputeHash(fs));
                    }
                }
                catch (Exception) { continue; }
                if (seen.Add(hash)) { result.Add(dir); }
            }
            return result;
        }
    }
}
