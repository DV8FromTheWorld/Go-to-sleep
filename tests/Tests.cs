using System;

namespace GoToSleep
{
    static class Tests
    {
        static int failures;

        static void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + name);
            if (!ok) failures++;
        }

        static DateTime L(int day, int h, int m, int s = 0)
        {
            return new DateTime(2026, 10, day, h, m, s, DateTimeKind.Local);
        }

        static DateTime U(DateTime local) { return local.ToUniversalTime(); }

        static Engine NewEngine(string bed, string wake)
        {
            var c = new Config();
            Config.TryParseTime(bed, out c.Bedtime);
            Config.TryParseTime(wake, out c.WakeTime);
            return new Engine(c, new State());
        }

        static int Main()
        {
            Console.WriteLine("Schedule");
            var d = new DateTime(2026, 10, 10);
            Check("1am bedtime falls the next calendar day", Schedule.Bedtime(d, TimeSpan.FromHours(1), TimeSpan.FromHours(7), 0) == L(11, 1, 0));
            Check("11pm bedtime falls the same day", Schedule.Bedtime(d, TimeSpan.FromHours(23), TimeSpan.FromHours(7), 0) == L(10, 23, 0));
            Check("debt moves bedtime earlier", Schedule.Bedtime(d, TimeSpan.FromHours(1), TimeSpan.FromHours(7), 20) == L(11, 0, 40));
            Check("lock ends at next wake", Schedule.LockEnd(d, TimeSpan.FromHours(7)) == L(11, 7, 0));
            TimeSpan t;
            Check("parses 1:00 AM", Config.TryParseTime("1:00 am", out t) && t == TimeSpan.FromHours(1));
            Check("parses 23:30", Config.TryParseTime("23:30", out t) && t == new TimeSpan(23, 30, 0));

            Console.WriteLine("Lock + 60s grace");
            var e = NewEngine("01:00", "07:00");
            e.Update(U(L(10, 20, 0)));
            Check("evening is unlocked", !e.Locked);
            e.Update(U(L(11, 0, 59)));
            Check("12:59 unlocked", !e.Locked);
            e.Update(U(L(11, 1, 0)));
            Check("1:00 locked", e.Locked);
            Check("grace starts", e.StartGrace());
            e.Update(U(L(11, 1, 0, 30)));
            Check("unlocked during grace", !e.Locked);
            e.Update(U(L(11, 1, 1, 1)));
            Check("locked after grace", e.Locked);
            Check("grace only once", !e.StartGrace());

            Console.WriteLine("Borrow pool");
            e.Update(U(L(11, 1, 2)));
            Check("borrow starts", e.StartBorrow());
            e.Update(U(L(11, 1, 10)));
            Check("unlocked during borrow", !e.Locked);
            Check("projected debt 8 (8:00 used)", e.ProjectedDebtMinutes == 8);
            e.EndUnlock(U(L(11, 1, 14, 30)));
            e.Update(U(L(11, 1, 15)));
            Check("stopping early locks", e.Locked);
            Check("12:30 used, 17:30 left in pool", e.S.BorrowedSeconds == 750 && e.BorrowPoolSeconds == 1050);
            Check("can borrow again from what's left", e.StartBorrow());
            Check("second stretch is capped at 17:30", e.S.UnlockUntilUtc == U(L(11, 1, 32, 30)));
            e.Update(U(L(11, 1, 20)));
            Check("projected debt counts both stretches (17:30 -> 18)", e.ProjectedDebtMinutes == 18);
            e.Update(U(L(11, 1, 40)));
            Check("locked when the pool runs out", e.Locked && e.S.BorrowedSeconds == 1800);
            Check("pool empty: no more borrowing", !e.StartBorrow());
            e.Update(U(L(11, 7, 0)));
            Check("unlocked at wake", !e.Locked && !e.InWindow);
            Check("30 min carried to tonight", e.S.DebtMinutes == 30 && e.BedtimeLocal == L(12, 0, 30));
            Check("grace and pool reset", !e.S.GraceUsed && e.S.BorrowedSeconds == 0);
            e.Update(U(L(12, 0, 30)));
            Check("locked at 12:30", e.Locked);
            Check("paying back all 30: nothing to borrow", e.PoolTotalSeconds == 0 && !e.StartBorrow());

            Console.WriteLine("Debt shrinks the next night's pool (borrow 20 -> 12:40 + 10 -> 12:50 + 20)");
            var p = NewEngine("01:00", "07:00");
            p.Update(U(L(10, 20, 0)));
            p.Update(U(L(11, 1, 0)));
            p.StartBorrow();
            p.EndUnlock(U(L(11, 1, 20)));
            p.Update(U(L(11, 7, 0)));
            Check("night 2: locks 12:40 with a 10 min pool", p.BedtimeLocal == L(12, 0, 40) && p.PoolTotalSeconds == 600);
            p.Update(U(L(12, 0, 40)));
            p.StartBorrow();
            p.Update(U(L(12, 0, 55)));
            Check("night 2: pool runs out at 12:50", p.Locked && p.S.BorrowedSeconds == 600 && !p.StartBorrow());
            p.Update(U(L(12, 7, 0)));
            Check("night 3: locks 12:50 with a 20 min pool", p.BedtimeLocal == L(13, 0, 50) && p.PoolTotalSeconds == 1200);

            Console.WriteLine("Partial minutes round up once, not per stretch");
            p.Update(U(L(13, 0, 50)));
            p.StartBorrow();
            p.EndUnlock(U(L(13, 0, 50, 20)));
            p.Update(U(L(13, 0, 51)));
            p.StartBorrow();
            p.EndUnlock(U(L(13, 0, 51, 20)));
            Check("two 20s stretches cost 1 min, not 2", p.S.BorrowedSeconds == 40 && p.ProjectedDebtMinutes == 1);

            Console.WriteLine("Skipped day drops debt");
            e.Update(U(L(14, 12, 0)));
            Check("debt dropped after a skipped day", e.S.DebtMinutes == 0 && e.BedtimeLocal == L(15, 1, 0));

            Console.WriteLine("Changing times between nights");
            var m = NewEngine("00:05", "00:10");
            m.Update(U(L(10, 23, 59)));
            Check("test lock: 12:05-12:10", m.BedtimeLocal == L(11, 0, 5) && m.LockEndLocal == L(11, 0, 10));
            Config.TryParseTime("01:00", out m.Config.Bedtime);
            Config.TryParseTime("07:00", out m.Config.WakeTime);
            m.Update(U(L(11, 0, 11)));
            Check("test lock crossing midnight doesn't skip tonight's 1:00", !m.Locked && m.BedtimeLocal == L(11, 1, 0));
            m.Update(U(L(11, 1, 0)));
            Check("locked at 1:00 the same night", m.Locked && m.LockEndLocal == L(11, 7, 0));

            var late = NewEngine("01:00", "07:00");
            late.Update(U(L(10, 20, 0)));
            late.Update(U(L(11, 6, 0)));
            Config.TryParseTime("23:30", out late.Config.WakeTime);
            late.Update(U(L(11, 7, 0)));
            Check("moving wake time later doesn't re-lock after a night ends", !late.Locked && late.BedtimeLocal == L(12, 1, 0));

            Console.WriteLine("Borrow is capped at wake time");
            var w = NewEngine("06:50", "07:00");
            w.Update(U(L(11, 6, 55)));
            Check("only 5 min available", w.Locked && w.BorrowAvailableSeconds == 300);
            w.Update(U(L(11, 6, 58)));
            Check("pool doesn't drain while not borrowing", w.BorrowPoolSeconds == 1800);

            Console.WriteLine("Uninstall window");
            var u = NewEngine("01:00", "07:00");
            u.Update(U(L(10, 21, 0)));
            Check("4h before bedtime: allowed", u.CanUninstall());
            u.Update(U(L(10, 22, 30)));
            Check("2.5h before bedtime: refused", !u.CanUninstall());

            Console.WriteLine("Popup monitor choice");
            var primary = new System.Drawing.Rectangle(0, 0, 2560, 1440);
            var left = new System.Drawing.Rectangle(-1920, 0, 1920, 1080);
            var right = new System.Drawing.Rectangle(2560, 0, 1920, 1080);
            var three = new[] { primary, left, right };
            Check("no exclusive game (or borderless): primary", Popup.PickScreen(three, false, primary) == 0);
            Check("exclusive game on primary: next monitor", Popup.PickScreen(three, true, primary) == 1);
            Check("exclusive game on 2nd: primary", Popup.PickScreen(three, true, left) == 0);
            Check("single monitor: primary anyway", Popup.PickScreen(new[] { primary }, true, primary) == 0);
            Console.WriteLine("Minimizing other apps");
            using (var notepad = System.Diagnostics.Process.Start("notepad.exe"))
            {
                notepad.WaitForInputIdle(5000);
                IntPtr h = IntPtr.Zero;
                for (int i = 0; i < 50 && h == IntPtr.Zero; i++)
                {
                    System.Threading.Thread.Sleep(100);
                    notepad.Refresh();
                    h = notepad.MainWindowHandle;
                }
                Check("notepad counts as an app window", Native.IsOtherAppWindow(h));
                Native.Minimize(h);
                System.Threading.Thread.Sleep(500);
                Check("notepad got minimized", Native.IsIconic(h));
                Native.Restore(h);
                System.Threading.Thread.Sleep(500);
                Check("notepad got restored", !Native.IsIconic(h));
                notepad.Kill();
            }

            Console.WriteLine(failures == 0 ? "All passed" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
