using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace GoToSleep
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (mode == "--preview") { Preview(); return 0; }
            if (mode == "--status") { MessageBox.Show(Status(Snapshot()), "Go To Sleep"); return 0; }
            if (mode == "--can-uninstall") return Snapshot().CanUninstall() ? 0 : 1;

            bool createdNew;
#if DEBUG
            const string mutexName = @"Global\GoToSleep.Debug";
#else
            const string mutexName = @"Global\GoToSleep.Enforcer";
#endif
            using (var mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew) return 0;
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Write("Unhandled: " + e.Exception);
                Application.Run(new Controller());
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        // Read-only view of the current state (nothing is saved).
        static Engine Snapshot()
        {
            var state = State.Load();
            var engine = new Engine(Config.Load(), state);
            engine.Update(new MonotonicClock(state.LastSeenUtc).UtcNow());
            return engine;
        }

        static string Status(Engine e)
        {
            var s = e.S;
            string mode = e.Locked ? "LOCKED" : s.UnlockKind == "grace" ? "wrap-up minute" : s.UnlockKind == "borrow" ? "borrowed time" : "unlocked";
            return "Now: " + Fmt.When(e.NowLocal) + "  (" + mode + ")\n\n" +
                   "Tonight's bedtime: " + Fmt.When(e.BedtimeLocal) +
                   (s.DebtMinutes > 0 ? "  (" + s.DebtMinutes + " min early, borrowed last night)" : "") + "\n" +
                   "Unlocks at: " + Fmt.When(e.LockEndLocal) + "\n" +
                   "Wrap-up minute: " + (s.GraceUsed ? "used" : "available") + "\n" +
                   "Borrowed from tomorrow: " + Fmt.Countdown(TimeSpan.FromSeconds(e.BorrowedSecondsNow)) + " of tonight's " +
                   Fmt.Countdown(TimeSpan.FromSeconds(e.PoolTotalSeconds)) + " pool\n" +
                   "Tomorrow's bedtime: " + Fmt.When(e.TomorrowBedtime(e.ProjectedDebtMinutes)) + "\n\n" +
                   "Config: " + Paths.Config + "\n(config changes take effect from the next wake time)";
        }

        static void Preview()
        {
            var engine = Snapshot();
            var form = new LockForm(Screen.PrimaryScreen.Bounds, true, true);
            var view = Controller.BuildView(engine);
            view.Headline = "Preview: press Esc to close";
            form.UpdateView(view);
            form.ShutdownClicked += (s, e) => MessageBox.Show(form, "This would shut down the PC.", "Preview");
            form.SleepClicked += (s, e) => MessageBox.Show(form, "This would put the PC to sleep.", "Preview");
            form.GraceClicked += (s, e) => MessageBox.Show(form, "This would unlock for " + engine.S.GraceSeconds + " seconds.", "Preview");
            form.BorrowClicked += (s, e) => MessageBox.Show(form, "This would start borrowing time from tomorrow.", "Preview");
            Application.Run(form);
        }
    }

    static class Fmt
    {
        public static string Time(DateTime t) { return t.ToShortTimeString(); }
        public static string When(DateTime t) { return t.ToString("ddd") + " " + t.ToShortTimeString(); }

        public static string Duration(TimeSpan t)
        {
            int minutes = Math.Max(0, (int)Math.Ceiling(t.TotalMinutes));
            return minutes >= 60 ? (minutes / 60) + "h " + (minutes % 60) + "m" : minutes + "m";
        }

        public static string Countdown(TimeSpan t)
        {
            int total = Math.Max(0, (int)Math.Ceiling(t.TotalSeconds));
            return (total / 60) + ":" + (total % 60).ToString("00");
        }
    }

    class Controller : ApplicationContext
    {
        readonly State state;
        readonly Engine engine;
        readonly MonotonicClock clock;
        readonly System.Windows.Forms.Timer timer;
        readonly List<LockForm> lockForms = new List<LockForm>();
        readonly Popup overlay = new Popup(true);
        readonly Popup toast = new Popup(false);
        readonly HashSet<string> firedWarnings = new HashSet<string>();
        readonly Tray tray = new Tray();
        string overlayKey, dismissedOverlayKey;  // which countdown is showing, and which one the user closed
        readonly List<IntPtr> displaced = new List<IntPtr>();  // windows the lock pushed aside; [0] had focus
        bool lockedUi;
        DateTime toastUntil = DateTime.MinValue, lastSave = DateTime.MinValue, lastHook = DateTime.MinValue, lastConfigLoad;
        string lastError;
#if DEBUG
        readonly DateTime debugStartedUtc = DateTime.UtcNow;
        bool debugExitRequested;
#endif

        public Controller()
        {
            try { Directory.CreateDirectory(Paths.Dir); } catch { }
            state = State.Load();
            clock = new MonotonicClock(state.LastSeenUtc);
            engine = new Engine(Config.Load(), state);
            lastConfigLoad = clock.UtcNow();
            engine.Resume(lastConfigLoad);
            TaskManagerPolicy.Set(false);
            Log.Write("Started");

            overlay.ButtonClicked += (s, e) => StopExtension();
            overlay.DismissClicked += (s, e) => { dismissedOverlayKey = overlayKey; overlay.Hide(); };
            toast.DismissClicked += (s, e) => toast.Hide();
            tray.DoneClicked += (s, e) => StopExtension();
            tray.ShowClicked += (s, e) => { dismissedOverlayKey = null; Tick(); };
#if DEBUG
            tray.DebugExitClicked += (s, e) => debugExitRequested = true;
#endif
            timer = new System.Windows.Forms.Timer { Interval = 500 };
            timer.Tick += (s, e) => Tick();
            timer.Start();
            Tick();
        }

        void Tick()
        {
            try
            {
#if DEBUG
                if (debugExitRequested || (DateTime.UtcNow - debugStartedUtc).TotalMinutes >= 15)
                {
                    DebugExit(debugExitRequested ? "button" : "15 minute failsafe");
                    return;
                }
#endif
                var now = clock.UtcNow();
                if ((now - lastConfigLoad).TotalSeconds >= 60)
                {
                    engine.Config = Config.Load();
                    lastConfigLoad = now;
                }

                bool changed = engine.Update(now);
                if (engine.Locked) EnsureLocked(now);
                else EnsureUnlocked(state.UnlockKind != "");
                UpdateOverlay();
                UpdateWarnings(now);
                UpdateTray();

                state.LastSeenUtc = now;
                if (changed || (now - lastSave).TotalSeconds >= 10) Save();
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError) Log.Write("Tick error: " + ex);
                lastError = ex.Message;
            }
        }

