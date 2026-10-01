#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.SimNative
{
    /// <summary>Collects the sim's warnings (bounded) so a rejected scenario's reason reaches <c>chimera_last_error</c>.</summary>
    internal sealed class CollectingLog : ILogSink
    {
        private const int MaxLines = 16;
        private readonly List<string> _warnings = new List<string>();
        public void Info(string message) { }
        public void Warn(string message) { if (_warnings.Count < MaxLines) _warnings.Add(message); }
        public string Joined() => _warnings.Count == 0 ? "" : " | " + string.Join(" | ", _warnings);
    }

    /// <summary>One live session: the shared <see cref="SimSession"/> composition plus the ABI's own bookkeeping.</summary>
    internal sealed class NativeSession
    {
        public readonly int Id;
        public readonly SimSession Sim;
        /// <summary>The five pre-tick hashes and tick0, computed once at creation (before any step).</summary>
        public readonly PreTickHashes Pre;
        /// <summary>The last error message of this session (kept until the next error).</summary>
        public string LastError = "";
        /// <summary>The tick of the last scheduled checksum fold (0 = none yet), recorded by the loop's own checksum sink.</summary>
        public uint LastChecksumTick;
        /// <summary>The hash of that fold (SimChecksum.Compute), recorded with <see cref="LastChecksumTick"/>.</summary>
        public uint LastChecksumHash;
        /// <summary>Set when submit_order or step threw: the world may be part-stepped, so every later mutator refuses
        /// with -6 and <see cref="FaultMessage"/>. Reads stay available for diagnosis.</summary>
        public bool Faulted;
        /// <summary>The message of the exception that faulted the session.</summary>
        public string FaultMessage = "";

        public NativeSession(int id, SimSession sim, PreTickHashes pre)
        {
            Id = id; Sim = sim; Pre = pre;
            // The loop's own sink fires only when it really folded (interval hit AND checksum stores wired), so the
            // (tick, hash) pair can never be a new tick with a stale hash. A host-side observer: nothing it writes is folded.
            sim.Host.SetChecksumSink((tick, hash) => { LastChecksumTick = tick; LastChecksumHash = hash; });
        }
    }

    /// <summary>The process-wide session table, error slot and lock. Every export takes <see cref="Gate"/>, and so does
    /// <see cref="SetError"/> (Monitor is re-entrant), so the catch blocks that run after an export's lock is released
    /// still write the error slots under the lock.</summary>
    internal static class SessionTable
    {
        /// <summary>The one process-wide lock (no export runs concurrently with another).</summary>
        public static readonly object Gate = new object();

        private static readonly Dictionary<int, NativeSession> s_sessions = new Dictionary<int, NativeSession>();
        private static int s_nextId = 1;

        /// <summary>The process-wide last error (create failures, bad session ids).</summary>
        public static string ProcessError = "";

        public static int LiveCount => s_sessions.Count;

        public static NativeSession Add(SimSession sim, PreTickHashes pre)
        {
            var s = new NativeSession(s_nextId++, sim, pre);
            s_sessions.Add(s.Id, s);
            return s;
        }

        public static bool TryGet(int id, out NativeSession session)
        {
            if (s_sessions.TryGetValue(id, out NativeSession? s)) { session = s; return true; }
            session = null!;
            return false;
        }

        public static bool Remove(int id) => s_sessions.Remove(id);

        /// <summary>Record <paramref name="message"/> on the session when it exists, else process-wide. Never throws.</summary>
        public static void SetError(int sessionId, string message)
        {
            try
            {
                lock (Gate)
                {
                    if (sessionId != 0 && s_sessions.TryGetValue(sessionId, out NativeSession? s)) s.LastError = message;
                    else ProcessError = message;
                }
            }
            catch (Exception) { /* nothing left to do */ }
        }
    }
}
