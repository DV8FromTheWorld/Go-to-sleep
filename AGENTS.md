# Agent guide

Go To Sleep is a Windows bedtime lock (see README.md for what it does from the user's side). The owner uses it on their own gaming PC to stop staying up late. It should be hard to get around on a whim but never impossible, and never risky to the PC.

## Safety: read this before running anything

- **Never run `install.ps1` or the release exe (`bin\GoToSleep.exe` with no arguments) on the dev machine unless the user asks.** The real install locks the screen at bedtime, relaunches itself every minute, and has no exit button. If it runs at the wrong time, the user is locked out until wake time.
- **Use `debug-run.ps1` for anything interactive.** The debug build has a "DEBUG: Exit" button, auto-exits after 15 minutes, and uses `bin\debug-data` instead of the real `C:\ProgramData\GoToSleep`. Even so, it locks the screen 2 minutes after you run it, so only start it when the user wants to watch.
- **Safe to run anytime:** `build.ps1 -Test`, `GoToSleep.exe --preview` (Esc closes it), `--status`, and `--can-uninstall`.
- **The media tests are manual.** `Media.PausePlaying()` really pauses whatever the user is listening to, so don't call it from automated tests.

## Build

```powershell
.\build.ps1 -Test -DebugBuild
```

This uses `csc.exe` from .NET Framework 4.x (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`), which ships with Windows. There's no SDK, NuGet or project file, and that's on purpose: keep it buildable on a bare Windows install. The constraints that follow from that:

- **C# 5 only.** No `$"..."` strings, `?.`, `nameof`, `=>` members, `out var`, tuples, auto-property initializers or `using static`. Optional parameters, lambdas, LINQ and object initializers are fine.
- **ASCII-only source.** Without a BOM, csc reads files in the ANSI codepage, so write `"\u00D7"`, not `"×"`.
- **WinRT (`Windows.Media.Control`)** is referenced from `C:\Windows\System32\WinMetadata\*.winmd` plus the `System.Runtime.dll` facade. `AsTask()`/`await` on WinRT async calls need the Windows SDK's `Windows.winmd`, which isn't installed, so `Media.cs` waits on `IAsyncOperation` itself. Keep it that way.
- **Debug-only code** goes inside `#if DEBUG`. `build.ps1 -DebugBuild` defines it; the release build must never contain an escape hatch. To check, search the release exe for the debug strings, e.g. `DEBUG: Exit` (UTF-16, at both byte alignments).

## Layout

| File | What's in it |
|---|---|
| `src/Core.cs` | `Paths`, `Log`, `Ini`, `Config`, `State` (saved to `state.ini`), `Schedule` (date math), `MonotonicClock` (time can't go backwards), and `Engine`: all the rules |
| `src/Program.cs` | `Main` (modes `--preview`, `--status`, `--can-uninstall`), `Fmt`, and `Controller`: the 500 ms tick that drives the lock UI, popups, tray, media and window handling |
| `src/UI.cs` | `LockForm` (one per monitor; only the primary has content), `Popup` (corner box that doesn't take focus), `Tray`, `Theme`, `FlatButton` (can't take keyboard focus, so a stray Enter can't shut the PC down) |
| `src/Native.cs` | P/Invoke: focus, topmost, minimize/restore, exclusive-fullscreen detection, `KeyboardBlocker`, `TaskManagerPolicy`, `Power`, `Audio` (Core Audio mute) |
| `src/Media.cs` | Pause and resume through Windows' media controls, on a background queue |
| `tests/Tests.cs` | A console test runner (`/main:GoToSleep.Tests`): no framework, just `Check(name, bool)` |

## The rules (Engine)

- **A session is one day,** from wake time to the next wake time. `State.SessionDate` is the day it started. Bedtime, wake time, the 60-second grace and the borrow allowance are copied from the config when a session starts, so config edits only ever take effect tomorrow.
- **Locked** means `BedtimeLocal <= now < LockEndLocal` and no extension is running (`UnlockKind` is `""`, `"grace"` or `"borrow"`).
- **Borrowing:**
  - Tonight's pool is `PoolTotalSeconds = (BorrowMinutes - DebtMinutes) * 60`. `DebtMinutes` is last night's borrowing, which also moves tonight's bedtime earlier by the same amount.
  - `BorrowedSeconds` is tonight's total. It carries into tomorrow as `ceil(seconds / 60)`, rounded once for the whole night, not per stretch.
  - A stretch is capped at wake time. That's fine, because only time actually used is charged.
- **Day rollover:** the new session is the day we're in under the current wake time, moved one day later if its night would overlap the lock that just ended. Don't use "previous day + 1": that skips a night when the wake time moves later. Debt carries only into the first night after it was borrowed; if a whole night went by with the PC off, the debt is dropped.
- **Extensions survive a restart of the process within the same boot,** detected with `GetTickCount64`. After a reboot they end, and only the time actually used is charged.
- **Time comes from `MonotonicClock`,** which never goes back by more than 60 s, even across restarts (it uses `LastSeenUtc`). Don't use `DateTime.Now` for rule decisions.

## Controller behaviour to keep

- **Unlocking** goes through `EnsureUnlocked(resume)`. `resume` is true only for extensions (and the debug exit): then media resumes and displaced windows are restored. A morning unlock must **not** auto-resume media or pop a game back up.
- **Only minimize exclusive-fullscreen windows** (`Native.ExclusiveFullscreenRunning()`). Other windows stay behind the topmost lock.
- **Never block, freeze or slow the UI thread.** The low-level keyboard hook runs on it, and Windows silently drops slow hooks; the hook is re-installed every 30 s while locked. Anything slow goes to a background queue, like `Media`.
- **Every popup must be dismissable,** and the tray must expose anything a dismissed popup offered.
- **Lock forms only allow closing** when `AllowClose` is set or Windows is shutting down. Don't add other ways out in release builds.

## Scheduled task (install.ps1)

The every-minute relaunch must come from a **time trigger** starting at install time. A logon trigger's repetition only starts at the next logon, so installing while logged in (the normal case) would leave nothing to relaunch a killed process. The logon trigger is only there to start the app the moment you log in.

## Changing things

- **Rule changes:** add or adjust a scenario in `tests/Tests.cs`. Tests build local times with `L(day, h, m)` and convert with `U(...)`.
- **New saved fields:** add them to both `State.Load` and `State.Save`. Missing keys load as zero or empty, so old state files keep working.
- **User-facing text** lives in `Controller.BuildView`, `UpdateOverlay` and `UpdateTray`. Keep it plain and short; the lock screen is read half-asleep.
- **Update README.md** when behaviour changes.
