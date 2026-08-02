# update-spell-icons.ps1
# Extracts the first 9 spell gem icons from the running EverQuest window and saves
# them as clean PNGs (matched against the game's own icon sheets, full quality).
#
# Outputs (in .\icons):
#   spell_1.png .. spell_9.png       40x40 native icons from the game files
#   spell_1@128.png .. spell_9@128   128x128 upscaled versions
#   contact.png                      visual strip: captured gem vs saved icon
#   manifest.json                    per-gem source sheet, index, match score
#
# A calibration cache (barfit.json) makes subsequent runs fast (~2-3 s).
# Delete it to force a full re-search (e.g. after moving the spell bar).

param(
    # Left empty, the game folder is discovered (running process -> registry -> usual paths).
    [string]$GameDir = "",
    [string]$OutDir = (Join-Path $PSScriptRoot "icons"),
    [double]$AcceptScore = 0.80,   # per-gem: below this, fall back to the raw screen crop
    [double]$RunScore = 0.85,      # run-level: average score required to accept a run
    [int]$MaxAttempts = 3,         # captures can hit a bad moment (casting flash, overlay)
    [double]$Hysteresis = 0.05,    # how much better a challenger must be to replace the shown icon
    [double]$ChangeScore = 0.90,   # minimum confidence required to change an icon at all
    # Polling mode: use the cached grid only, one attempt, no full search. A bad moment
    # simply means "skip this cycle" (exit 2) instead of burning ~12 s re-searching.
    [switch]$Quick
)

if ($Quick) {
    $MaxAttempts = 1
}

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# --- Locate the game -------------------------------------------------------
function Test-EqDir($dir) {
    if (-not $dir) { return $false }
    return (Test-Path (Join-Path $dir "eqgame.exe")) -and (Test-Path (Join-Path $dir "uifiles"))
}

function Find-EqDir {
    # 1) The running process is the most reliable source, and it is already running
    #    whenever this script has anything to do.
    $p = Get-Process eqgame -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) {
        try {
            $d = Split-Path $p.Path -Parent
            if (Test-EqDir $d) { return $d }
        } catch { }
    }
    # 2) Uninstall entries (Daybreak / Steam installs register here).
    foreach ($root in @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")) {
        if (-not (Test-Path $root)) { continue }
        foreach ($k in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $props = Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -notmatch "EverQuest") { continue }
            foreach ($cand in @($props.InstallLocation, $props.DisplayIcon)) {
                if (-not $cand) { continue }
                $d = $cand.Trim('"')
                if ($d -match "\.exe$") { $d = Split-Path $d -Parent }
                if (Test-EqDir $d) { return $d }
            }
        }
    }
    # 3) Usual install locations, across all drives.
    $rel = @(
        "Daybreak Game Company\Installed Games\EverQuest Legends",
        "Daybreak Game Company\Installed Games\EverQuest",
        "Sony Online Entertainment\Installed Games\EverQuest",
        "Program Files (x86)\Steam\steamapps\common\EverQuest",
        "EverQuest"
    )
    $bases = @($env:PUBLIC, "$env:SystemDrive\Users\Public", "$env:ProgramFiles", "${env:ProgramFiles(x86)}", "$env:SystemDrive")
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue).Root) { $bases += $drive }
    foreach ($b in ($bases | Where-Object { $_ } | Select-Object -Unique)) {
        foreach ($r in $rel) {
            $d = Join-Path $b $r
            if (Test-EqDir $d) { return $d }
        }
    }
    return $null
}

if (-not $GameDir -or -not (Test-EqDir $GameDir)) {
    $found = Find-EqDir
    if (-not $found) {
        if ($Quick) { Write-Host "EverQuest install not found - skipping."; exit 2 }
        Write-Error "Could not locate the EverQuest installation. Pass -GameDir explicitly."
        exit 1
    }
    $GameDir = $found
}
Write-Host "Game folder: $GameDir"

