# EFT Live Log Capture Runbook

This runbook records the Windows procedure used on 2026-08-09 to launch Escape
from Tarkov through BSG Launcher, exercise a small UI transition, and inspect the
new application log. Use it when a parser decision needs evidence from a current
EFT client rather than an old fixture.

The findings from that capture are recorded separately in
[eft-1-1-profile-selection-log-analysis.md](eft-1-1-profile-selection-log-analysis.md).

The captured session used EFT `1.1.0.0.46657`. Paths and UI coordinates are
machine-specific and must be rediscovered on later runs.

## Safety boundary

- Keep the capture to menus unless the question specifically requires a raid. Do
  not enter matchmaking for profile-selection or session-mode evidence.
- Record the profile that is active before the test and restore it before exit.
- Never commit a complete EFT or launcher log. EFT logs contain profile and
  account ids, server addresses, session ids, and other account-specific data.
- Do not print or commit the complete BSG Launcher `settings` file. It can contain
  authentication data. Read only the property needed for path discovery.
- Redact at least `ProfileId`, `AccountId`, IP/port, `Sid`, `shortId`, nicknames,
  and tokens before copying evidence into a document or test fixture.
- Prefer menu-driven exit. Force-stop a process only after a graceful exit was
  requested, the exact process ids were reviewed, and the game is confirmed to
  be unresponsive. Never stop the `BEService` system service as cleanup.

## 1. Discover the installation and establish a baseline

Start by checking whether the launcher or game is already running:

```powershell
Get-Process |
    Where-Object { $_.ProcessName -match 'EscapeFromTarkov|BsgLauncher|Tarkov' } |
    Select-Object Id, ProcessName, Responding, Path
```

BSG Launcher settings provide a useful hint, but `gamesRootDir` may be stale. Do
not output the entire settings object:

```powershell
$launcherSettingsPath = Join-Path $env:APPDATA 'Battlestate Games\BsgLauncher\settings'
$launcherSettings = Get-Content -Raw -LiteralPath $launcherSettingsPath |
    ConvertFrom-Json
$launcherSettings | Select-Object gamesRootDir
```

If that path is stale, search launcher logs for path-only evidence:

```powershell
$launcherLogRoot = Join-Path $env:LOCALAPPDATA 'Battlestate Games\BsgLauncher\Logs'
rg -n -i 'gameRootDir|EscapeFromTarkov\.exe|Escape from Tarkov|installation path|game path' `
    $launcherLogRoot -g '*.log'
```

Set variables only after reviewing the result. In the 2026-08-09 capture the
resolved roots were `G:\Games\Escape from Tarkov` and its `Logs` child, but these
are examples rather than defaults:

```powershell
$eftRoot = 'G:\Games\Escape from Tarkov' # Replace with the discovered path.
$logRoot = Join-Path $eftRoot 'Logs'

$beforeLaunch = Get-ChildItem -LiteralPath $logRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
$beforeLaunch | Select-Object Name, FullName, LastWriteTime
```

Older installations or application code may refer to `build\Logs` or the EFT
folder under `%LOCALAPPDATA%`. Treat the newest directory that the launched
client actually creates as authoritative.

## 2. Launch through BSG Launcher

Launch or restore BSG Launcher and use its **Play** button. Do not start the game
executable directly because the launcher owns authentication and startup checks.
Starting the launcher with a visible window is appropriate for this interactive
step:

```powershell
Start-Process -FilePath 'G:\Games\BsgLauncher\BsgLauncher.exe'
```

The 2026-08-09 capture used Orca computer control because the launcher was
already running in the notification area. The useful inspection sequence was:

```powershell
orca status --json
orca computer capabilities --json
orca computer list-apps --json
orca computer list-windows --app BsgLauncher --json
orca computer get-app-state --app BsgLauncher --restore-window --json
```

Launcher and EFT webviews exposed little or no accessible control hierarchy, so
the Play button and profile cards required a coordinate click based on the latest
screenshot:

```text
action_x = screenshot_pixel_x / screenshot.scale
action_y = screenshot_pixel_y / screenshot.scale
```

```powershell
orca computer click --app BsgLauncher --x <action-x> --y <action-y> --json
```

Always capture fresh state after a UI transition. A click can launch the game
after a delay even when the launcher screenshot initially looks unchanged, so
verify the result from the process list and filesystem rather than clicking
again immediately.

Wait for a new session directory and application log:

```powershell
Get-Process |
    Where-Object { $_.ProcessName -match '^EscapeFromTarkov' } |
    Select-Object Id, ProcessName, Responding, StartTime

$captureDir = Get-ChildItem -LiteralPath $logRoot -Directory |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$applicationLog = Get-ChildItem -LiteralPath $captureDir.FullName -File `
    -Filter '*application*.log' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$captureDir | Select-Object Name, FullName, CreationTime, LastWriteTime
$applicationLog | Select-Object Name, Length, LastWriteTime
```

