# Go To Sleep

A bedtime lock for Windows, for people who stay up too late and have already beaten every browser extension.

At bedtime, every monitor gets covered by a lock screen that stays up until wake time. It's built to be **hard to get around on a whim**. The point isn't to make it impossible; it's to make staying up a deliberate choice instead of a reflex.

<img alt="screenshot" src="https://github.com/user-attachments/assets/999b1f8a-35c1-4f26-b1a2-f35d61464776" />


## How a night goes

1. **Warnings.** Popups (with a sound) 30, 15, 5 and 1 minute before bedtime, then a countdown for the last 60 seconds. Each one can be dismissed with ×.
2. **Lock.** At bedtime the lock screen covers every monitor.
   - **What gets paused:** media (Spotify, YouTube in a browser, anything in Windows' media controls) is paused and audio is muted.
   - **Fullscreen games:** a game in exclusive fullscreen is minimized, because nothing can draw over it. Other windows just sit behind the lock.
3. **Your options on the lock screen:**
   - **Shut down.** You'll still be locked when you log back in.
   - **Sleep.** Still locked when the PC wakes.
   - **60 seconds to wrap up.** Once a night. Pause the video, save the game.
   - **Borrow time from tomorrow.** See below.
4. **Morning.** The lock ends at wake time. Nothing auto-resumes, so music doesn't start playing when you wake the PC.

If you take the 60 seconds or borrowed time, what was paused resumes and the window you were using comes back. A corner countdown has an **I'm done, lock now** button. The moon icon in the tray has the same status and button if you dismiss the countdown; click the icon to bring the countdown back.

### Borrowing from tomorrow

Each night has a **30-minute pool, minus whatever you're paying back from last night**. Borrow in as many stretches as you like; "I'm done" relocks, and what's left stays available. Every minute you use (counted to the second, rounded up once for the night) moves tomorrow's bedtime earlier *and* shrinks tomorrow's pool.

With a 1:00 AM bedtime, borrowing everything allowed each night:

| Night | Paying back | Lock | Pool | Latest you're up |
|---|---|---|---|---|
| 1 | 0 | 1:00 | 30 | 1:20 (borrowed 20) |
| 2 | 20 | 12:40 | 10 | 12:50 |
| 3 | 10 | 12:50 | 20 | 1:10 |

A late night is always followed by an earlier one. You can never get past bedtime + 30, and you can't average later than your bedtime. The lock screen spells this out: why it locked early, what's in tonight's pool, and what tomorrow will look like.

## Install

From an **admin** PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

It builds the app, asks for a bedtime and wake time, copies it to `C:\Program Files\GoToSleep`, and registers a scheduled task that keeps it running. Re-run it any time to update; your settings and tonight's state are kept.

> **Your first night starts immediately.** If you install with a bedtime that's already passed, you're locked until wake time. To see it for real first, install with times a few minutes apart (`.\install.ps1 -Bedtime 23:05 -WakeTime 23:10`), then re-run with your real times.

## Settings

`C:\ProgramData\GoToSleep\config.ini` (edit from an admin terminal: `notepad C:\ProgramData\GoToSleep\config.ini`).

| Key | Default | Meaning |
|---|---|---|
| `Bedtime` | `01:00` | When the lock starts (`23:30`, `1:00 AM`, … all work) |
| `WakeTime` | `07:00` | When it ends |
| `WarnMinutes` | `30,15,5,1` | Warning popups, in minutes before bedtime |
| `GraceSeconds` | `60` | The once-a-night wrap-up unlock |
| `BorrowMinutes` | `30` | Nightly borrow pool (before subtracting what you owe) |

**Changes take effect from the next wake time, never tonight.**

## Commands

| | |
|---|---|
| Tonight's status | `& "$env:ProgramFiles\GoToSleep\GoToSleep.exe" --status` |
| Look at the lock screen (Esc closes it) | `& "$env:ProgramFiles\GoToSleep\GoToSleep.exe" --preview` |
| What happened each night | `C:\ProgramData\GoToSleep\log.txt` |
| Uninstall (admin) | `.\uninstall.ps1` |

## What makes it hard to get around

- **It always comes back.** The scheduled task starts it elevated at logon and checks every minute. Kill the process and it's back within 60 seconds.
- **The lock is based on the clock, not on a timer.** Rebooting, sleeping, signing out or killing the app doesn't reset anything.
- **Setting the system clock back doesn't help.** The app never lets time run backwards, even across reboots.
- **Shortcuts are blocked while locked.** Win, Alt+Tab, Alt+F4, Ctrl+Esc and Ctrl+Shift+Esc do nothing. Task Manager is disabled, and gets closed if it does open. The lock can't be minimized or closed.
- **Fullscreen games can't hide it.** An exclusive-fullscreen game is minimized, and minimized again if it grabs focus.
- **Settings changes wait until tomorrow,** and the config and state files need admin rights to edit.
- **Uninstalling takes effort.** It's refused while locked or within 3 hours of bedtime, and you have to type out a sentence.

## Limits

These are deliberate. The goal is to make getting around it a conscious choice, not to make it impossible.

- You're an admin on this PC. Disabling the scheduled task and killing the process during the day gets rid of it.
- **Safe Mode** doesn't run it. That's also the emergency exit if something ever goes wrong: Shift+Restart from the sign-in screen → Troubleshoot → Startup Settings → Safe Mode, then run `uninstall.ps1` or disable the `GoToSleep` task.
- Ctrl+Alt+Del still works, because Windows doesn't let any app block it. Its menu only offers sign out, switch user and lock, and all of those bring you back to the lock screen.
- Popups can't draw over a game in exclusive fullscreen. With several monitors they move to the main monitor if it's free, otherwise to the next free one. With one monitor you'll only hear the warning sound. Borderless and windowed games are fine.
- New tray icons start in the ^ overflow. Drag the moon onto the taskbar to keep it visible.

## Trying it safely (debug build)

```powershell
powershell -ExecutionPolicy Bypass -File .\debug-run.ps1                          # locks in ~2 min, for 5 min
powershell -ExecutionPolicy Bypass -File .\debug-run.ps1 -BorrowedLastNight 20    # simulate a "second night"
```

The debug build's lock screen and tray menu have a **DEBUG: Exit** option, and it exits by itself 15 minutes after starting. It keeps its settings and log in `bin\debug-data`, never touches the real install, and nothing relaunches it. The exit option is only compiled into `GoToSleep.Debug.exe`; `install.ps1` always installs the normal build, which doesn't contain it.

Run it from an admin PowerShell to also test Task Manager blocking. Other options: `-LockInMinutes`, `-LockForMinutes`.

## Building and testing

```powershell
.\build.ps1 -Test              # build bin\GoToSleep.exe and run the tests
.\build.ps1 -Test -DebugBuild  # also build bin\GoToSleep.Debug.exe
```

There's nothing to install: it uses the C# compiler that ships with Windows (.NET Framework 4.x).

| File | What's in it |
|---|---|
| `src/Core.cs` | Config, saved state, the clock that can't go backwards, and `Engine`: all the bedtime, extension and borrowing rules |
| `src/Program.cs` | Entry point and command-line modes, and `Controller`: the half-second loop that runs the lock, popups and tray |
| `src/UI.cs` | Lock screen, corner popups, tray icon |
| `src/Native.cs` | Windows APIs: keyboard blocking, focus, minimize/restore, Task Manager policy, mute, power |
| `src/Media.cs` | Pausing and resuming media through Windows' media controls |
| `tests/Tests.cs` | Tests for the time logic, plus a few window and monitor checks |
| `install.ps1`, `uninstall.ps1`, `debug-run.ps1`, `build.ps1` | Scripts |

## License

MIT. See [LICENSE](LICENSE).
