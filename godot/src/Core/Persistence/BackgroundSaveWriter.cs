#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// DW-467 — the off-game-thread half of an SP save. <see cref="SaveGameState.CaptureFrom"/> must run ON the game
    /// thread (it reads live mutable sim stores), but everything after it — <see cref="SaveGameFile.Write"/>'s body
    /// serialization and the <see cref="ISaveStore"/> disk write (blocking <c>File.WriteAllBytes</c> +
    /// <c>File.Replace</c>) — only reads the ALREADY-CAPTURED buffers, which <c>CaptureFrom</c> deep-copies off the
    /// sim (every array freshly allocated, reference-typed lanes round-tripped by id or cloned). This writer runs that
    /// half on a background thread-pool chain so the 120 s autosave (and a manual save) no longer produces a
    /// serialization + disk-I/O frame hitch at the 500-2000 entity target.
    ///
    /// <para><b>Contract:</b> the <paramref name="state"/>/<paramref name="header"/> passed to <see cref="Enqueue"/>
    /// are OWNED by the writer from that point — the caller must not mutate them (the game-thread capture path never
    /// does; it builds both fresh per save). Writes are chained FIFO in issue order (never concurrent), so two saves
    /// to the same slot land last-issued-wins and the store's temp-file + atomic-replace dance is never raced.
    /// Completion (success or failure) is reported through a result queue the game thread drains once per frame
    /// (<see cref="TryDequeueResult"/>) — a background write failure is surfaced as a toast exactly like the old
    /// synchronous path's catch, never swallowed. Godot-free, Tier-1 testable.</para>
    /// </summary>
    public sealed class BackgroundSaveWriter
    {
        /// <summary>The outcome of one background save write, drained on the game thread for the player-facing
        /// toast/log. <see cref="Error"/> is null on success.</summary>
        public readonly struct SaveResult
        {
            /// <summary>The slot the save was written to (e.g. "0", "autosave").</summary>
            public string Slot { get; }
            /// <summary>The sim tick stamped in the save header (for the completion log line).</summary>
            public uint Tick { get; }
            /// <summary>True when the serialize + store write completed; false when either threw.</summary>
            public bool Success { get; }
            /// <summary>The failure message (disk full, replace conflict, permissions, …); null on success.</summary>
            public string? Error { get; }

            public SaveResult(string slot, uint tick, bool success, string? error)
            { Slot = slot; Tick = tick; Success = success; Error = error; }
        }

        private readonly ISaveStore _store;
        private readonly object _gate = new();
        private readonly ConcurrentQueue<SaveResult> _completed = new();
        /// <summary>The FIFO write chain. Each enqueue continues off the previous task, so writes are strictly
        /// serialized in issue order on thread-pool threads (never the game thread, never concurrent with each other).</summary>
        private Task _chain = Task.CompletedTask;
        private int _pending;

        public BackgroundSaveWriter(ISaveStore store) =>
            _store = store ?? throw new ArgumentNullException(nameof(store));

        /// <summary>Saves issued but not yet completed (their result not yet enqueued). 0 = idle.</summary>
        public int PendingCount => Volatile.Read(ref _pending);

        /// <summary>Queue one save: serialize <paramref name="state"/> under <paramref name="header"/> and write the
        /// bytes to <paramref name="slot"/>, all on a background thread. Returns immediately — the calling (game)
        /// thread never blocks on serialization or disk I/O. The buffers are owned by the writer after this call.</summary>
        public void Enqueue(string slot, SaveGameState state, SaveGameHeaderData header)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (header == null) throw new ArgumentNullException(nameof(header));

            Interlocked.Increment(ref _pending);
            lock (_gate)
            {
                // TaskContinuationOptions.None: run whether the predecessor completed or faulted (RunOne never
                // throws, but a faulted link must never wedge every later save). Explicit TaskScheduler.Default so
                // no ambient scheduler is ever inherited.
                _chain = _chain.ContinueWith(
                    _ => RunOne(slot, state, header),
                    CancellationToken.None,
                    TaskContinuationOptions.DenyChildAttach,
                    TaskScheduler.Default);
            }
        }

        /// <summary>Serialize + write one save; never throws (failures become a <see cref="SaveResult"/>).</summary>
        private void RunOne(string slot, SaveGameState state, SaveGameHeaderData header)
        {
            try
            {
                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    SaveGameFile.Write(ms, state, header);
                    bytes = ms.ToArray();
                }
                _store.Write(slot, bytes);
                _completed.Enqueue(new SaveResult(slot, header.Tick, true, null));
            }
            catch (Exception ex)
            {
                _completed.Enqueue(new SaveResult(slot, header.Tick, false, ex.Message));
            }
            finally
            {
                Interlocked.Decrement(ref _pending);
            }
        }

        /// <summary>Dequeue one completed save outcome (drained per frame on the game thread). False = none pending.</summary>
        public bool TryDequeueResult(out SaveResult result) => _completed.TryDequeue(out result);

        /// <summary>Block until every save issued so far has completed (its store write finished, success or failure).
        /// Used before a load reads a slot (save → immediate load must see the newest bytes) and at teardown so a
        /// pending autosave is never cut off by process exit. Returns false if <paramref name="timeoutMs"/> elapsed
        /// first. Results still need draining via <see cref="TryDequeueResult"/> afterwards.</summary>
        public bool WaitForIdle(int timeoutMs = Timeout.Infinite)
        {
            Task chain;
            lock (_gate) chain = _chain;
            try { return chain.Wait(timeoutMs); }
            catch (AggregateException) { return true; } // a faulted link is complete (defensive; RunOne never throws)
        }
    }
}
