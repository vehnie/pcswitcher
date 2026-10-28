using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MonitorAutoSwitch
{
    enum OutcomeKind { AlreadyThere, Switched, SentUnverified, NotFound, Cancelled, Failed }

    class Outcome
    {
        public OutcomeKind Kind;
        public string Error = "";

        public bool Ok
        {
            get { return Kind == OutcomeKind.AlreadyThere || Kind == OutcomeKind.Switched || Kind == OutcomeKind.SentUnverified; }
        }

        public string Text
        {
            get
            {
                switch (Kind)
                {
                    case OutcomeKind.AlreadyThere: return "already on target";
                    case OutcomeKind.Switched: return "switched (verified)";
                    case OutcomeKind.SentUnverified: return "command sent (not verified)";
                    case OutcomeKind.NotFound: return "monitor not found / not reachable from this PC";
                    case OutcomeKind.Cancelled: return "cancelled by newer request";
                    default: return "FAILED: " + Error;
                }
            }
        }

        public static Outcome Of(OutcomeKind k) { return new Outcome { Kind = k }; }
    }

    /// Performs DDC input switches for all configured monitors. Serialises requests; a newer request cancels an older one.
    /// Mirrors macos/Sources/mas/Switcher.swift.
    class Switcher
    {
        public Config Config;
        readonly object runLock = new object();
        int generation;

        public Switcher(Config config) { Config = config; }

        public static string Label(Target t) { return t == Target.Mac ? "Mac" : "PC"; }

        /// Runs on a worker thread; `completion` is called on that thread.
        public void SwitchAll(Target target, string reason, Action<string> completion)
        {
            int gen = Interlocked.Increment(ref generation);
            var config = Config;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string summary;
                lock (runLock) { summary = Run(target, reason, config, gen); }
                if (completion != null) completion(summary);
            });
        }

        public string SwitchAllSync(Target target, string reason)
        {
            int gen = Interlocked.Increment(ref generation);
            lock (runLock) { return Run(target, reason, Config, gen); }
        }

        string Run(Target target, string reason, Config config, int gen)
        {
            if (gen != Volatile.Read(ref generation)) return "cancelled";
            var sw = Stopwatch.StartNew();
            Log.Info("Switching monitors to " + Label(target) + " (" + reason + ")");
            if (config.Monitors.Count == 0)
            {
                Log.Error("No [[monitors]] configured; nothing to switch");
                return "no monitors configured";
            }
            var outcomes = new Outcome[config.Monitors.Count];
            Parallel.For(0, config.Monitors.Count, i =>
            {
                var m = config.Monitors[i];
                var o = Apply(m, target, config, gen);
                outcomes[i] = o;
                string line = string.Format("[{0}] -> {1} 0x{2:X2}: {3}", m.Name, Label(target), m.InputFor(target), o.Text);
                if (o.Ok) Log.Info(line); else Log.Warn(line);
            });
            int ok = outcomes.Count(o => o.Ok);
            string summary = string.Format("{0}: {1}/{2} ok in {3} ms", Label(target), ok, outcomes.Length, sw.ElapsedMilliseconds);
            Log.Info("Switch done - " + summary);
            return summary;
        }

        Outcome Apply(MonitorConfig m, Target target, Config config, int gen)
        {
            uint want = m.InputFor(target);
            bool wroteOK = false, everFound = false;
            string lastError = "";
            var delays = config.RetryDelaysMs.Count == 0 ? new List<int> { 300 } : config.RetryDelaysMs;

            // attempt 0: idempotency read + first write; attempts 1..retries: read back (verify) then re-write if needed.
            for (int attempt = 0; attempt <= config.Retries; attempt++)
            {
                if (attempt > 0) Thread.Sleep(delays[Math.Min(attempt - 1, delays.Count - 1)]);
                if (gen != Volatile.Read(ref generation)) return Outcome.Of(OutcomeKind.Cancelled);

                // Re-enumerate every attempt: monitors come and go during hot-plug, and handles go stale.
                var all = DisplayMonitor.Discover();
                try
                {
                    var d = all.FirstOrDefault(m.Matches);
                    if (d == null)
                    {
                        Log.Debug(string.Format("[{0}] attempt {1}: not found among desktop monitors", m.Name, attempt));
                        if (wroteOK) return Outcome.Of(OutcomeKind.SentUnverified);   // likely dropped off after switching away from us
                        continue;
                    }
                    everFound = true;

                    bool readFailed = false;
                    if (m.ReliableReadback) try
                    {
                        uint max;
                        uint cur = d.Read(m.VcpCode, out max);
                        Log.Debug(string.Format("[{0}] read VCP 0x{1:X2} = 0x{2:X2}", m.Name, m.VcpCode, cur));
                        if (InputMatches(cur, want))
                            return Outcome.Of(attempt == 0 ? OutcomeKind.AlreadyThere : OutcomeKind.Switched);
                        if (attempt > 0) Log.Warn(string.Format("[{0}] readback 0x{1:X2} != 0x{2:X2}, retrying", m.Name, cur, want));
                    }
                    catch (DdcException e)
                    {
                        Log.Debug("[" + m.Name + "] read failed: " + e.Message);
                        lastError = e.Message;
                        readFailed = true;
                    }
                    if (attempt == config.Retries) break;   // last pass is verification only
                    // Monitors often refuse DDC for 1-2 s while changing input. After a successful write, an
                    // unreadable monitor is still busy: wait for the next check instead of re-sending (which can restart the switch).
                    if (wroteOK && readFailed) continue;

                    try
                    {
                        d.Write(m.VcpCode, want);
                        wroteOK = true;
                        Log.Info(string.Format("[{0}] wrote VCP 0x{1:X2} = 0x{2:X2} (attempt {3})", m.Name, m.VcpCode, want, attempt + 1));
                        if (!config.VerifyReadback || !m.ReliableReadback) return Outcome.Of(OutcomeKind.SentUnverified);
                    }
                    catch (DdcException e)
                    {
                        lastError = e.Message;
                        Log.Warn(string.Format("[{0}] write attempt {1} failed: {2}", m.Name, attempt + 1, e.Message));
                    }
                }
                finally { DisplayMonitor.DisposeAll(all); }
            }
            if (wroteOK) return Outcome.Of(OutcomeKind.SentUnverified);
            if (!everFound) return Outcome.Of(OutcomeKind.NotFound);
            return new Outcome { Kind = OutcomeKind.Failed, Error = lastError };
        }

        /// Some monitors set garbage in the high byte of VCP 0x60; compare the low byte when the target fits in one.
        public static bool InputMatches(uint current, uint want)
        {
            return want <= 0xFF ? (current & 0xFF) == want : current == want;
        }
    }
}