Confirm that `$captureDir.FullName` differs from `$beforeLaunch.FullName` before
using it as evidence.

## 3. Exercise one controlled transition at a time

Write down the starting screen and active profile. Make one UI change, wait for
the log write, and then inspect only the relevant patterns:

```powershell
Select-String -LiteralPath $applicationLog.FullName `
    -Pattern 'Session mode:|PrepareSelectedProfileLocally|CompleteSelectedProfile|SelectProfile' |
    Select-Object LineNumber, Line
```

For the seasonal-profile capture, the controlled order was:

```text
PvE Zone (initial) -> PvP Season -> PvP Zone -> PvE Zone (restored)
```

No raid or matchmaking was started. EFT `1.1.0.0.46657` produced this redacted
evidence:

```text
Session mode: PvpSeason
PrepareSelectedProfileLocally ProfileId:<season-pmc-id> AccountId:<account-id>
CompleteSelectedProfile ProfileId:<season-pmc-id> AccountId:<account-id>

Session mode: Regular
PrepareSelectedProfileLocally ProfileId:<permanent-pvp-pmc-id> AccountId:<account-id>
CompleteSelectedProfile ProfileId:<permanent-pvp-pmc-id> AccountId:<account-id>
```

`CompleteSelectedProfile` is the authoritative completion signal in this client.
`PrepareSelectedProfileLocally` can appear when the selector is opened or before
an interrupted switch, so it is not sufficient evidence on its own. Continue to
support legacy `SelectProfile` lines when building fixtures.

Use exact token boundaries when testing session-mode parsing. An unbounded
`(Pve|Pvp|Regular)` alternative matches the `Pvp` prefix of `PvpSeason`:

```regex
Session mode:\s*(Pve|PvpSeason|Pvp|Regular)\s*$
```

## 4. Summarize without exposing identifiers

The following check reports modes, counts, and identity relationships without
printing the captured ids:

```powershell
$lines = Get-Content -LiteralPath $applicationLog.FullName

$modes = @($lines | ForEach-Object {
    if ($_ -match 'Session mode:\s*(\S+)\s*$') { $Matches[1] }
})

$completed = @($lines | ForEach-Object {
    if ($_ -match 'CompleteSelectedProfile ProfileId:([a-f0-9]+) AccountId:(\d+)') {
        [pscustomobject]@{
            ProfileId = $Matches[1]
            AccountId = $Matches[2]
        }
    }
})

$expectedModes = @('Pve', 'PvpSeason', 'Regular', 'Pve')
[pscustomobject]@{
    ModeSequence = $modes -join ' -> '
    ExpectedModeSequence = (($modes -join '|') -eq ($expectedModes -join '|'))
    CompletedProfileCount = $completed.Count
    AllCompletedIdsDistinct =
        (@($completed.ProfileId | Sort-Object -Unique).Count -eq $completed.Count)
    OneAccountAcrossProfiles =
        (@($completed.AccountId | Sort-Object -Unique).Count -eq 1)
}
```

For the 2026-08-09 sequence, the expected checks were true, three completed
profile ids were seen, all three were distinct, and they belonged to one account.
Only minimal redacted lines should become repository fixtures. Keep the raw log
outside the repository and delete any temporary copy when the investigation is
finished.

## 5. Restore state and exit

Return to the profile/mode selector, choose the profile recorded at the start,
and confirm both its `Session mode` line and completed profile selection in the
log. Then use the in-game exit menu and confirm the exit dialog.

If the game remains after a reasonable wait, inspect it before taking action:

```powershell
Get-Process |
    Where-Object { $_.ProcessName -match '^EscapeFromTarkov' } |
    Select-Object Id, ProcessName, Responding, Path
```

Only after reviewing that table, copy the exact hung game and anti-cheat wrapper
process ids into a separate list, verify them a second time, and stop those ids:

```powershell
$verifiedGameProcessIds = @(<game-pid>, <wrapper-pid>)
Get-Process -Id $verifiedGameProcessIds -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, Responding, Path

Get-Process -Id $verifiedGameProcessIds -ErrorAction SilentlyContinue |
    Stop-Process -Force
```

Do not include BSG Launcher or `BEService` in that list. Finally, confirm that no
EFT game process remains:

```powershell
Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match '^EscapeFromTarkov' }
```

## Capture record

Add these facts to the relevant decision document or investigation notes:

- Capture date, EFT version, and session-directory name
- Question being answered and exact UI transition order
- Whether matchmaking or a raid was entered
- Starting profile and confirmation that it was restored
- Relevant exact tokens and minimal redacted log lines
- Parser behavior before and after the new evidence
- Confirmation that raw logs and screenshots were not committed
- Exit result, including whether exact game processes required force-stop

See [eft-log-patterns.md](eft-log-patterns.md) for the broader application and
network log format reference.
