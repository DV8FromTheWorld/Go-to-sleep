using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace GoToSleep
{
    static class Paths
    {
#if DEBUG
        // Debug builds never touch the real install's settings or state.
        public static readonly string Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug-data");
#else
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GoToSleep");
#endif
        public static readonly string Config = Path.Combine(Dir, "config.ini");
        public static readonly string State = Path.Combine(Dir, "state.ini");
        public static readonly string Log = Path.Combine(Dir, "log.txt");
    }

    static class Log
    {
        public static void Write(string message)
        {
            try
            {
                var info = new FileInfo(Paths.Log);
                if (info.Exists && info.Length > 1024 * 1024)
                {
                    File.Copy(Paths.Log, Paths.Log + ".old", true);
                    File.Delete(Paths.Log);
                }
                File.AppendAllText(Paths.Log,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    static class Ini
    {
        public static Dictionary<string, string> Read(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return d;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static void Write(string path, IEnumerable<KeyValuePair<string, string>> values)
        {
            var sb = new StringBuilder();
            foreach (var kv in values) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    class Config
    {
        public TimeSpan Bedtime = new TimeSpan(1, 0, 0);
        public TimeSpan WakeTime = new TimeSpan(7, 0, 0);
        public int[] WarnMinutes = { 30, 15, 5, 1 };
        public int GraceSeconds = 60;
        public int BorrowMinutes = 30;

        public static Config Load()
        {
            var c = new Config();
            Dictionary<string, string> d;
            try { d = Ini.Read(Paths.Config); }
            catch { return c; }

            string v;
            TimeSpan t;
            int n;
            if (d.TryGetValue("Bedtime", out v) && TryParseTime(v, out t)) c.Bedtime = t;
            if (d.TryGetValue("WakeTime", out v) && TryParseTime(v, out t)) c.WakeTime = t;
            if (d.TryGetValue("GraceSeconds", out v) && int.TryParse(v, out n)) c.GraceSeconds = Math.Max(0, Math.Min(n, 300));
            if (d.TryGetValue("BorrowMinutes", out v) && int.TryParse(v, out n)) c.BorrowMinutes = Math.Max(0, Math.Min(n, 120));
            if (d.TryGetValue("WarnMinutes", out v))
            {
                var list = new List<int>();
                foreach (var part in v.Split(','))
                    if (int.TryParse(part.Trim(), out n) && n > 0) list.Add(n);
                c.WarnMinutes = list.ToArray();
            }
            return c;
        }

        static readonly string[] TimeFormats = { "H:mm", "HH:mm", "h:mm tt", "h:mmtt", "h tt", "htt" };

        public static bool TryParseTime(string s, out TimeSpan t)
        {
            DateTime dt;
            if (DateTime.TryParseExact(s.Trim().ToUpperInvariant(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                t = dt.TimeOfDay;
                return true;
            }
            t = TimeSpan.Zero;
            return false;
        }
    }

    // Persisted between runs and reboots. The "session" is one day, running from wake time to the next wake time;
    // its rules are snapshotted when it starts so editing the config can only ever affect tomorrow.
    class State
    {
        public DateTime SessionDate = DateTime.MinValue;
        public TimeSpan Bedtime, WakeTime;
        public int GraceSeconds, BorrowMinutes;
        public int DebtMinutes;          // taken off tonight's bedtime (borrowed last night)
        public int BorrowedSeconds;      // borrowed so far tonight (finished stretches); comes off tomorrow's bedtime
        public bool GraceUsed;
        public string UnlockKind = "";   // "", "grace" or "borrow"
        public DateTime UnlockStartUtc = DateTime.MinValue, UnlockUntilUtc = DateTime.MinValue;
        public ulong UnlockUptimeMs;
        public bool MutedByUs;
        public DateTime LastSeenUtc = DateTime.MinValue;

        const string DateFormat = "yyyy-MM-dd";

        public static State Load()
        {
            var s = new State();
            Dictionary<string, string> d;
            try { d = Ini.Read(Paths.State); }
            catch { return s; }

            s.SessionDate = GetDate(d, "SessionDate", DateFormat);
            s.Bedtime = GetSpan(d, "Bedtime");
            s.WakeTime = GetSpan(d, "WakeTime");
            s.GraceSeconds = GetInt(d, "GraceSeconds");
            s.BorrowMinutes = GetInt(d, "BorrowMinutes");
            s.DebtMinutes = GetInt(d, "DebtMinutes");
            s.BorrowedSeconds = GetInt(d, "BorrowedSeconds");
            s.GraceUsed = GetInt(d, "GraceUsed") != 0;
            string v;
            s.UnlockKind = d.TryGetValue("UnlockKind", out v) && (v == "grace" || v == "borrow") ? v : "";
            s.UnlockStartUtc = GetDate(d, "UnlockStartUtc", "o");
            s.UnlockUntilUtc = GetDate(d, "UnlockUntilUtc", "o");
            ulong up;
            s.UnlockUptimeMs = d.TryGetValue("UnlockUptimeMs", out v) && ulong.TryParse(v, out up) ? up : 0;
            s.MutedByUs = GetInt(d, "MutedByUs") != 0;
            s.LastSeenUtc = GetDate(d, "LastSeenUtc", "o");
            return s;
        }

        public void Save()
        {
            var ic = CultureInfo.InvariantCulture;
            Ini.Write(Paths.State, new[]
            {
                Pair("SessionDate", SessionDate.ToString(DateFormat, ic)),
                Pair("Bedtime", Bedtime.ToString("c", ic)),
                Pair("WakeTime", WakeTime.ToString("c", ic)),
                Pair("GraceSeconds", GraceSeconds.ToString(ic)),
                Pair("BorrowMinutes", BorrowMinutes.ToString(ic)),
                Pair("DebtMinutes", DebtMinutes.ToString(ic)),
                Pair("BorrowedSeconds", BorrowedSeconds.ToString(ic)),
                Pair("GraceUsed", GraceUsed ? "1" : "0"),
                Pair("UnlockKind", UnlockKind),
                Pair("UnlockStartUtc", UnlockStartUtc.ToString("o", ic)),
                Pair("UnlockUntilUtc", UnlockUntilUtc.ToString("o", ic)),
                Pair("UnlockUptimeMs", UnlockUptimeMs.ToString(ic)),
                Pair("MutedByUs", MutedByUs ? "1" : "0"),
                Pair("LastSeenUtc", LastSeenUtc.ToString("o", ic)),
            });
        }

        static KeyValuePair<string, string> Pair(string k, string v) { return new KeyValuePair<string, string>(k, v); }

        static DateTime GetDate(Dictionary<string, string> d, string key, string format)
        {
            string v;
            DateTime dt;
            if (d.TryGetValue(key, out v) &&
                DateTime.TryParseExact(v, format, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out dt))
                return dt;
            return DateTime.MinValue;
        }

        static TimeSpan GetSpan(Dictionary<string, string> d, string key)
        {
            string v;
            TimeSpan t;
            return d.TryGetValue(key, out v) && TimeSpan.TryParseExact(v, "c", CultureInfo.InvariantCulture, out t) ? t : TimeSpan.Zero;
        }

        static int GetInt(Dictionary<string, string> d, string key)
        {
            string v;
            int n;
            return d.TryGetValue(key, out v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0;
        }
    }

    static class Schedule
    {
        // A session starts at sessionDate + wake; bedtime is the first occurrence of `bed` after that.
        public static DateTime Bedtime(DateTime sessionDate, TimeSpan bed, TimeSpan wake, int debtMinutes)
        {
            var start = sessionDate.Date + wake;
            var offset = bed - wake;
            if (offset <= TimeSpan.Zero) offset += TimeSpan.FromDays(1);
            var b = start + offset - TimeSpan.FromMinutes(debtMinutes);
            return b < start ? start : b;
        }

        public static DateTime LockEnd(DateTime sessionDate, TimeSpan wake)
        {
            return sessionDate.Date.AddDays(1) + wake;
        }

        public static DateTime SessionDateFor(DateTime local, TimeSpan wake)
        {
            return (local - wake).Date;
        }
    }

    // Wall-clock time that refuses to go meaningfully backwards, so setting the system clock back doesn't buy time.
    class MonotonicClock
    {
        static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(60);
        readonly Stopwatch sw = Stopwatch.StartNew();
        DateTime last;
        TimeSpan lastElapsed;

        public MonotonicClock(DateTime lastSeenUtc)
        {
            var wall = DateTime.UtcNow;
            last = lastSeenUtc > wall + Tolerance ? lastSeenUtc : wall;
            lastElapsed = sw.Elapsed;
        }

        public DateTime UtcNow()
        {
            var wall = DateTime.UtcNow;
            var elapsed = sw.Elapsed;
            var floor = last + (elapsed - lastElapsed);
            last = wall + Tolerance < floor ? floor : wall;
            lastElapsed = elapsed;
            return last;
        }
    }

    class Engine
    {
        public Config Config;
        public readonly State S;
        public DateTime NowUtc, NowLocal, BedtimeLocal, LockEndLocal;
        public bool InWindow, Locked;

        public Engine(Config config, State state)
        {
            Config = config;
            S = state;
        }

        // Returns true if persisted state changed.
        public bool Update(DateTime nowUtc)
        {
            NowUtc = nowUtc;
            NowLocal = nowUtc.ToLocalTime();
            bool changed = false;

            if (S.UnlockKind != "" && NowUtc >= S.UnlockUntilUtc)
            {
                Log.Write("Extension (" + S.UnlockKind + ") ran out");
                EndUnlock(S.UnlockUntilUtc);
                changed = true;
            }

            if (S.SessionDate == DateTime.MinValue)
            {
                StartSession(Schedule.SessionDateFor(NowLocal, Config.WakeTime), 0);
                changed = true;
            }
            else if (NowLocal >= Schedule.LockEnd(S.SessionDate, S.WakeTime))
            {
                if (S.UnlockKind != "") EndUnlock(NowUtc);
                var previousEnd = Schedule.LockEnd(S.SessionDate, S.WakeTime);
                // The day we're in under the (possibly changed) wake time, unless its night would overlap the lock
                // that just ended. Plain "previous day + 1" skips tonight when the wake time moves later (e.g. a
                // short test install that ended at 12:10 AM, then a real 7:00 AM wake time).
                var next = Schedule.SessionDateFor(NowLocal, Config.WakeTime);
                if (Schedule.Bedtime(next, Config.Bedtime, Config.WakeTime, 0) < previousEnd) next = next.AddDays(1);
                // Debt carries into the first night after the one it was borrowed in. If a whole night went by
                // with the PC off, that night already "paid" it.
                bool firstNightAfter = Schedule.Bedtime(next.AddDays(-1), Config.Bedtime, Config.WakeTime, 0) < previousEnd;
                StartSession(next, firstNightAfter ? DebtMinutesFor(S.BorrowedSeconds) : 0);
                changed = true;
            }

            BedtimeLocal = Schedule.Bedtime(S.SessionDate, S.Bedtime, S.WakeTime, S.DebtMinutes);
            LockEndLocal = Schedule.LockEnd(S.SessionDate, S.WakeTime);
            InWindow = NowLocal >= BedtimeLocal && NowLocal < LockEndLocal;
            Locked = InWindow && S.UnlockKind == "";
            return changed;
        }

        // Called once at startup: an extension only survives a restart of this process, not a reboot.
        public void Resume(DateTime nowUtc)
        {
            if (S.UnlockKind == "") return;
            ulong up = Native.GetTickCount64();
            double sinceStartMs = (nowUtc - S.UnlockStartUtc).TotalMilliseconds;
            bool sameBoot = up >= S.UnlockUptimeMs && Math.Abs((double)(up - S.UnlockUptimeMs) - sinceStartMs) < 120000;
            if (sameBoot) return;
            var end = S.LastSeenUtc < S.UnlockUntilUtc ? S.LastSeenUtc : S.UnlockUntilUtc;
            if (end < S.UnlockStartUtc) end = S.UnlockStartUtc;
            Log.Write("Extension (" + S.UnlockKind + ") ended by restart");
            EndUnlock(end);
        }

        void StartSession(DateTime date, int debt)
        {
            S.SessionDate = date.Date;
            S.Bedtime = Config.Bedtime;
            S.WakeTime = Config.WakeTime;
            S.GraceSeconds = Config.GraceSeconds;
            S.BorrowMinutes = Config.BorrowMinutes;
            S.DebtMinutes = debt;
            S.BorrowedSeconds = 0;
            S.GraceUsed = false;
            S.UnlockKind = "";
            Log.Write("New day " + date.ToString("yyyy-MM-dd") + ": bedtime " +
                      Schedule.Bedtime(date, S.Bedtime, S.WakeTime, debt).ToString("ddd HH:mm") +
                      (debt > 0 ? " (" + debt + " min early, borrowed last night)" : ""));
        }

        // Tonight's pool is the nightly allowance minus what's being paid back from last night, so a late night is always
        // followed by an earlier one instead of the debt rolling forward forever.
        public int PoolTotalSeconds
        {
            get { return Math.Max(0, (S.BorrowMinutes - S.DebtMinutes) * 60); }
        }

        // What's left of tonight's borrow pool, not counting a stretch that's running right now.
        public int BorrowPoolSeconds
        {
            get { return Math.Max(0, PoolTotalSeconds - S.BorrowedSeconds); }
        }

        // How long a new borrow stretch could run (the pool, but never past wake time).
        public int BorrowAvailableSeconds
        {
            get { return (int)Math.Max(0, Math.Min(BorrowPoolSeconds, Math.Floor((LockEndLocal - NowLocal).TotalSeconds))); }
        }

        // Tonight's borrowing so far, including a stretch in progress.
        public int BorrowedSecondsNow
        {
            get { return S.UnlockKind == "borrow" ? S.BorrowedSeconds + StretchSeconds(NowUtc) : S.BorrowedSeconds; }
        }

        public int ProjectedDebtMinutes
        {
            get { return DebtMinutesFor(BorrowedSecondsNow); }
        }

        public int DebtMinutesFor(int borrowedSeconds)
        {
            return Math.Min(S.BorrowMinutes, (int)Math.Ceiling(borrowedSeconds / 60.0));
        }

        public bool StartGrace()
        {
            if (!Locked || S.GraceUsed || S.GraceSeconds <= 0) return false;
            S.GraceUsed = true;
            BeginUnlock("grace", TimeSpan.FromSeconds(S.GraceSeconds));
            return true;
        }

        public bool StartBorrow()
        {
            int seconds = BorrowAvailableSeconds;
            if (!Locked || seconds <= 0) return false;
            BeginUnlock("borrow", TimeSpan.FromSeconds(seconds));
            return true;
        }

        void BeginUnlock(string kind, TimeSpan length)
        {
            S.UnlockKind = kind;
            S.UnlockStartUtc = NowUtc;
            S.UnlockUntilUtc = NowUtc + length;
            S.UnlockUptimeMs = Native.GetTickCount64();
            Locked = false;
        }

        public void EndUnlock(DateTime endUtc)
        {
            if (S.UnlockKind == "borrow")
            {
                int used = StretchSeconds(endUtc);
                S.BorrowedSeconds = Math.Min(PoolTotalSeconds, S.BorrowedSeconds + used);
                Log.Write("Borrowed " + Fmt.Countdown(TimeSpan.FromSeconds(used)) + "; tonight's total " +
                          Fmt.Countdown(TimeSpan.FromSeconds(S.BorrowedSeconds)) + " of " + Fmt.Countdown(TimeSpan.FromSeconds(PoolTotalSeconds)));
            }
            S.UnlockKind = "";
        }

        int StretchSeconds(DateTime endUtc)
        {
            double used = Math.Max(0, (endUtc - S.UnlockStartUtc).TotalSeconds);
            return Math.Min(BorrowPoolSeconds, (int)Math.Ceiling(used - 0.001));
        }

        public DateTime TomorrowBedtime(int debtMinutes)
        {
            return Schedule.Bedtime(S.SessionDate.AddDays(1), Config.Bedtime, Config.WakeTime, debtMinutes);
        }

        public bool CanUninstall()
        {
            return !InWindow && S.UnlockKind == "" && (BedtimeLocal - NowLocal).TotalHours >= 3;
        }
    }
}