#if DEBUG
        void DebugExit(string reason)
        {
            timer.Stop();
            Log.Write("DEBUG exit (" + reason + ")");
            EnsureUnlocked(true);
            KeyboardBlocker.Disable();
            TaskManagerPolicy.Set(false);
            overlay.Close();
            toast.Close();
            tray.Dispose();
            ExitThread();
        }
#endif

        void Save()
        {
            try
            {
                state.Save();
                lastSave = state.LastSeenUtc;
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError) Log.Write("Save failed: " + ex.Message);
                lastError = ex.Message;
            }
        }

        void EnsureLocked(DateTime now)
        {
            if (!lockedUi)
            {
                lockedUi = true;
                Log.Write("Locked");
                toast.Hide();
                overlay.Hide();
                // Only an exclusive-fullscreen game needs minimizing; anything else just sits behind the lock.
                displaced.Clear();
                var active = Native.GetForegroundWindow();
                if (Native.IsOtherAppWindow(active))
                {
                    displaced.Add(active);
                    if (Native.ExclusiveFullscreenRunning()) Native.Minimize(active);
                }
                Media.PausePlaying();
                if (!state.MutedByUs && !Audio.IsMuted() && Audio.SetMute(true)) state.MutedByUs = true;
                TaskManagerPolicy.Set(true);
                KeyboardBlocker.Enable();
                lastHook = now;
                Save();
            }

            var bounds = Screen.AllScreens.OrderBy(s => s.Primary ? 0 : 1).Select(s => s.Bounds).ToArray();
            if (!bounds.SequenceEqual(lockForms.Select(f => f.TargetBounds))) RebuildLockForms(bounds);

            lockForms[0].UpdateView(BuildView(engine));
            foreach (var f in lockForms)
            {
                if (!f.Visible) f.Show();
                if (f.WindowState != FormWindowState.Normal) f.WindowState = FormWindowState.Normal;
                var b = f.TargetBounds;
                Native.SetWindowPos(f.Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
            }
            var fg = Native.GetForegroundWindow();
            if (!lockForms.Any(f => f.Handle == fg))
            {
                // Something grabbed focus back. If it's an exclusive-fullscreen game, knock it down again.
                if (Native.IsOtherAppWindow(fg) && Native.ExclusiveFullscreenRunning())
                {
                    Native.Minimize(fg);
                    if (!displaced.Contains(fg)) displaced.Add(fg);
                }
                Native.ForceForeground(lockForms[0].Handle);
            }

            // Windows silently drops low-level hooks that ever respond slowly, so refresh it periodically.
            if ((now - lastHook).TotalSeconds >= 30)
            {
                KeyboardBlocker.Disable();
                KeyboardBlocker.Enable();
                lastHook = now;
            }
            TaskManagerPolicy.KillRunning();
        }

        void RebuildLockForms(Rectangle[] bounds)
        {
            CloseLockForms();
            for (int i = 0; i < bounds.Length; i++)
            {
                var f = new LockForm(bounds[i], i == 0, false);
                if (i == 0)
                {
                    f.ShutdownClicked += (s, e) => { Log.Write("Chose: shut down"); Save(); Power.Shutdown(); };
                    f.SleepClicked += (s, e) => { Log.Write("Chose: sleep"); Save(); Power.Sleep(); };
                    f.GraceClicked += (s, e) => StartExtension(false);
                    f.BorrowClicked += (s, e) => StartExtension(true);
#if DEBUG
                    // Handled on the next tick rather than here, so the form isn't disposed inside its own click.
                    f.DebugExitClicked += (s, e) => debugExitRequested = true;
#endif
                }
                lockForms.Add(f);
            }
        }

        void CloseLockForms()
        {
            foreach (var f in lockForms)
            {
                f.AllowClose = true;
                f.Close();
                f.Dispose();
            }
            lockForms.Clear();
        }

        // resume: true when unlocking for an extension (put things back how they were), false in the morning
        // (don't autoplay music or pop a game back up when you wake the PC).
        void EnsureUnlocked(bool resume)
        {
            if (state.MutedByUs)
            {
                Audio.SetMute(false);
                state.MutedByUs = false;
            }
            if (!lockedUi) return;
            lockedUi = false;
            CloseLockForms();
            KeyboardBlocker.Disable();
            TaskManagerPolicy.Set(false);
            Log.Write("Unlocked" + (state.UnlockKind != "" ? " (" + state.UnlockKind + ")" : ""));
            if (resume)
            {
                Media.ResumePaused();
                // Restore the originally focused window last so it ends up on top.
                for (int i = displaced.Count - 1; i >= 0; i--) Native.Restore(displaced[i]);
            }
            else Media.ForgetPaused();
            displaced.Clear();
            Save();
        }

        void StartExtension(bool borrow)
        {
            engine.Update(clock.UtcNow());
            bool started = borrow ? engine.StartBorrow() : engine.StartGrace();
            if (!started) return;
            Log.Write(borrow
                ? "Chose: borrow from tomorrow (" + Fmt.Countdown(state.UnlockUntilUtc - state.UnlockStartUtc) + " available)"
                : "Chose: " + state.GraceSeconds + "s wrap-up");
            Save();
            Tick();
        }

        void StopExtension()
        {
            if (state.UnlockKind == "") return;
            engine.Update(clock.UtcNow());
            Log.Write("Stopped " + state.UnlockKind + " early");
            engine.EndUnlock(engine.NowUtc);
            Save();
            Tick();
        }

        void UpdateOverlay()
        {
            string text = null, button = null, key = null;
            if (!engine.Locked)
            {
                var left = state.UnlockUntilUtc - engine.NowUtc;
                if (state.UnlockKind == "grace")
                {
                    text = "Wrap up. Locking in " + Fmt.Countdown(left);
                    button = "Lock now";
                    key = "grace" + state.UnlockStartUtc.Ticks;
                }
                else if (state.UnlockKind == "borrow")
                {
                    text = "Borrowed time: " + Fmt.Countdown(left) + " left  \u00B7  tomorrow -" + engine.ProjectedDebtMinutes + " min";
                    button = "I'm done, lock now";
                    key = "borrow" + state.UnlockStartUtc.Ticks;
                }
                else
                {
                    var toBed = engine.BedtimeLocal - engine.NowLocal;
                    if (toBed > TimeSpan.Zero && toBed <= TimeSpan.FromSeconds(60))
                    {
                        text = "Bedtime in " + Fmt.Countdown(toBed);
                        key = "bed" + engine.BedtimeLocal.Ticks;
                    }
                }
            }
            overlayKey = key;
            if (text == null || key == dismissedOverlayKey) overlay.Hide();
            else overlay.Display(text, button);
        }

        void UpdateTray()
        {
            string status;
            var left = state.UnlockUntilUtc - engine.NowUtc;
            if (engine.Locked) status = "Locked until " + Fmt.Time(engine.LockEndLocal);
            else if (state.UnlockKind == "grace") status = "Wrap-up: " + Fmt.Countdown(left) + " left";
            else if (state.UnlockKind == "borrow") status = "Borrowed time: " + Fmt.Countdown(left) + " left (tomorrow -" + engine.ProjectedDebtMinutes + " min)";
            else status = "Bedtime " + Fmt.Time(engine.BedtimeLocal) + " (in " + Fmt.Duration(engine.BedtimeLocal - engine.NowLocal) + ")";

            int poolLeft = Math.Max(0, engine.PoolTotalSeconds - engine.BorrowedSecondsNow);
            string pool = state.BorrowMinutes > 0
                ? "Borrowable tonight: " + Fmt.Countdown(TimeSpan.FromSeconds(poolLeft)) + " of " + Fmt.Countdown(TimeSpan.FromSeconds(engine.PoolTotalSeconds)) +
                  (state.DebtMinutes > 0 ? " (paying back " + state.DebtMinutes + " min)" : "")
                : null;
            tray.Update(status, pool, state.UnlockKind != "", overlayKey != null && overlayKey == dismissedOverlayKey);
        }

        void UpdateWarnings(DateTime now)
        {
            if (toast.Visible && (engine.Locked || now >= toastUntil)) toast.Hide();
            if (engine.InWindow || state.UnlockKind != "") return;

            double left = (engine.BedtimeLocal - engine.NowLocal).TotalMinutes;
            if (left <= 0) return;
            string key = engine.BedtimeLocal.Ticks + ":";
            bool hit = false;
            foreach (int t in engine.Config.WarnMinutes.OrderByDescending(x => x))
                if (left <= t && firedWarnings.Add(key + t)) hit = true;
            if (!hit) return;

            int minutes = (int)Math.Ceiling(left);
            toast.Display("Bedtime in " + minutes + " min (" + Fmt.Time(engine.BedtimeLocal) + "). Start wrapping up.", null);
            toastUntil = now.AddSeconds(15);
            SystemSounds.Exclamation.Play();
        }

        public static LockView BuildView(Engine e)
        {
            var s = e.S;
            // Show the pool itself, which only shrinks while borrowing. A stretch is still cut off at wake time, but
            // that costs nothing since only time actually used is charged.
            int available = e.BorrowAvailableSeconds;
            int pool = e.BorrowPoolSeconds;
            string poolText = pool % 60 == 0 ? (pool / 60) + " min" : Fmt.Countdown(TimeSpan.FromSeconds(pool));
            int tomorrowDebt = e.ProjectedDebtMinutes;
            int poolTotalMinutes = e.PoolTotalSeconds / 60;
            var normalBedtime = Schedule.Bedtime(s.SessionDate, s.Bedtime, s.WakeTime, 0);

            string poolInfo = "";
            if (s.BorrowMinutes > 0)
            {
                if (poolTotalMinutes == 0)
                    poolInfo = "Nothing to borrow tonight: you're paying back all " + s.DebtMinutes + " min from last night.";
                else
                {
                    poolInfo = "Tonight's borrow pool: " + poolTotalMinutes + " min" +
                               (s.DebtMinutes > 0 ? " (" + s.BorrowMinutes + " minus the " + s.DebtMinutes + " you're paying back)." : ".");
                    if (s.BorrowedSeconds > 0)
                        poolInfo += " Used " + Fmt.Countdown(TimeSpan.FromSeconds(s.BorrowedSeconds)) + ", " +
                                    Fmt.Countdown(TimeSpan.FromSeconds(pool)) + " left.";
                }
            }

            var v = new LockView
            {
                Clock = Fmt.Time(e.NowLocal),
                Headline = "Time to sleep.",
                Until = "This PC is locked until " + Fmt.Time(e.LockEndLocal) + "." +
                        (s.DebtMinutes > 0
                            ? "\nLocked at " + Fmt.Time(e.BedtimeLocal) + " instead of " + Fmt.Time(normalBedtime) +
                              " because you borrowed " + s.DebtMinutes + " min last night."
                            : ""),
                PoolInfo = poolInfo,
                GraceEnabled = !s.GraceUsed && s.GraceSeconds > 0,
                GraceText = s.GraceUsed ? "Wrap-up time already used tonight" : s.GraceSeconds + " seconds to wrap up (once a night)",
                BorrowEnabled = available > 0,
                BorrowText = available > 0
                    ? (s.BorrowedSeconds == 0 ? "Borrow up to " + poolText + " from tomorrow" : "Borrow more from tomorrow (" + poolText + " left)")
                    : pool > 0 ? "Too close to wake time to borrow"
                    : poolTotalMinutes == 0 && s.BorrowMinutes > 0 ? "Nothing to borrow tonight (paying back last night)"
                    : "No borrowable time left tonight",
                BorrowWarning = "Every minute you use comes off tomorrow's bedtime. Use all " + poolText +
                                " and tomorrow you'll be locked at " + Fmt.Time(e.TomorrowBedtime(e.DebtMinutesFor(s.BorrowedSeconds + pool))) +
                                ". Stop whenever you like; whatever's left stays available tonight.",
                Tomorrow = "Tomorrow's bedtime: " + Fmt.Time(e.TomorrowBedtime(tomorrowDebt)) +
                           (tomorrowDebt > 0
                               ? " (" + tomorrowDebt + " min early, borrowed tonight), with " +
                                 Math.Max(0, e.Config.BorrowMinutes - tomorrowDebt) + " min to borrow"
                               : ""),
            };
#if DEBUG
            v.Tomorrow += "\n\nDEBUG BUILD: auto-exits 15 min after start.";
#endif
            return v;
        }
    }
}