# --- Compile helpers -------------------------------------------------------
$libSrc = Get-Content (Join-Path $PSScriptRoot "tools\EqIconLib.cs") -Raw
if (-not ("EqIcon.Matcher" -as [type])) {
    $cpar = New-Object System.CodeDom.Compiler.CompilerParameters
    $cpar.CompilerOptions = "/unsafe /optimize"
    [void]$cpar.ReferencedAssemblies.Add("System.dll")
    [void]$cpar.ReferencedAssemblies.Add("System.Core.dll")
    [void]$cpar.ReferencedAssemblies.Add("System.Drawing.dll")
    Add-Type -TypeDefinition $libSrc -CompilerParameters $cpar
}
if (-not ("Win32Cap" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32Cap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

# --- Read the character's UI settings (active skin, spell bar X) -----------
# EQ writes one UI_<character>_<server>.ini per character; the most recently touched
# one belongs to the character currently played.
$uiIni = Get-ChildItem $GameDir -Filter "UI_*.ini" -ErrorAction SilentlyContinue |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
$uiSkin = "default"
$barXPercent = $null
if ($uiIni) {
    $section = ""
    foreach ($line in (Get-Content $uiIni.FullName)) {
        if ($line -match '^\[(.+)\]') { $section = $Matches[1]; continue }
        if ($line -match '^\s*UISkin\s*=\s*(.+?)\s*$') { $uiSkin = $Matches[1] }
        if ($section -eq 'CastSpellWnd' -and $line -match '^\s*XPos\s*=\s*([-\d\.]+)%') {
            $barXPercent = [double]$Matches[1]
        }
    }
    Write-Host "UI settings: $($uiIni.Name)  skin='$uiSkin'"
}

# --- Candidate icon packs --------------------------------------------------
# EQ ships three distinct icon sets (Textures\Alternate 1..3, one of them classic);
# the uifiles\<skin> folders are byte-identical copies of two of them. Which one the
# game draws is a player setting, so list the distinct packs and let the capture decide.
$cachePath = Join-Path $PSScriptRoot "barfit.json"
$cache = $null
if (Test-Path $cachePath) {
    try { $cache = Get-Content $cachePath -Raw | ConvertFrom-Json } catch { $cache = $null }
}

$candidates = @()
foreach ($rel in @("Textures\Alternate 1", "Textures\Alternate 2", "Textures\Alternate 3",
                   "uifiles\$uiSkin", "uifiles\default")) {
    $dir = Join-Path $GameDir $rel
    $probe = Join-Path $dir "Spells01.tga"
    if (Test-Path $probe) {
        $candidates += [PSCustomObject]@{ Dir = $dir; Hash = (Get-FileHash $probe -Algorithm MD5).Hash }
    }
}
$candidates = @($candidates | Group-Object Hash | ForEach-Object { $_.Group[0] })
if ($candidates.Count -eq 0) {
    if ($Quick) { Write-Host "No icon sheets found - skipping."; exit 2 }
    Write-Error "No spell icon sheets found under '$GameDir'."; exit 1
}

function Get-IconLibrary($dir) {
    return [EqIcon.Matcher]::LoadLibrary($dir, "Spells*.tga", 40, 107, 107, 107)
}

# Reuse the pack chosen on a previous run; otherwise start with the first candidate
# and let the pack-selection step below correct it.
$iconDir = $null
if ($cache -and $cache.IconDir -and (Test-Path $cache.IconDir)) { $iconDir = $cache.IconDir }
if (-not $iconDir) { $iconDir = $candidates[0].Dir }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lib = Get-IconLibrary $iconDir
Write-Host ("Icon pack: {0} ({1} icons, {2} ms; {3} distinct pack(s) available)" -f `
    (Split-Path $iconDir -Leaf), $lib.Count, $sw.ElapsedMilliseconds, $candidates.Count)

# --- Capture + locate, with retries (a capture can hit a bad in-game moment:
# casting flash, cooldown grey-out, an overlay on the bar...) ----------------
$proc = Get-Process eqgame -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
    if ($Quick) { Write-Host "EverQuest not running - skipping."; exit 2 }
    Write-Error "EverQuest (eqgame) is not running."; exit 1
}
$hwnd = $proc.MainWindowHandle

$capBmp = $null; $fit = $null; $avg = 0

if ($Quick -and -not (Test-Path $cachePath)) {
    Write-Host "No calibration cache yet - run once without -Quick first."; exit 2
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    if ([Win32Cap]::IsIconic($hwnd)) {
        if ($Quick) { Write-Host "Window minimized - skipping."; exit 2 }
        Write-Error "The EverQuest window is minimized - restore it first."; exit 1
    }
    if ($capBmp) { $capBmp.Dispose() }

    $rect = New-Object Win32Cap+RECT
    [void][Win32Cap]::GetWindowRect($hwnd, [ref]$rect)
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $capBmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($capBmp)
    $hdc = $g.GetHdc()
    [void][Win32Cap]::PrintWindow($hwnd, $hdc, 2)  # PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $screen = [EqIcon.FloatImg]::FromBitmap($capBmp)
    Write-Host "Attempt ${attempt}: captured EverQuest window ($w x $h)"

    # Cached grid first (fast), full search otherwise.
    $fit = $null
    if ($cache) {
        $c = $cache
        $sw.Restart()
        $fit = [EqIcon.Matcher]::FindBar($screen, $lib,
            ($c.X - 1), ($c.X + 1), ($c.Y0 - 1), ($c.Y0 + 1),
            ($c.Size - 1), ($c.Size + 1), ($c.Stride - 0.25), ($c.Stride + 0.25))
        $avg = ($fit.Gems | Measure-Object -Property Score -Average).Average
        Write-Host "  cached grid: avg score $([Math]::Round($avg,3)) ($($sw.ElapsedMilliseconds) ms)"
        if ($avg -lt $RunScore) { $fit = $null }
    }
    if (-not $fit -and -not $Quick) {
        # The bar sits at the far left of the window; XPos in the character's UI file
        # decodes reliably (the vertical position in that file does not, so Y is swept).
        $seedX = if ($barXPercent -ne $null) { [int]($w * $barXPercent / 100.0) } else { 12 }
        $xLo = [Math]::Max(0, $seedX - 25); $xHi = $seedX + 35
        Write-Host ("  locating bar: x in [{0}..{1}], y over full height" -f $xLo, $xHi)

        # Pass 1: sweep with the icons already known to be on the bar. Nine templates
        # instead of ~2600 makes a full-height sweep cheap; it covers "the bar moved".
        $statePathEarly = Join-Path $OutDir "state.json"
        if (Test-Path $statePathEarly) {
            try {
                $st0 = Get-Content $statePathEarly -Raw | ConvertFrom-Json
                $keys = @($st0.gems | ForEach-Object { "$($_.sheet)|$($_.index)" })
                $mini = [EqIcon.Matcher]::Subset($lib, $keys)
                if ($mini.Count -ge 3) {
                    $sw.Restart()
                    $c0 = [EqIcon.Matcher]::FindBarCoarse($screen, $mini,
                        $xLo, $xHi, 1, 0, $h, 1, 26, 46, 1, 39.0, 46.0, 0.5, 9)
                    Write-Host ("  pass 1 (known icons): x={0} y={1} size={2} stride={3} score={4} ({5} ms)" -f `
                        $c0[0], $c0[1], $c0[2], $c0[3], [Math]::Round($c0[4],3), $sw.ElapsedMilliseconds)
                    if ($c0[4] -ge 0.75) {
                        $fit = [EqIcon.Matcher]::FindBar($screen, $lib,
                            ($c0[0]-2), ($c0[0]+2), ($c0[1]-2), ($c0[1]+2),
                            ($c0[2]-2), ($c0[2]+2), ($c0[3]-0.5), ($c0[3]+0.5))
                        $avg = ($fit.Gems | Measure-Object -Property Score -Average).Average
                        Write-Host "  refined: avg score $([Math]::Round($avg,3))"
                        if ($avg -lt $RunScore) { $fit = $null }
                    }
                }
            } catch { }
        }

        # Pass 2: no usable history. Find the bar by its repeating structure (fast), then
        # climb to the topmost gem, because landing mid-bar would shift every icon.
        if (-not $fit) {
            $sw.Restart()
            $per = [EqIcon.Matcher]::FindGridPeriodic($screen, $xLo, ($xHi + 45), 38.0, 47.0, 0.5, 9)
            $py = $per[0]; $ps = $per[1]
            Write-Host ("  pass 2 (periodicity): y={0} stride={1} quality={2} ({3} ms)" -f `
                $py, $ps, [Math]::Round($per[2],3), $sw.ElapsedMilliseconds)

            # Lock x / size / exact y on the first cell of that run. The stride constrains
            # the icon size (an icon nearly fills its cell), and the run start is within
            # half a stride of a cell boundary, so both ranges stay tight.
            $szLo = [int][Math]::Max(20, $ps * 0.72)
            $szHi = [int]($ps * 0.98)
            $fit = [EqIcon.Matcher]::FindBar($screen, $lib,
                $xLo, ($xHi + 20), ($py - $ps/2 - 4), ($py + $ps/2 + 4),
                $szLo, $szHi, ($ps - 1), ($ps + 1))

            # Climb: while a well-matching gem sits one stride higher, that one is the real
            # first gem. Bounded by the 13 gems an EQ spell bar can hold.
            for ($up = 0; $up -lt 13; $up++) {
                $yAbove = $fit.Y0 - $fit.Stride
                if ($yAbove -lt 0) { break }
                $sAbove = [EqIcon.Matcher]::ScoreCell($screen, $lib, $fit.X, $yAbove, $fit.Scale)
                if ($sAbove -lt 0.80) { break }
                Write-Host ("  gem found above (score {0}) - moving up" -f [Math]::Round($sAbove,3))
                $fit = [EqIcon.Matcher]::FindBar($screen, $lib,
                    ($fit.X - 1), ($fit.X + 1), ($yAbove - 1), ($yAbove + 1),
                    ($fit.Scale - 1), ($fit.Scale + 1), ($fit.Stride - 0.5), ($fit.Stride + 0.5))
            }
            $avg = ($fit.Gems | Measure-Object -Property Score -Average).Average
            Write-Host ("  full search: avg score {0} ({1} ms total)" -f [Math]::Round($avg,3), $sw.ElapsedMilliseconds)
        }
    }
    if ($avg -ge $RunScore) { break }
    if ($attempt -lt $MaxAttempts) { Write-Host "  low confidence - retrying in 2 s..."; Start-Sleep -Seconds 2 }
}

if ($avg -lt $RunScore) {
    if ($Quick) {
        # Bad moment (casting, cooldown, overlay): keep the current icons, retry next cycle.
        $capBmp.Dispose()
        Write-Host ("Low confidence ({0}) - skipping this cycle." -f [Math]::Round($avg,2))
        exit 2
    }
    $capBmp.Save((Join-Path $PSScriptRoot "debug-capture.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $capBmp.Dispose()
    Write-Error ("Could not read the spell bar with confidence (avg score {0}). Previous icons kept. Debug capture saved next to this script." -f [Math]::Round($avg,2))
    exit 1
}

# --- Pick the icon pack the game is actually drawing ------------------------
# Only on a full run: score every distinct pack against the gems we just located and
# keep the best. The winner is remembered, so polling loads a single pack.
if (-not $Quick -and $candidates.Count -gt 1) {
    $bestDir = $iconDir
    $bestScore = [EqIcon.Matcher]::ScorePack($screen, $lib, $fit)
    foreach ($cand in $candidates) {
        if ($cand.Dir -eq $iconDir) { continue }
        $trial = Get-IconLibrary $cand.Dir
        $score = [EqIcon.Matcher]::ScorePack($screen, $trial, $fit)
        Write-Host ("  pack {0}: {1}" -f (Split-Path $cand.Dir -Leaf), [Math]::Round($score, 3))
        if ($score -gt $bestScore + 0.01) { $bestScore = $score; $bestDir = $cand.Dir; $lib = $trial }
    }
    if ($bestDir -ne $iconDir) {
        Write-Host ("Switching icon pack to {0} (score {1})" -f (Split-Path $bestDir -Leaf), [Math]::Round($bestScore,3))
        $iconDir = $bestDir
        # Re-match the gems against the winning pack before saving anything.
        $fit = [EqIcon.Matcher]::FindBar($screen, $lib,
            ($fit.X - 0.5), ($fit.X + 0.5), ($fit.Y0 - 0.5), ($fit.Y0 + 0.5),
            ($fit.Scale - 0.5), ($fit.Scale + 0.5), ($fit.Stride - 0.25), ($fit.Stride + 0.25))
        $avg = ($fit.Gems | Measure-Object -Property Score -Average).Average
        Write-Host "  re-matched: avg score $([Math]::Round($avg,3))"
    }
}

# Success: only now do we trust and persist the grid.
@{ X = $fit.X; Y0 = $fit.Y0; Size = $fit.Scale; Stride = $fit.Stride; IconDir = $iconDir } |
    ConvertTo-Json | Set-Content $cachePath -Encoding utf8
Write-Host "Grid: X=$($fit.X) Y0=$($fit.Y0) Size=$($fit.Scale) Stride=$($fit.Stride)"

# --- Save icons ------------------------------------------------------------
if (-not (Test-Path $OutDir)) { [void](New-Item -ItemType Directory -Path $OutDir) }

# Write only when the bytes actually differ: rewriting identical files would wake the
# plugin's file watcher and force a pointless key redraw on every polling cycle.
$script:Changed = 0
function Save-IfChanged($bitmap, $path) {
    $ms = New-Object System.IO.MemoryStream
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $new = $ms.ToArray()
    $ms.Dispose()
    if (Test-Path $path) {
        $old = [System.IO.File]::ReadAllBytes($path)
        if ($old.Length -eq $new.Length) {
            $same = $true
            for ($i = 0; $i -lt $new.Length; $i++) {
                if ($old[$i] -ne $new[$i]) { $same = $false; break }
            }
            if ($same) { return $false }
        }
    }
    [System.IO.File]::WriteAllBytes($path, $new)
    $script:Changed++
    return $true
}

# Previous per-gem choice, used for the stickiness rule above. Kept in its own file so
# writing it never disturbs the plugin's watcher (it only listens for spell_*.png).
$statePath = Join-Path $OutDir "state.json"
$prevChoice = @{}
if (Test-Path $statePath) {
    try {
        $st = Get-Content $statePath -Raw | ConvertFrom-Json
        foreach ($e in $st.gems) { $prevChoice["$($e.gem)"] = $e }
    } catch { $prevChoice = @{} }
}
$libIndex = @{}
foreach ($li in $lib) { $libIndex["$($li.Sheet)|$($li.Index)"] = $li }

$manifest = @()
$sheets = @{}
$nn = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$half = [System.Drawing.Drawing2D.PixelOffsetMode]::Half

# contact strip: captured gem | saved icon
$cs = 80
$contact = New-Object System.Drawing.Bitmap (($cs*2 + 15), ($cs*9 + 10))
$cg = [System.Drawing.Graphics]::FromImage($contact)
$cg.Clear([System.Drawing.Color]::FromArgb(30,30,30))
$cg.InterpolationMode = $nn; $cg.PixelOffsetMode = $half

foreach ($m in $fit.Gems) {
    $n = $m.Gem + 1

    # A gem being recast is drawn with a progress overlay, so its match wanders between
    # near-identical library icons with a tiny margin. Stick to the icon already shown
    # unless the new candidate is clearly better: score the previous choice against this
    # very capture and only switch if the challenger beats it by more than $Hysteresis.
    $chosenSheet = $m.Sheet; $chosenIndex = $m.Index; $chosenScore = $m.Score; $sticky = $false
    $p = $prevChoice["$n"]
    if ($p) {
        $key = "$($p.sheet)|$($p.index)"
        if ($libIndex.ContainsKey($key)) {
            $patch = $screen.Patch($m.IconX, $m.IconY, $m.IconSize, $m.IconSize, 24)
            if ([EqIcon.Matcher]::Normalize($patch)) {
                $prevScore = [EqIcon.Matcher]::Dot($patch, $libIndex[$key].Norm24)
                # Keep the current icon if it still explains the capture almost as well,
                # or if the challenger is not confident enough to justify a change.
                if (($prevScore -ge ($m.Score - $Hysteresis)) -or ($m.Score -lt $ChangeScore)) {
                    $chosenSheet = $p.sheet; $chosenIndex = $p.index; $chosenScore = $prevScore
                    $sticky = $true
                }
            }
        }
    }
    $ok = $chosenScore -ge $AcceptScore

    # Nothing trustworthy to show and something is already on screen: leave it alone.
    if (-not $ok -and (Test-Path (Join-Path $OutDir "spell_$n.png"))) {
        Write-Host ("Gem {0}: low score {1} - keeping current icon" -f $n, [Math]::Round($chosenScore,3))
        $manifest += [PSCustomObject]@{
            gem = $n; status = "kept"; score = [Math]::Round($chosenScore, 4); margin = [Math]::Round($m.Margin, 4)
            sheet = $chosenSheet; index = $chosenIndex
        }
        continue
    }

    # 40x40 icon bitmap: matched -> crop from source sheet; unmatched -> screen crop fallback
    $icon40 = New-Object System.Drawing.Bitmap 40, 40
    $ig = [System.Drawing.Graphics]::FromImage($icon40)
    $ig.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $ig.PixelOffsetMode = $half
    if ($ok) {
        if (-not $sheets.ContainsKey($chosenSheet)) { $sheets[$chosenSheet] = [EqIcon.Tga]::Load($chosenSheet) }
        $sheet = $sheets[$chosenSheet]
        $cols = [int]($sheet.Width / 40)
        $sx = ($chosenIndex % $cols) * 40; $sy = [int][Math]::Floor($chosenIndex / $cols) * 40
        $ig.DrawImage($sheet, (New-Object System.Drawing.Rectangle 0, 0, 40, 40), $sx, $sy, 40, 40, [System.Drawing.GraphicsUnit]::Pixel)
    } else {
        $ig.DrawImage($capBmp, (New-Object System.Drawing.Rectangle 0, 0, 40, 40),
            [int][Math]::Round($m.IconX), [int][Math]::Round($m.IconY),
            [int][Math]::Ceiling($m.IconSize), [int][Math]::Ceiling($m.IconSize), [System.Drawing.GraphicsUnit]::Pixel)
    }
    $ig.Dispose()
    [void](Save-IfChanged $icon40 (Join-Path $OutDir "spell_$n.png"))

    # 128x128 upscale (nearest neighbor keeps the retro pixel look crisp)
    $icon128 = New-Object System.Drawing.Bitmap 128, 128
    $bg = [System.Drawing.Graphics]::FromImage($icon128)
    $bg.InterpolationMode = $nn; $bg.PixelOffsetMode = $half
    $bg.DrawImage($icon40, 0, 0, 128, 128)
    $bg.Dispose()
    [void](Save-IfChanged $icon128 (Join-Path $OutDir "spell_$n@128.png"))

    # contact strip row
    $y = 5 + $m.Gem * $cs
    $cg.DrawImage($capBmp, (New-Object System.Drawing.Rectangle 5, $y, ($cs-8), ($cs-8)),
        [int][Math]::Round($m.IconX), [int][Math]::Round($m.IconY),
        [int][Math]::Ceiling($m.IconSize), [int][Math]::Ceiling($m.IconSize), [System.Drawing.GraphicsUnit]::Pixel)
    $cg.DrawImage($icon40, (New-Object System.Drawing.Rectangle ($cs+10), $y, ($cs-8), ($cs-8)),
        0, 0, 40, 40, [System.Drawing.GraphicsUnit]::Pixel)
    $icon128.Dispose(); $icon40.Dispose()

    $status = if (-not $ok) { "fallback-crop" } elseif ($sticky) { "sticky" } else { "matched" }
    if (-not $Quick) {
        Write-Host ("Gem {0}: {1}  score={2}  ({3} #{4})" -f $n, $status, [Math]::Round($chosenScore,3), (Split-Path $chosenSheet -Leaf), $chosenIndex)
    }
    $manifest += [PSCustomObject]@{
        gem = $n; status = $status; score = [Math]::Round($chosenScore, 4); margin = [Math]::Round($m.Margin, 4)
        sheet = $chosenSheet; index = $chosenIndex
    }
}
$cg.Dispose()
foreach ($s in $sheets.Values) { $s.Dispose() }
$capBmp.Dispose()

# The contact strip and manifest are diagnostics for manual runs. The strip embeds the
# live capture, so it differs every time - never write it while polling.
if (-not $Quick) {
    [void](Save-IfChanged $contact (Join-Path $OutDir "contact.png"))
    [PSCustomObject]@{
        generatedAt = (Get-Date).ToString("o")
        window = "$w x $h"
        grid = @{ X = $fit.X; Y0 = $fit.Y0; Size = $fit.Scale; Stride = $fit.Stride }
        gems = $manifest
    } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir "manifest.json") -Encoding utf8
}
$contact.Dispose()

# Remember what is on screen so the next run can stay on it (see the stickiness rule).
[PSCustomObject]@{ gems = $manifest } | ConvertTo-Json -Depth 4 | Set-Content $statePath -Encoding utf8

Write-Host "Done. $($script:Changed) file(s) updated in $OutDir"
