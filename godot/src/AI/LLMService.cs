#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl; // NodeKinds — the closed flat trigger vocabulary (internal, same assembly)

namespace ProjectChimera.AI
{
    /// <summary>
    /// Context injected into LLM prompts so the model knows which units and factions
    /// are available in the current scenario.
    /// </summary>
    public class ScenarioContext
    {
        /// <summary>All unit IDs available across both factions (e.g. "worker", "melee", "archer").</summary>
        public string[] UnitIds { get; set; } = Array.Empty<string>();

        /// <summary>Half-width of the playable map in world units. Spawn points must be within ±Bounds.</summary>
        public float MapBounds { get; set; } = 120f;

        /// <summary>
        /// DW-627 (optional, TRUSTED) — the per-slot resolved <see cref="FactionDefinition"/>s, indexed by
        /// <c>(int)Faction</c> = slot + 1, i.e. the SAME array shape <see cref="ScenarioValidator.Validate"/> takes.
        /// Threading it lets a generated trigger's <c>building_type</c> name an authored custom building of the
        /// event/condition's own faction, exactly as the DW-170 load gate allows for hand-authored triggers. NULL
        /// (the default) restricts the check to built-in <see cref="BuildingType"/> enum names — the same amnesty
        /// the load gate applies when no defs are threaded. Never sourced from the generated (untrusted) output.
        /// </summary>
        public IReadOnlyList<FactionDefinition?>? SlotFactionDefs { get; set; }
    }

    /// <summary>
    /// Context injected into map generation prompts — tells the LLM which factions
    /// and unit types are available, the playable bounds, and the faction JSON paths.
    /// </summary>
    public class MapGeneratorContext
    {
        /// <summary>All unit IDs available across both factions (e.g. "worker", "melee").</summary>
        public string[] UnitIds { get; set; } = Array.Empty<string>();

        /// <summary>Half-width of the playable map in world units. Positions must be within ±MapBounds.</summary>
        public float MapBounds { get; set; } = 120f;

        /// <summary>res:// path to the faction JSON for slot 0 (Player 1).</summary>
        public string Slot0FactionJson { get; set; } = "res://resources/data/factions/alpha_faction.json";

        /// <summary>res:// path to the faction JSON for slot 1 (Player 2).</summary>
        public string Slot1FactionJson { get; set; } = "res://resources/data/factions/beta_faction.json";

        // ── Story 8.3 — the three RTS clamps, parameterized out of ValidateScenario/BuildMapSystemPrompt onto this
        //    TRUSTED context (editor/caller-supplied). Defaults reproduce today's RTS behavior exactly, so RTS output
        //    is byte-for-byte unchanged; a non-RTS caller supplies relaxed values. NONE of these is ever sourced from
        //    the parsed (untrusted) scenario file — that would weaken the validation gate circularly. DW-371 landed
        //    the promised populator: <see cref="ScenarioTypeRegistry.Apply"/> writes all three (plus
        //    <see cref="ScenarioType"/>) from a selected type's trusted preset. ──

        // ── DW-372 — lower-bound guards on the two integer clamps ─────────────────────────────────────────────
        //
        // Story 8.3 shipped both as bare auto-properties, so nonsense values were accepted in silence:
        //   • MinPlayerSlots = 0 admitted a scenario declaring an EMPTY player_slots array — it satisfied
        //     "0 slots >= 0", skipped the DW-373 index loop entirely, and (with no units/buildings to fail the
        //     DW-542 reference check) validated clean, even though every downstream faction/spawn path assumes at
        //     least one player.
        //   • MaxCombatUnitsPerSlot < 0 produced the nonsense reject message "…(max -1)" and made the prompt ask
        //     for "at most -1 combat units".
        // Both are CLAMPED ON SET rather than checked at the point of use, so the prompt and the gate can only ever
        // read the SAME effective number — the prompt/gate divergence class this whole entry exists to close.

        /// <summary>DW-372: the floor for <see cref="MinPlayerSlots"/>. A scenario with no player slot at all is not
        /// authorable, so one player slot is the effective minimum however the caller sets the clamp.</summary>
        public const int MinPlayerSlotsFloor = 1;

        /// <summary>DW-372: the floor for <see cref="MaxCombatUnitsPerSlot"/>. Zero is a MEANINGFUL cap ("no
        /// pre-placed combat units at all"); anything below it is not.</summary>
        public const int MaxCombatUnitsPerSlotFloor = 0;

        private int _minPlayerSlots        = 2;
        private int _maxCombatUnitsPerSlot = 6;

        /// <summary>Story 8.3: the minimum number of player slots a valid scenario must declare. RTS default 2;
        /// <see cref="ValidateScenario"/> rejects fewer. Trusted (never read from the scenario file).
        /// DW-372: clamped on set into [<see cref="MinPlayerSlotsFloor"/>, <see cref="FactionRegistry.PLAYER_COUNT"/>]
        /// — below the floor an empty player_slots array validates clean; above the sim's player ceiling the floor is
        /// unsatisfiable by any loadable scenario (<see cref="ScenarioValidator"/> rejects a slot index ≥
        /// PLAYER_COUNT), so every generation would be rejected no matter what the model returned.</summary>
        public int MinPlayerSlots
        {
            get => _minPlayerSlots;
            set => _minPlayerSlots =
                  value < MinPlayerSlotsFloor          ? MinPlayerSlotsFloor
                : value > FactionRegistry.PLAYER_COUNT ? FactionRegistry.PLAYER_COUNT
                : value;
        }

        /// <summary>Story 8.3: the maximum pre-placed combat (non-worker) units allowed per faction slot. RTS default
        /// 6; <see cref="ValidateScenario"/> rejects more. Trusted (never read from the scenario file).
        /// DW-372: clamped on set to at least <see cref="MaxCombatUnitsPerSlotFloor"/>.</summary>
        public int MaxCombatUnitsPerSlot
        {
            get => _maxCombatUnitsPerSlot;
            set => _maxCombatUnitsPerSlot =
                value < MaxCombatUnitsPerSlotFloor ? MaxCombatUnitsPerSlotFloor : value;
        }

        /// <summary>Story 8.3: the TRUSTED per-slot faction-JSON resolver. NULL ⇒ the built-in default described on
        /// <see cref="ResolveFactionJson"/> (even slots → <see cref="Slot0FactionJson"/>, odd slots →
        /// <see cref="Slot1FactionJson"/> since DW-372 made it total). A caller wanting a different mapping supplies
        /// its own trusted one. <see cref="ValidateScenario"/> OVERWRITES each slot's hallucinated
        /// <c>faction_json</c> from this resolver, so the untrusted file never dictates the path.</summary>
        public Func<int, string>? FactionJsonResolver { get; set; }

        /// <summary>DW-371: the scenario type this context's clamps were populated for, set by
        /// <see cref="ScenarioTypeRegistry.Apply"/>. Editor-side and IN-MEMORY ONLY — never written into or read
        /// back out of a scenario file (the Story 8.3 "Never" constraint). Drives only the per-type guidance block
        /// in <see cref="BuildMapSystemPrompt"/>; the gate itself reads the three clamp fields above, so a caller
        /// that sets them by hand keeps working unchanged.</summary>
        // NOTE: fully-qualified initializer — the property deliberately shares its name with its enum type, so the
        // simple name would be the "Color Color" ambiguity inside an instance initializer.
        public ScenarioType ScenarioType { get; set; } = ProjectChimera.AI.ScenarioType.Rts;

        /// <summary>
        /// DW-627 (optional, TRUSTED) — the per-slot resolved <see cref="FactionDefinition"/>s, indexed by
        /// <c>(int)Faction</c> = slot + 1, i.e. the SAME array shape <see cref="ScenarioValidator.Validate"/> takes.
        /// Threading it lets a generated scenario pre-place an authored CUSTOM building (a lowercase building-def id
        /// in the owning slot's faction), exactly as the Story 6.8 load gate allows for hand-authored maps. NULL (the
        /// default) restricts the check to built-in <see cref="BuildingType"/> enum names — the same amnesty the load
        /// gate applies when no defs are threaded. Never sourced from the generated (untrusted) file.
        /// </summary>
        public IReadOnlyList<FactionDefinition?>? SlotFactionDefs { get; set; }

        /// <summary>Resolve the trusted faction-JSON path for the given 0-based <paramref name="slot"/>, honoring
        /// <see cref="FactionJsonResolver"/> when set else the built-in default mapping.
        ///
        /// DW-372 — the default is now TOTAL: it ALTERNATES (even slot → <see cref="Slot0FactionJson"/>, odd slot →
        /// <see cref="Slot1FactionJson"/>) instead of the original <c>slot == 0 ? Slot0 : Slot1</c>, which silently
        /// collapsed slots 1, 2, 3 … onto ONE faction. That collapse was invisible while nothing could declare more
        /// than two slots, but a caller relaxing <see cref="MinPlayerSlots"/> without supplying a resolver turned a
        /// 4-slot map into a 1-vs-3 with no warning. The two mappings AGREE on slots 0 and 1, so every two-slot
        /// scenario — i.e. every scenario any shipping caller produces — resolves exactly as it always did; only
        /// slots ≥ 2, which were previously wrong, change.</summary>
        public string ResolveFactionJson(int slot)
            => FactionJsonResolver != null
                ? FactionJsonResolver(slot)
                : (slot % 2 == 0 ? Slot0FactionJson : Slot1FactionJson);
    }

    /// <summary>
    /// Translates natural language descriptions into validated TriggerDefinition / ScenarioData JSON, routing every
    /// generation call through the Story 8.2 <see cref="ILLMProvider"/> stack via
    /// <see cref="LlmProviderFactory.TryCreate"/>. The selected provider is AUTHORITATIVE — on failure that provider's
    /// error is surfaced and NO other provider is attempted (the old implicit Claude→Ollama fallback is gone).
    ///
    /// Pure C# — no Godot dependency. The API key is read ONLY through <see cref="ISecretStore"/> (via the factory),
    /// never a property / <c>[Export]</c> field / settings. The owned <see cref="HttpClient"/> is built with
    /// <c>AllowAutoRedirect=false</c> (a real key now flows through it — closes the cross-host redirect key-leak).
    /// Follows the ConcurrentQueue/DrainEvents pattern used by NakamaService and ModIoService.
    /// Call DrainEvents() once per _Process frame to marshal callbacks to the main thread.
    ///
    /// DW-375 — the service OWNS unmanaged-backed state and is therefore <see cref="IDisposable"/>: the seven per-flow
    /// <see cref="CancellationTokenSource"/>es (one per generate entry point) and, on the <c>http: null</c> construction
    /// path, the <see cref="HttpClient"/>/<see cref="HttpClientHandler"/> pair it builds. A superseded token source is
    /// now cancelled AND disposed at the point of reassignment (<see cref="ReplaceTokenSource"/>) instead of being
    /// abandoned once per Generate press, and <see cref="Dispose"/> releases everything the service owns. An INJECTED
    /// client is never disposed — the caller that supplied it owns its lifetime.
    /// </summary>
    public sealed class LLMService : IDisposable
    {
        // ── Configuration ─────────────────────────────────────────────────────

        private const int    MAX_TOKENS    = 2048;
        // A faction draft echoes the full unit schema for EVERY unit in a playable roster (plus buildings), so the
        // single-entity 2048 budget truncates it mid-JSON (→ a generic "Invalid JSON" reject). Give it more headroom.
        private const int    FACTION_DRAFT_MAX_TOKENS = 8192;
        private const int    TIMEOUT_MS    = 30_000;

        // Safety cap: spawn_unit count is clamped to this in validation, independently
        // of the schema comment in the prompt.
        private const int    MAX_SPAWN_COUNT = 50;

        /// <summary>
        /// DW-627 — the built-in building-type choices as the prompts spell them (<c>"A"|"B"|…</c>), DERIVED from the
        /// one <see cref="ScenarioValidator.PlaceableBuildingTypeNames"/> vocabulary the gates enforce. Both prompt
        /// builders hardcoded a 4-name list that went stale when Story 2.8 appended <c>Aviary</c>, so the model was
        /// told a built-in did not exist while the (equally stale) gate rejected it if the model guessed it anyway.
        /// A member added to <see cref="BuildingType"/> now reaches the request and both gates in the same edit.
        /// </summary>
        internal static readonly string BuildingTypeChoices =
            string.Join("|", ScenarioValidator.PlaceableBuildingTypeNames.Select(n => $"\"{n}\""));

        // ── Internal state ────────────────────────────────────────────────────

        private readonly HttpClient _http;
        /// <summary>DW-375: true only when this service BUILT <see cref="_http"/> (the <c>http: null</c> path) and is
        /// therefore responsible for disposing it. An injected client belongs to its caller and is left alone.</summary>
        private readonly bool _ownsHttp;
        private readonly Func<SettingsData> _getSettings;
        private readonly ISecretStore _secretStore;
        private readonly ConcurrentQueue<Action> _queue = new();
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _mapCts;
        /// <summary>DW-375: set once by <see cref="Dispose"/>. Written and read on the caller (Godot main) thread —
        /// the worker tasks never touch it.</summary>
        private bool _disposed;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Story 8.3: construct the service with the Godot-free seams the provider stack needs — a settings accessor
        /// (the authoritative selected provider/model/base-URL), the <see cref="ISecretStore"/> (the ONLY key source),
        /// and an optional injected <see cref="HttpClient"/> (the unit-test seam over a stub handler). When
        /// <paramref name="http"/> is null an owned client is built with <c>AllowAutoRedirect=false</c>.
        ///
        /// DW-375: which of the two branches ran is recorded in <see cref="_ownsHttp"/> — <see cref="Dispose"/> disposes
        /// the client ONLY on the owned branch, so a caller-injected client (every Tier-1 test, and any future shared
        /// client) is never torn down under its owner.
        /// </summary>
        public LLMService(Func<SettingsData> getSettings, ISecretStore secretStore, HttpClient? http = null)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));

            if (http != null)
            {
                _http     = http;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient(BuildOwnedHttpHandler())
                { Timeout = TimeSpan.FromMilliseconds(TIMEOUT_MS) };
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectChimera/1.0");
                _ownsHttp = true;
            }
        }

        /// <summary>
        /// Story 8.3: the owned-client message handler, factored out so the <c>AllowAutoRedirect=false</c> hardening is
        /// directly Tier-1 assertable (the owned path is never taken when a stub client is injected). A real key now
        /// flows through this client via the provider adapters, so redirects are refused: the host allowlist is enforced
        /// only against the initial base URL, and .NET does NOT strip a custom <c>x-api-key</c> header on a cross-host
        /// redirect — mirrors the evaluator client 8.2 hardened.
        /// </summary>
        internal static HttpClientHandler BuildOwnedHttpHandler() => new() { AllowAutoRedirect = false };

        // ── Lifecycle (DW-375) ────────────────────────────────────────────────

        /// <summary>
        /// DW-375 — install a FRESH <see cref="CancellationTokenSource"/> in <paramref name="slot"/>, cancelling and
        /// DISPOSING whatever was there, and return the new token. Every generate entry point reassigns its per-flow
        /// source through here. Before this, each press did <c>slot?.Cancel(); slot = new()</c> — the superseded source
        /// was left to the GC undisposed, so a session leaked one <see cref="CancellationTokenSource"/> (plus any wait
        /// handle / linked-registration state it had materialised) per Generate press.
        ///
        /// The two orderings below are both load-bearing:
        /// <list type="bullet">
        ///   <item>the fresh source is PUBLISHED BEFORE the old one is torn down, so a <see cref="Cancel"/> racing the
        ///         reassignment can never observe a disposed source — <c>CancellationTokenSource.Cancel</c> throws
        ///         <see cref="ObjectDisposedException"/> after disposal;</item>
        ///   <item>the old source is CANCELLED BEFORE it is disposed, which is what makes disposing a source whose token
        ///         a still-unwinding request holds safe. A cancelled token stays permanently "cancellation requested",
        ///         and every <see cref="CancellationToken"/> member such a request touches — <c>IsCancellationRequested</c>,
        ///         <c>ThrowIfCancellationRequested</c>, <c>Register</c>/<c>UnsafeRegister</c>, the
        ///         <c>CreateLinkedTokenSource</c> that <c>LlmHttp.SendAsync</c> wraps the body read in, and
        ///         <c>HttpClient.SendAsync</c> itself — short-circuits on that state instead of checking disposal. Only
        ///         <c>Token</c>, <c>Cancel</c> and <c>WaitHandle</c> throw once disposed, and none of them is reachable
        ///         from an in-flight generation. <c>LlmServiceLifecycleTests</c> pins that framework contract.</item>
        /// </list>
        /// </summary>
        private static CancellationToken ReplaceTokenSource(ref CancellationTokenSource? slot)
        {
            CancellationTokenSource? superseded = slot;
            var fresh = new CancellationTokenSource();
            slot = fresh;

            if (superseded != null)
            {
                superseded.Cancel();    // cancel BEFORE dispose — see the ordering note above
                superseded.Dispose();
            }
            return fresh.Token;
        }

        /// <summary>
        /// DW-375 — cancel and dispose the source in <paramref name="slot"/>, leaving the slot EMPTY. Emptying it is what
        /// keeps a <see cref="Cancel"/> / <see cref="CancelScenario"/> / <see cref="CancelDrafts"/> /
        /// <see cref="CancelBalanceAnalysis"/> call after <see cref="Dispose"/> a silent no-op instead of an
        /// <see cref="ObjectDisposedException"/> out of <c>CancellationTokenSource.Cancel</c>.
        /// </summary>
        private static void CancelAndDisposeTokenSource(ref CancellationTokenSource? slot)
        {
            CancellationTokenSource? source = slot;
            slot = null;
            if (source == null) return;
            source.Cancel();
            source.Dispose();
        }

        /// <summary>DW-375 — guard for the generate entry points. A request issued after <see cref="Dispose"/> would run
        /// against a disposed client, so fail loudly at the call site rather than surfacing an opaque "Cannot access a
        /// disposed object" through the async error callback a frame later.</summary>
        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LLMService));
        }

        /// <summary>
        /// DW-375 — release everything this service owns: every per-flow <see cref="CancellationTokenSource"/> (cancelled
        /// first, so an in-flight request unwinds through its own cancellation path instead of into a disposed client),
        /// and — ONLY when the service built it (the <c>http: null</c> construction path) — the owned
        /// <see cref="HttpClient"/>. Disposing that client also disposes the <see cref="HttpClientHandler"/> it was built
        /// over, because <c>new HttpClient(handler)</c> takes ownership of the handler. An INJECTED client is left
        /// untouched.
        ///
        /// Idempotent. Afterwards the Cancel* methods stay callable (they no-op on the emptied slots) and
        /// <see cref="DrainEvents"/> still flushes anything already queued; the Generate* entry points throw
        /// <see cref="ObjectDisposedException"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancelAndDisposeTokenSource(ref _cts);
            CancelAndDisposeTokenSource(ref _mapCts);
            CancelAndDisposeTokenSource(ref _unitCts);
            CancelAndDisposeTokenSource(ref _abilityCts);
            CancelAndDisposeTokenSource(ref _heroCts);
            CancelAndDisposeTokenSource(ref _factionCts);
            CancelAndDisposeTokenSource(ref _balanceCts);

            if (_ownsHttp) _http.Dispose();
        }

        /// <summary>DW-375 — the per-flow cancellation slots this service owns, one per generate entry point. The ledger
        /// entry named the trigger/scenario pair, but all seven shared the reassign-without-dispose defect.</summary>
        internal enum GenerationFlow
        {
            Trigger,
            Scenario,
            UnitDraft,
            AbilityDraft,
            HeroDraft,
            FactionDraft,
            BalanceAnalysis
        }

        /// <summary>DW-375 Tier-1 seam — internal (not private) for the same reason <see cref="BuildSystemPrompt"/> and
        /// <see cref="BuildOwnedHttpHandler"/> are: the property under test is otherwise unobservable. Returns the source
        /// currently installed for <paramref name="flow"/> (null before that flow's first request and after
        /// <see cref="Dispose"/>), so the lifecycle test can prove a SUPERSEDED source was cancelled and disposed — a
        /// disposed source's <c>Token</c> getter throws, which is the only external evidence of disposal. Nothing in the
        /// shipping code reads this.</summary>
        internal CancellationTokenSource? ActiveTokenSource(GenerationFlow flow) => flow switch
        {
            GenerationFlow.Trigger         => _cts,
            GenerationFlow.Scenario        => _mapCts,
            GenerationFlow.UnitDraft       => _unitCts,
            GenerationFlow.AbilityDraft    => _abilityCts,
            GenerationFlow.HeroDraft       => _heroCts,
            GenerationFlow.FactionDraft    => _factionCts,
            GenerationFlow.BalanceAnalysis => _balanceCts,
            _                              => null
        };

        /// <summary>DW-375 Tier-1 seam: the <see cref="HttpClient"/> this service OWNS (built on the <c>http: null</c>
        /// path), or null when the caller injected one. Every other test injects a client, so this is the only way to
        /// assert that <see cref="Dispose"/> disposes what it owns — and, on the injected path, that it does not.</summary>
        internal HttpClient? OwnedHttpClient => _ownsHttp ? _http : null;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generates a trigger from a natural language description.
        /// The callback is marshalled to the main thread via DrainEvents().
        /// On success: callback(trigger, null). On failure: callback(null, errorMessage).
        /// </summary>
        public void GenerateTriggerAsync(
            string description,
            ScenarioContext context,
            Action<TriggerDefinition?, string?> onComplete)
        {
            ThrowIfDisposed();
            // DW-375: the superseded source is cancelled AND disposed here (see ReplaceTokenSource) — this press used to
            // abandon the previous one undisposed.
            CancellationToken token = ReplaceTokenSource(ref _cts);

            // Snapshot the authoritative settings on the caller thread; the factory reads the provider/model/base-URL
            // from it and the key from the secret store — no fallback, no other provider attempted.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    string prompt = BuildSystemPrompt(context);
                    string msg    = $"Create a trigger for: {description}";

                    // Route through the selected provider only (Story 8.3). A synchronous-unavailable case
                    // (no provider / no key / bad host) short-circuits with the availability message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(prompt, msg, MAX_TOKENS), token);

                    if (!result.Ok)
                    {
                        // The selected provider's failure is surfaced — never masked by another provider — and voiced
                        // with the SAME availability microcopy Test-connection uses (Story 8.3), not a raw adapter string,
                        // so the async failure half of the availability UX matches the synchronous half.
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    // Validate the generated JSON.
                    var (trigger, validationError) = Validate(StripMarkdown(result.Text), context);
                    if (trigger == null)
                    {
                        _queue.Enqueue(() => onComplete(null,
                            $"Generated trigger failed validation: {validationError}"));
                        return;
                    }

                    _queue.Enqueue(() => onComplete(trigger, null));
                }
                catch (OperationCanceledException) { /* silently dropped */ }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        /// <summary>Cancel any in-flight generation request. DW-375: a no-op after <see cref="Dispose"/> — the slot is
        /// emptied there precisely so this stays safe to call during teardown.</summary>
        public void Cancel() => _cts?.Cancel();

        /// <summary>Drain queued main-thread callbacks. Call once per _Process frame.</summary>
        public void DrainEvents()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }

        // ── Validation pipeline ───────────────────────────────────────────────

        /// <summary>
        /// Six-pass validation:
        /// 1. Schema — can JSON be deserialized to TriggerDefinition?
        /// 2. Construct membership (Story 8.3) — every event/condition/action Type is a member of the closed flat
        ///    <see cref="NodeKinds"/> vocabulary; an unknown or graph-only construct is rejected with a LOCATED error.
        /// 3. Faction slots — 0 or 1 only
        /// 4. BuildingType strings — a built-in <see cref="BuildingType"/> enum name, or (when the caller threaded
        ///    <see cref="ScenarioContext.SlotFactionDefs"/>) a custom building id authored by the referencing
        ///    faction — the same two vocabularies the load gate resolves (DW-627)
        /// 5. Operators — only the six standard comparison symbols
        /// 6. Range / safety — counts ≤ 50, durations > 0, spawn inside bounds
        /// Returns (null, errorMessage) on failure, (trigger, null) on success.
        /// </summary>
        public static (TriggerDefinition? trigger, string? error) Validate(
            string json, ScenarioContext context)
        {
            // Pass 1 — schema. DW-526: the shared untrusted-model-output posture (see ContentJson.ModelOutputOptions),
            // NOT a per-call hand-rolled option set — same Fixed quantization boundary, same name-only enum
            // fail-closed, same syntax tolerance as every other LLM parse path in this file.
            TriggerDefinition trigger;
            try
            {
                trigger = JsonSerializer.Deserialize<TriggerDefinition>(json, ContentJson.ModelOutputOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            // Pass 2 — construct membership (Story 8.3). Reject any event/condition/action whose Type is outside the
            // closed FLAT NodeKinds vocabulary, with a LOCATED error (path + offending value) matching the shape
            // ScenarioValidator uses. Driven off the single NodeKinds registry so the LLM gate and the load gate never
            // diverge — a graph-only construct (e.g. custom_event, for_each, order_units) is a flat-channel unknown and
            // is rejected here, not silently accepted.
            var knownEvents     = new HashSet<string>(NodeKinds.EventTypes, StringComparer.Ordinal);
            var knownConditions = new HashSet<string>(NodeKinds.ConditionTypes, StringComparer.Ordinal);
            var knownActions    = new HashSet<string>(NodeKinds.FlatActionTypes, StringComparer.Ordinal);
            for (int j = 0; j < trigger.Events.Length; j++)
                if (!knownEvents.Contains(trigger.Events[j].Type))
                    return (null, $"events[{j}].type='{trigger.Events[j].Type}' is not a known trigger event type.");
            for (int j = 0; j < trigger.Conditions.Length; j++)
                if (!knownConditions.Contains(trigger.Conditions[j].Type))
                    return (null, $"conditions[{j}].type='{trigger.Conditions[j].Type}' is not a known trigger condition type.");
            for (int j = 0; j < trigger.Actions.Length; j++)
                if (!knownActions.Contains(trigger.Actions[j].Type))
                    return (null, $"actions[{j}].type='{trigger.Actions[j].Type}' is not a known trigger action type.");

            // Pass 3 — faction slots.
            foreach (var ev in trigger.Events)
                if (ev.Faction is not (0 or 1))
                    return (null, $"Event '{ev.Type}' has invalid faction slot {ev.Faction} (must be 0 or 1).");
            foreach (var c in trigger.Conditions)
                if (c.Faction is not (0 or 1))
                    return (null, $"Condition '{c.Type}' has invalid faction slot {c.Faction}.");
            foreach (var a in trigger.Actions)
                if (a.Faction is not (0 or 1))
                    return (null, $"Action '{a.Type}' has invalid faction slot {a.Faction}.");

            // Pass 4 — building type strings (DW-627). Resolved through ScenarioValidator's ONE predicate — the same
            // two vocabularies the DW-170 load gate accepts: a built-in BuildingType enum name, OR an authored
            // building-def id in the faction that owns the event/condition's own `faction` slot (its faction
            // qualifier), when the caller threaded the trusted per-slot defs. Before this, the check ran against a
            // PRIVATE 4-member shadow enum declared in this file, so a generated trigger naming a custom building was
            // rejected upstream of the gate that would have accepted it — and, since Story 2.8 appended Aviary to the
            // real enum, the shadow rejected a legitimate BUILT-IN reference too.
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.BuildingType)
                    && !ScenarioValidator.IsKnownBuildingType(
                        ev.BuildingType, ScenarioValidator.OwnerFactionDef(context.SlotFactionDefs, ev.Faction)))
                    return (null, $"Unknown building_type '{ev.BuildingType}'. " +
                        $"Valid: {string.Join(", ", ScenarioValidator.PlaceableBuildingTypeNames)}" +
                        " (or a custom building id authored by that faction).");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.BuildingType)
                    && !ScenarioValidator.IsKnownBuildingType(
                        c.BuildingType, ScenarioValidator.OwnerFactionDef(context.SlotFactionDefs, c.Faction)))
                    return (null, $"Unknown building_type '{c.BuildingType}'.");

            // Pass 5 — operator strings.
            var validOps = new HashSet<string> { ">", "<", ">=", "<=", "==", "!=" };
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.Operator) && !validOps.Contains(ev.Operator))
                    return (null, $"Invalid operator '{ev.Operator}' in event '{ev.Type}'.");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.Operator) && !validOps.Contains(c.Operator))
                    return (null, $"Invalid operator '{c.Operator}' in condition '{c.Type}'.");

            // Pass 6 — range and safety.
            foreach (var a in trigger.Actions)
            {
                if (a.Type == "spawn_unit")
                {
                    if (a.Count <= 0 || a.Count > MAX_SPAWN_COUNT)
                        a.Count = Math.Clamp(a.Count, 1, MAX_SPAWN_COUNT); // auto-clamp rather than reject
                    // Story 7.1: a.X/a.Z are now Fixed. Quantize the map bound to Fixed once (the sanctioned AI
                    // authoring float→Fixed boundary) and compare Fixed-vs-Fixed — no float comparison here.
                    Fixed b = Fixed.FromFloat(context.MapBounds);
                    if (a.X < -b || a.X > b || a.Z < -b || a.Z > b)
                        return (null, $"spawn_unit position ({a.X}, {a.Z}) is outside map bounds ±{context.MapBounds}.");
                }
                if (a.Type == "create_timer" && a.TimerSeconds <= Fixed.Zero)
                    return (null, $"create_timer '{a.TimerName}' has invalid duration {a.TimerSeconds.ToFloat()}s.");
                if (a.Type == "display_message" && a.Duration <= Fixed.Zero)
                    a.Duration = Fixed.FromInt(4); // auto-fix
            }

            return (trigger, null);
        }

        // ── Prompt builder ────────────────────────────────────────────────────

        // Story 8.3: internal (not private) so the Tier-1 staleness-guard test can assert the prompt enumerates every
        // flat NodeKinds construct — a future flat construct added to NodeKinds but not described here fails the guard.
        internal static string BuildSystemPrompt(ScenarioContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "You are a trigger authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine(
                "Convert the user's description into a valid JSON TriggerDefinition object.");
            sb.AppendLine();
            sb.AppendLine("=== TRIGGER SCHEMA ===");
            sb.AppendLine(@"{
  ""name"": ""string"",
  ""enabled"": true,
  ""run_once"": false,
  ""cooldown_seconds"": 0.0,
  ""priority"": 0,
  ""events"": [ TriggerEvent ],
  ""conditions"": [ TriggerCondition ],
  ""actions"": [ TriggerAction ]
}");
            sb.AppendLine();
            // NOTE: every construct string below is a member of NodeKinds.EventTypes / ConditionTypes /
            // FlatActionTypes (Story 7.13 appended the 5 built-in event sources; Story 6.4 added unit_in_region). This
            // description block is HAND-AUTHORED (not derived from NodeKinds) precisely so the staleness-guard test can
            // catch a future NodeKinds addition that was not documented here.
            sb.AppendLine("=== VALID EVENT TYPES ===");
            sb.AppendLine($@"match_start              — no additional fields
unit_dies               — faction (0=Player1, 1=Player2)
building_completed      — faction, building_type ({BuildingTypeChoices})
timer_expires           — timer_name (string)
resource_threshold      — faction, amount (float), operator
unit_count_threshold    — faction, count (int), operator
unit_damaged            — faction — fires when a unit of faction takes damage
unit_trained            — faction — fires when a unit of faction finishes training
ability_cast            — faction — fires when a unit of faction casts an ability
hero_level              — faction — fires when a hero of faction gains a level
player_chat             — faction — fires when a player sends a chat message");
            sb.AppendLine();
            sb.AppendLine("=== VALID CONDITION TYPES ===");
            sb.AppendLine(@"always                  — always true
building_exists         — faction, building_type
resource_comparison     — faction, amount (float), operator
unit_count              — faction, count (int), operator
variable_comparison     — variable (string), value (int), operator
unit_in_region          — faction, region_id (string) — true while a live unit of faction is inside the named region");
            sb.AppendLine();
            sb.AppendLine("=== VALID ACTION TYPES ===");
            sb.AppendLine(@"spawn_unit      — unit_id (string), faction, x (float), z (float), count (int, max 50)
display_message — text (string), duration (float seconds)
victory         — faction (this faction wins)
defeat          — faction (this faction loses, other wins)
create_timer    — timer_name (string), timer_seconds (float)
add_resources   — faction, amount (float ore)
set_variable    — variable (string), value (int)
play_sound      — sound_id (string)");
            sb.AppendLine();
            sb.AppendLine("=== VALID OPERATORS ===");
            sb.AppendLine(@""">"" | ""<"" | "">="" | ""<="" | ""=="" | ""!=""");
            sb.AppendLine();
            sb.AppendLine("=== SCENARIO CONTEXT ===");
            sb.AppendLine($"Available unit IDs: {string.Join(", ", ctx.UnitIds.Select(id => $"\"{id}\""))}");
            sb.AppendLine($"Map bounds: positions must be within ±{ctx.MapBounds} on X and Z axes.");
            sb.AppendLine();
            sb.AppendLine("=== EXAMPLES ===");
            sb.AppendLine(@"Example 1 — ""When the match starts, give Player 1 an extra 200 ore"":
{
  ""name"": ""Bonus Starting Resources"",
  ""enabled"": true,
  ""run_once"": true,
  ""cooldown_seconds"": 0,
  ""priority"": 0,
  ""events"": [{""type"": ""match_start""}],
  ""conditions"": [],
  ""actions"": [{""type"": ""add_resources"", ""faction"": 0, ""amount"": 200}]
}");
            sb.AppendLine();
            sb.AppendLine(@"Example 2 — ""When Player 2 builds a Barracks, spawn 5 enemy soldiers near Player 1's base"":
{
  ""name"": ""Enemy Vanguard"",
  ""enabled"": true,
  ""run_once"": true,
  ""cooldown_seconds"": 0,
  ""priority"": 0,
  ""events"": [{""type"": ""building_completed"", ""faction"": 1, ""building_type"": ""Barracks""}],
  ""conditions"": [],
  ""actions"": [
    {""type"": ""spawn_unit"", ""unit_id"": ""melee"", ""faction"": 1, ""x"": -30, ""z"": 0, ""count"": 5},
    {""type"": ""display_message"", ""text"": ""Enemy reinforcements spotted!"", ""duration"": 5}
  ]
}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        /// <summary>Strip ```json ... ``` markdown fences that some models add.</summary>
        private static string StripMarkdown(string? text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("```"))
            {
                int start = text.IndexOf('\n') + 1;
                int end   = text.LastIndexOf("```");
                if (start > 0 && end > start)
                    text = text.Substring(start, end - start).Trim();
            }
            return text;
        }

        // DW-627: the private 4-member `enum BuildingType { CommandCenter, Barracks, ArcheryRange, SiegeWorkshop }`
        // that used to sit here is GONE. It SHADOWED ProjectChimera.Core.BuildingType (imported above), so every
        // building-type check in this file silently gated against a hand-listed copy that stopped tracking the real
        // enum at Story 2.8 (Aviary) and could never know about an authored custom building. Both gates now resolve
        // through ScenarioValidator.IsKnownBuildingType — one vocabulary for hand-authored, editor-authored and
        // generated content. Do not re-introduce a local BuildingType: the unqualified name must keep binding to the
        // real Core enum.

        // ── Scenario generation ───────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generates a full ScenarioData from a natural language map brief.
        /// Callback is marshalled to the main thread via DrainEvents().
        /// On success: callback(scenario, null). On failure: callback(null, errorMessage).
        /// </summary>
        public void GenerateScenarioAsync(
            string description,
            MapGeneratorContext context,
            Action<ScenarioData?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _mapCts);   // DW-375: cancels AND disposes the superseded source

            // Snapshot the authoritative settings on the caller thread (see GenerateTriggerAsync). No fallback.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    string prompt = BuildMapSystemPrompt(context);
                    string msg    = $"Create a map scenario for: {description}";

                    // Route through the selected provider only (Story 8.3). Synchronous-unavailable short-circuits
                    // with the availability message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(prompt, msg, MAX_TOKENS), token);

                    if (!result.Ok)
                    {
                        // Story 8.3: voice the runtime failure with the shared availability microcopy (see the trigger path).
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    var (scenario, validationError) = ValidateScenario(StripMarkdown(result.Text), context);
                    if (scenario == null)
                    {
                        _queue.Enqueue(() => onComplete(null,
                            $"Generated map failed validation: {validationError}"));
                        return;
                    }

                    _queue.Enqueue(() => onComplete(scenario, null));
                }
                catch (OperationCanceledException)
                {
                    // DW-899 (adjacent defect, found while tracing it): swallowing the cancel WITHOUT enqueueing a
                    // callback meant OnGenerationComplete never ran, so the panel's Generate button — disabled at
                    // issue time and re-enabled only in that callback — stayed disabled for the rest of the session
                    // with no path back. That is the "flag set, never cleared" shape, applied to the button rather
                    // than to close. Report the cancel so every completion path lands in exactly one callback.
                    _queue.Enqueue(() => onComplete(null, "Generation cancelled."));
                }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        /// <summary>Cancel any in-flight scenario generation request. DW-375: a no-op after <see cref="Dispose"/>.</summary>
        public void CancelScenario() => _mapCts?.Cancel();

        /// <summary>
        /// Validate a generated ScenarioData JSON through seven passes:
        /// 1. Schema — the DW-366 upstream byte-size guard (≤ <see cref="ScenarioSerializer.MaxScenarioFileBytes"/>,
        ///    checked BEFORE deserialization), then deserialization succeeds. (UNIVERSAL — always runs.)
        /// 2. Player slots — at least <see cref="MapGeneratorContext.MinPlayerSlots"/> (RTS default 2); slot indices
        ///    unique and within [0, PlayerSlots.Length) (UNIVERSAL — always runs; DW-373); every unit's/building's
        ///    <c>slot</c> references a DECLARED player slot (UNIVERSAL — always runs; DW-542); faction paths
        ///    forced from the TRUSTED per-slot <see cref="MapGeneratorContext.ResolveFactionJson"/> mapping.
        /// 3. Building types — a built-in <see cref="BuildingType"/> enum name, or (when the caller threaded
        ///    <see cref="MapGeneratorContext.SlotFactionDefs"/>) a custom building id authored by the owning slot's
        ///    faction — the same two vocabularies the load gate resolves (DW-627).
        /// 4. Unit IDs — only IDs present in MapGeneratorContext.UnitIds.
        /// 5. Position bounds — all X/Z within ±MapBounds. (UNIVERSAL — always runs.)
        /// 6. Ore node spacing — every pair at least 15 units apart. (UNIVERSAL — always runs.)
        /// 7. Pre-placed unit count — at most <see cref="MapGeneratorContext.MaxCombatUnitsPerSlot"/> (RTS default 6)
        ///    non-worker units per faction slot.
        /// Story 8.3: the three RTS clamps (passes 2 min-slots / 2 faction-path / 7 max-combat) are parameterized from
        /// the TRUSTED <paramref name="context"/> (RTS defaults preserve today's behavior exactly); the universal passes
        /// (1/5/6) always run regardless of clamp values. NO clamp value is ever sourced from the parsed (untrusted)
        /// scenario file. Returns (null, errorMessage) on failure, (scenario, null) on success.
        /// </summary>
        /// <summary>
        /// The text of the line a <see cref="JsonException"/> points at, trimmed and length-capped, as a
        /// <c>" — line N: ..."</c> suffix. Empty when there is no usable line number. Purely diagnostic: it turns an
        /// abstract parser complaint into the model's actual output, which is otherwise discarded with the response.
        /// </summary>
        internal static string DescribeOffendingLine(string json, long? lineNumber)
        {
            if (json == null || lineNumber is not long ln || ln < 0) return "";

            string[] lines = json.Split('\n');
            if (ln >= lines.Length) return "";

            string text = lines[ln].Trim().TrimEnd('\r');
            if (text.Length == 0) return "";
            const int Cap = 160;
            if (text.Length > Cap) text = text.Substring(0, Cap) + "…";
            return $" — line {ln + 1}: {text}";
        }

        public static (ScenarioData? scenario, string? error) ValidateScenario(
            string json, MapGeneratorContext context)
        {
            // Pass 1 — schema (UNIVERSAL).
            ScenarioData scenario;
            try
            {
                // DW-366 — upstream size guard, uniform with ScenarioSerializer.LoadFromFile: an AI-generated
                // scenario string is untrusted input, so cap its byte size BEFORE deserialization materializes any
                // collection (the per-collection gates all run post-parse). Over-cap → the guard's JsonException is
                // caught below and surfaced as the pass-1 validation error.
                ScenarioSerializer.GuardScenarioInputSize(Encoding.UTF8.GetByteCount(json), "generated scenario");
                // DW-526: the shared untrusted-model-output posture (ContentJson.ModelOutputOptions), replacing the
                // per-call option set that used to be built here. It carries the same leniency this site always
                // documented — a model is not a JSON serializer: trailing commas and // comments are its two most
                // common deviations and both are pure syntax, so rejecting them throws away a whole (paid) generation
                // over nothing. Everything about the CONTENT is still decided by passes 2-7 below; being lenient there
                // widens the syntax accepted, never the values trusted. What it ADDS over the old inline set is the
                // name-only enum boundary (a numeric enum now fails closed instead of silently resolving to whichever
                // member holds that ordinal — the same tightening DW-274 gave the scenario FILE format).
                scenario = JsonSerializer.Deserialize<ScenarioData>(json, ContentJson.ModelOutputOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (JsonException jex)
            {
                // Quote the offending line. The raw response is not retained anywhere, so without this the model's
                // actual output dies with the exception and the failure is undiagnosable — exactly what happened to
                // "'4' is an invalid end of a number ... $.resource_nodes[5].max_gatherers" (Alec, 2026-08-04),
                // where the malformed token could not be recovered after the fact.
                return (null, $"Invalid JSON: {jex.Message}{DescribeOffendingLine(json, jex.LineNumber)}");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            // Pass 2 — player slots (Story 8.3: min-slots clamp from the trusted context; RTS default 2).
            if (scenario.PlayerSlots.Length < context.MinPlayerSlots)
                return (null, $"Expected at least {context.MinPlayerSlots} player slots, got {scenario.PlayerSlots.Length}.");

            // DW-373 — slot-index uniqueness + range (UNIVERSAL — structural, independent of the trusted clamp
            // values). Distinct + within [0, PlayerSlots.Length) ⇒ the declared indices form a permutation of
            // 0..Length-1, so the min-slots length check above really guarantees that many DISTINCT players.
            // Without this, an (untrusted) scenario declaring two slots both "slot":0 passes the length-based
            // check, both resolve to the SAME faction via the per-slot resolver below, and Pass 7 merges their
            // combat counts under one key — a degenerate one-faction scenario sails past the gate.
            var declaredSlots = new HashSet<int>();
            for (int i = 0; i < scenario.PlayerSlots.Length; i++)
            {
                int slotIndex = scenario.PlayerSlots[i].Slot;
                if (slotIndex < 0 || slotIndex >= scenario.PlayerSlots.Length)
                    return (null, $"player_slots[{i}].slot={slotIndex} is outside [0, {scenario.PlayerSlots.Length}).");
                if (!declaredSlots.Add(slotIndex))
                    return (null, $"player_slots[{i}].slot={slotIndex} duplicates another player slot — slot indices must be unique.");
            }

            // DW-542 — PLACEMENT slot references (UNIVERSAL — structural, like the declaration check above). DW-373
            // gated the DECLARATION indices only; a generated map could still place a unit or building on slot 3 of a
            // 2-slot scenario. That passed here — "validated" was reported to the creator — and was then rejected at
            // the ScenarioValidator load gate with "references no declared player_slot", so the AI-generation UX
            // promised a scenario the loader refuses. Gate it in the SAME pass that validates the declarations, with
            // the same located shape, so the two gates agree. Runs BEFORE the per-slot faction resolution below, so
            // every later pass (building-type owner resolution, Pass 7's per-slot counts) reads a real declared slot.
            for (int i = 0; i < scenario.Buildings.Length; i++)
                if (!declaredSlots.Contains(scenario.Buildings[i].Slot))
                    return (null, $"buildings[{i}].slot={scenario.Buildings[i].Slot} references no declared player_slot.");
            for (int i = 0; i < scenario.Units.Length; i++)
                if (!declaredSlots.Contains(scenario.Units[i].Slot))
                    return (null, $"units[{i}].slot={scenario.Units[i].Slot} references no declared player_slot.");

            // Force faction JSON paths from the TRUSTED per-slot resolver — LLMs often hallucinate these, and the
            // untrusted file must never dictate the path. RTS default = the existing slot-0/slot-1 mapping.
            foreach (var slot in scenario.PlayerSlots)
                slot.FactionJson = context.ResolveFactionJson(slot.Slot);

            // Pass 3 — building types (DW-627). The hardcoded 4-name set that used to live here has the same defect
            // the file's private shadow enum had: it stopped tracking the real BuildingType at Story 2.8 (Aviary) and
            // knows nothing about authored custom buildings. Resolve through ScenarioValidator's ONE predicate — a
            // built-in enum name, or an authored building-def id in the OWNER slot's faction when the caller threaded
            // the trusted per-slot defs — so a generated map is gated by exactly what the loader will accept.
            for (int i = 0; i < scenario.Buildings.Length; i++)
            {
                ScenarioBuilding b = scenario.Buildings[i];
                if (!ScenarioValidator.IsKnownBuildingType(
                        b.Type, ScenarioValidator.OwnerFactionDef(context.SlotFactionDefs, b.Slot)))
                    return (null, $"Unknown building type '{b.Type}'. " +
                        $"Valid: {string.Join(", ", ScenarioValidator.PlaceableBuildingTypeNames)}" +
                        " (or a custom building id authored by the owning slot's faction).");
            }

            // Pass 4 — unit IDs.
            var validUnits = new HashSet<string>(context.UnitIds, StringComparer.OrdinalIgnoreCase);
            validUnits.Add("worker"); // always present in every faction
            foreach (var u in scenario.Units)
                if (!validUnits.Contains(u.UnitId))
                    return (null, $"Unknown unit_id '{u.UnitId}'. " +
                        $"Valid: {string.Join(", ", context.UnitIds)}");

            // Pass 5 — position bounds (UNIVERSAL — always runs regardless of the clamp values).
            float bounds = context.MapBounds;
            foreach (var slot in scenario.PlayerSlots)
                if (Math.Abs(slot.BaseX) > bounds || Math.Abs(slot.BaseZ) > bounds)
                    return (null, $"Slot {slot.Slot} base ({slot.BaseX}, {slot.BaseZ}) " +
                        $"is outside map bounds ±{bounds}.");

            foreach (var node in scenario.ResourceNodes)
            {
                if (Math.Abs(node.X) > bounds || Math.Abs(node.Z) > bounds)
                    return (null, $"Resource node at ({node.X}, {node.Z}) is outside ±{bounds}.");
                if (node.Supply <= 0) node.Supply = 400f;
                if (node.Rate   <= 0) node.Rate   = 5f;
            }

            foreach (var b in scenario.Buildings)
                if (Math.Abs(b.X) > bounds || Math.Abs(b.Z) > bounds)
                    return (null, $"Building '{b.Type}' at ({b.X}, {b.Z}) is outside ±{bounds}.");

            foreach (var u in scenario.Units)
                if (Math.Abs(u.X) > bounds || Math.Abs(u.Z) > bounds)
                    return (null, $"Unit '{u.UnitId}' at ({u.X}, {u.Z}) is outside ±{bounds}.");

            // Pass 6 — ore node spacing ≥ 15u (UNIVERSAL — always runs regardless of the clamp values).
            for (int i = 0; i < scenario.ResourceNodes.Length; i++)
                for (int j = i + 1; j < scenario.ResourceNodes.Length; j++)
                {
                    float dx = scenario.ResourceNodes[i].X - scenario.ResourceNodes[j].X;
                    float dz = scenario.ResourceNodes[i].Z - scenario.ResourceNodes[j].Z;
                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist < 15f)
                        return (null, $"Ore nodes {i} and {j} are {dist:F1}u apart (minimum 15u).");
                }

            // Pass 7 — pre-placed combat units per slot (Story 8.3: max-combat clamp from the trusted context; RTS
            // default 6).
            var combatCount = new Dictionary<int, int>();
            foreach (var u in scenario.Units)
                if (!string.Equals(u.UnitId, "worker", StringComparison.OrdinalIgnoreCase))
                    combatCount[u.Slot] = combatCount.GetValueOrDefault(u.Slot) + 1;
            foreach (var kv in combatCount)
                if (kv.Value > context.MaxCombatUnitsPerSlot)
                    return (null, $"Slot {kv.Key} has {kv.Value} pre-placed combat units (max {context.MaxCombatUnitsPerSlot}).");

            return (scenario, null);
        }

        // ── Map system prompt ─────────────────────────────────────────────────

        /// <summary>
        /// DW-372 — how many player slots the map prompt's SCHEMA and EXAMPLE blocks render. NEVER fewer than the
        /// <see cref="MapGeneratorContext.MinPlayerSlots"/> floor the PLACEMENT RULES state and
        /// <see cref="ValidateScenario"/> enforces. Before this, both blocks hardcoded exactly two slots, so a caller
        /// raising the floor to 4 told the model "at least 4 player slots" while showing it a 2-slot schema AND a
        /// 2-slot example — the model copies the worked example, emits 2, and the gate rejects EVERY generation.
        /// Two is the floor of the RENDERING (not of the clamp): a 2-slot example satisfies any floor ≤ 2, so the
        /// prompts of every shipped scenario type stay byte-for-byte what they have always been.
        /// </summary>
        internal static int PromptSlotCount(MapGeneratorContext ctx) => Math.Max(2, ctx.MinPlayerSlots);

        /// <summary>
        /// DW-372 — the example base position for 0-based <paramref name="slot"/> of <paramref name="slotCount"/>, on
        /// a ring of radius <paramref name="radius"/> starting due WEST and walking counter-clockwise. Chosen so the
        /// two-slot case is exactly the historical (-45, 0) / (+45, 0) pair while N &gt; 2 spreads the bases evenly
        /// (the symmetry every non-RTS scenario type's guidance asks for). Integer coordinates only — the prompt must
        /// never carry a locale-dependent decimal separator.
        /// </summary>
        private static (int X, int Z) PromptBase(int slot, int slotCount, int radius)
        {
            double angle = Math.PI + slot * (2.0 * Math.PI / slotCount);
            return ((int)Math.Round(radius * Math.Cos(angle)), (int)Math.Round(radius * Math.Sin(angle)));
        }

        /// <summary>
        /// DW-372 — the two example worker positions for a slot whose base is (<paramref name="baseX"/>,
        /// <paramref name="baseZ"/>): <paramref name="offset"/> world units inboard (toward the map centre), then
        /// ±offset along the perpendicular. Returned in ascending (Z, then X) order, which reproduces the historical
        /// (-42,-3), (-42,3), (42,-3), (42,3) block exactly for the two-slot ring.
        /// </summary>
        private static (int X, int Z)[] PromptWorkers(int baseX, int baseZ, int offset)
        {
            double len = Math.Sqrt((double)baseX * baseX + (double)baseZ * baseZ);
            double ux  = len > 0 ? -baseX / len : 1.0;   // unit vector: base → map centre
            double uz  = len > 0 ? -baseZ / len : 0.0;
            double cx  = baseX + ux * offset;
            double cz  = baseZ + uz * offset;
            // Perpendicular of (ux, uz) is (-uz, ux).
            var a = ((int)Math.Round(cx - uz * offset), (int)Math.Round(cz + ux * offset));
            var b = ((int)Math.Round(cx + uz * offset), (int)Math.Round(cz - ux * offset));
            bool aFirst = a.Item2 < b.Item2 || (a.Item2 == b.Item2 && a.Item1 <= b.Item1);
            return aFirst ? new[] { a, b } : new[] { b, a };
        }

        // Story 8.3: internal (not private) so the Tier-1 clamp test can assert the prompt reflects the SAME clamp
        // values ValidateScenario gates against (min player slots + max combat units per slot).
        internal static string BuildMapSystemPrompt(MapGeneratorContext ctx)
        {
            // ── DW-372 — ONE slot count and ONE ring geometry drive every slot-shaped block below: the schema's
            //    player_slots rows and its "slot": 0|1 choice lists, the placement-rule base hints, and the example's
            //    player_slots / CommandCenters / workers. The request therefore cannot contradict itself about how
            //    many players the map has, which is what made a raised MinPlayerSlots unusable. The two-slot
            //    rendering is byte-for-byte the hand-written text it replaces, so no shipping prompt moves.
            int slotCount = PromptSlotCount(ctx);
            // Historical radius is 45 on the default ±120 bounds; deriving it keeps the whole ring inside a caller's
            // tighter bounds instead of emitting an example that fails the prompt's own position rule.
            int radius    = Math.Max(1, (int)Math.Round(Math.Min(45.0, ctx.MapBounds * 0.375)));
            int workerOff = Math.Max(1, Math.Min(3, radius));

            var bases = new (int X, int Z)[slotCount];
            for (int s = 0; s < slotCount; s++) bases[s] = PromptBase(s, slotCount, radius);

            var schemaSlotRows      = new List<string>(slotCount);
            var exampleSlotRows     = new List<string>(slotCount);
            var exampleBuildingRows = new List<string>(slotCount);
            var exampleUnitRows     = new List<string>(slotCount * 2);
            var baseHints           = new StringBuilder();

            for (int s = 0; s < slotCount; s++)
            {
                (int bx, int bz) = bases[s];
                string tail  = s == slotCount - 1 ? "" : ",";
                // The prompt names the SAME per-slot faction path ValidateScenario will force onto the slot, so a
                // non-default resolver (free-for-all / sandbox) is visible to the model instead of being silently
                // rewritten after generation.
                string fjson = ctx.ResolveFactionJson(s);

                schemaSlotRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "    {{ \"slot\": {0}, \"faction_json\": \"{1}\", \"start_ore\": 200.0, " +
                    "\"base_x\": {2,5:0.0}, \"base_z\": {3:0.0} }}{4}", s, fjson, bx, bz, tail));

                exampleSlotRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "    {{ \"slot\": {0}, \"faction_json\": \"{1}\", \"start_ore\": 200, " +
                    "\"base_x\": {2,3}, \"base_z\": {3} }}{4}", s, fjson, bx, bz, tail));

                exampleBuildingRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "    {{ \"type\": \"CommandCenter\", \"slot\": {0}, \"x\": {1,3}, \"z\": {2}, " +
                    "\"pre_built\": true }}{3}", s, bx, bz, tail));

                foreach ((int wx, int wz) in PromptWorkers(bx, bz, workerOff))
                    exampleUnitRows.Add(string.Format(CultureInfo.InvariantCulture,
                        "    {{ \"unit_id\": \"worker\", \"slot\": {0}, \"x\": {1,3}, \"z\": {2,2} }},", s, wx, wz));

                baseHints.AppendFormat(CultureInfo.InvariantCulture,
                    " Player {0} (slot {1}): base near X={2}, Z={3}.", s + 1, s, bx, bz);
            }
            // The last unit row closes its JSON array — drop the trailing comma the loop appends unconditionally.
            exampleUnitRows[^1] = exampleUnitRows[^1].TrimEnd(',');

            // Joined with a bare "\n", NEVER Environment.NewLine: these blocks are spliced INTO verbatim string
            // literals whose newlines come from this source file, so the emitted prompt stays byte-identical on every
            // platform instead of drifting with the host's line ending.
            string schemaSlots      = string.Join("\n", schemaSlotRows);
            string exampleSlots     = string.Join("\n", exampleSlotRows);
            string exampleBuildings = string.Join("\n", exampleBuildingRows);
            string exampleUnits     = string.Join("\n", exampleUnitRows);
            string slotChoices      = string.Join("|", Enumerable.Range(0, slotCount));

            var sb = new StringBuilder();
            sb.AppendLine(
                "You are a scenario designer for Project Chimera, a real-time strategy game.");
            sb.AppendLine(
                "Convert the user's map brief into a valid JSON ScenarioData object.");
            sb.AppendLine();
            sb.AppendLine("=== SCENARIO SCHEMA ===");
            sb.AppendLine($@"{{
  ""id"": ""string (lowercase_snake_case, e.g. my_map)"",
  ""display_name"": ""string"",
  ""terrain_ref"": """",
  ""map_bounds"": 120.0,
  ""win_condition"": ""DestroyAllBuildings"" | ""EliminateAllUnits"",
  ""player_slots"": [
{schemaSlots}
  ],
  ""resource_nodes"": [
    {{ ""x"": float, ""z"": float, ""supply"": 400.0, ""rate"": 5.0, ""max_gatherers"": 4 }}
  ],
  ""buildings"": [
    {{ ""type"": {BuildingTypeChoices}, ""slot"": {slotChoices}, ""x"": float, ""z"": float, ""pre_built"": true }}
  ],
  ""units"": [
    {{ ""unit_id"": ""string"", ""slot"": {slotChoices}, ""x"": float, ""z"": float }}
  ],
  ""triggers"": []
}}");
            sb.AppendLine();
            sb.AppendLine("=== PLACEMENT RULES ===");
            sb.AppendLine($"- All x/z positions MUST be within ±{ctx.MapBounds} world units.");
            // Story 8.3: reflect the SAME min-player-slots clamp ValidateScenario gates against (RTS default 2).
            // DW-372: the per-player base hints are generated from the SAME ring the schema/example render, so a
            // raised floor names every slot instead of only the historical two.
            sb.AppendLine($"- Provide at least {ctx.MinPlayerSlots} player slots.{baseHints}");
            sb.AppendLine("- Each slot MUST have a CommandCenter (pre_built=true) at its base position.");
            sb.AppendLine("- Ore nodes must be spaced at least 15 units apart from every other ore node.");
            sb.AppendLine("- Use 4–12 resource nodes. Supply 200–2000, rate 3–10.");
            // Story 8.3: reflect the SAME max-combat clamp ValidateScenario gates against (RTS default 6).
            sb.AppendLine($"- Pre-place at most {ctx.MaxCombatUnitsPerSlot} combat (non-worker) units per faction slot.");
            sb.AppendLine("- Start workers 3–5 units from their CommandCenter.");

            // DW-371 — per-type guidance from the TRUSTED ScenarioTypeRegistry preset (never from the generated
            // file). EMPTY for the RTS default, so the RTS prompt is byte-for-byte what it was before the registry
            // landed — pinned by ScenarioTypeRegistryTests.RtsPrompt_IsUnchangedByApplyingTheDefaultType.
            string typeGuidance = ScenarioTypeRegistry.PromptGuidance(ctx.ScenarioType);
            if (!string.IsNullOrEmpty(typeGuidance))
            {
                sb.AppendLine();
                sb.AppendLine("=== SCENARIO TYPE ===");
                sb.AppendLine(typeGuidance);
            }

            sb.AppendLine();
            sb.AppendLine("=== AVAILABLE UNIT IDs ===");
            sb.AppendLine(string.Join(", ", ctx.UnitIds.Select(id => $"\"{id}\"")));
            sb.AppendLine();
            sb.AppendLine("=== EXAMPLE OUTPUT ===");
            sb.AppendLine($@"{{
  ""id"": ""contested_valley"",
  ""display_name"": ""Contested Valley"",
  ""terrain_ref"": """",
  ""map_bounds"": 120,
  ""win_condition"": ""DestroyAllBuildings"",
  ""player_slots"": [
{exampleSlots}
  ],
  ""resource_nodes"": [
    {{ ""x"": -25, ""z"":  15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"": -25, ""z"": -15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"":   0, ""z"":   0, ""supply"": 900, ""rate"": 7, ""max_gatherers"": 4 }},
    {{ ""x"":  25, ""z"":  15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"":  25, ""z"": -15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }}
  ],
  ""buildings"": [
{exampleBuildings}
  ],
  ""units"": [
{exampleUnits}
  ],
  ""triggers"": []
}}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine(
                "Create an interesting, balanced, playable map based on the user's description.");
            sb.AppendLine(
                "Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Story 8.4 — AI entity drafts (unit / ability / hero / faction)
        //
        // A provider-backed EDITABLE-DRAFT framework mirroring GenerateTriggerAsync/GenerateScenarioAsync: the four
        // kinds share the SAME no-fallback / availability / StripMarkdown pipeline (see RunDraftAsync) and each draft is
        // gated by the EXISTING per-kind validator (UnitDefinitionValidator / AbilityLoader+AbilityValidator /
        // FactionValidator) — never a fork, never a weakened gate. No second float→Fixed path is introduced:
        //   • ability drafts deserialize through ContentJson.Options (FixedJsonConverter quantizes at parse, rejects
        //     |v| >= 32768 / NaN / Inf) — the same boundary hand-authored abilities use;
        //   • unit / hero / faction drafts deserialize through the lenient FactionDefinition.JsonOptions (plain float)
        //     and are range-gated to the Fixed-safe [0, 32768) by UnitDefinitionValidator; quantization to Fixed still
        //     happens ONLY later at EntityWorld.ApplyUnitDefinition (the single def→SoA boundary), so a draft hashes
        //     identically to an equivalent hand-authored def BY CONSTRUCTION.
        // Stays Godot-free (no Godot dependency) — Tier-1 + analyzer covered.
        // ══════════════════════════════════════════════════════════════════════

        // DW-375: like _cts/_mapCts above, every one of these is reassigned ONLY through ReplaceTokenSource (cancel +
        // dispose the superseded source) and released by Dispose. Do not hand-roll `x?.Cancel(); x = new(...)` here again.
        private CancellationTokenSource? _unitCts;
        private CancellationTokenSource? _abilityCts;
        private CancellationTokenSource? _heroCts;
        private CancellationTokenSource? _factionCts;
        private CancellationTokenSource? _balanceCts;   // Story 8.5 — balance-analysis flow (own CTS)

        // ── Public draft API ──────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generate an editable <see cref="UnitDefinition"/> draft from a natural-language prompt. The
        /// draft is gated by the SAME <see cref="UnitDefinitionValidator"/> hand-authored units pass; the callback is
        /// marshalled to the main thread via <see cref="DrainEvents"/>. On success: callback(def, null). On any failure
        /// (unavailable provider / provider error / failed validation): callback(null, message).
        /// </summary>
        public void GenerateUnitDraftAsync(string prompt, UnitDraftContext ctx, Action<UnitDefinition?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _unitCts);   // DW-375: cancels AND disposes the superseded source
            RunDraftAsync(BuildUnitDraftPrompt(ctx), $"Create a unit for: {prompt}",
                json => ValidateUnitDraft(json, ctx), token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable <see cref="AbilityDefinition"/> draft. Gated by the SAME
        /// <see cref="AbilityLoader"/>/<see cref="AbilityValidator"/> path hand-authored abilities pass (numbers land as
        /// <see cref="Fixed"/> via <see cref="ContentJson.Options"/>). See <see cref="GenerateUnitDraftAsync"/> for the callback contract.
        /// </summary>
        public void GenerateAbilityDraftAsync(string prompt, AbilityDraftContext ctx, Action<AbilityDefinition?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _abilityCts);   // DW-375: cancels AND disposes the superseded source
            RunDraftAsync(BuildAbilityDraftPrompt(ctx), $"Create an ability for: {prompt}",
                json => ValidateAbilityDraft(json, ctx), token, onComplete);
        }

        /// <summary>
        /// DW-900 — ask the model for effect nodes to APPEND to the ability in <paramref name="ctx"/>, rather than a
        /// replacement ability. Same cancellation/callback contract as <see cref="GenerateAbilityDraftAsync"/> and it
        /// shares the same <c>_abilityCts</c>, so a Generate and an Add-more can never both be in flight.
        /// </summary>
        public void ExtendAbilityDraftAsync(string prompt, AbilityDraftContext ctx,
                                            Action<List<ProjectChimera.Effects.EffectNode>?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _abilityCts);
            RunDraftAsync(BuildAbilityExtendPrompt(ctx), $"Add to this ability: {prompt}",
                json => ValidateAbilityAddition(json, ctx), token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable HERO draft — a <see cref="UnitDefinition"/> with <c>is_hero:true</c> and a
        /// <c>hero</c> block. <see cref="ValidateHeroDraft"/> requires the hero designation before running the shared unit
        /// gate. See <see cref="GenerateUnitDraftAsync"/> for the callback contract.
        /// </summary>
        public void GenerateHeroDraftAsync(string prompt, UnitDraftContext ctx, Action<UnitDefinition?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _heroCts);   // DW-375: cancels AND disposes the superseded source
            RunDraftAsync(BuildHeroDraftPrompt(ctx), $"Create a hero for: {prompt}",
                json => ValidateHeroDraft(json, ctx), token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable <see cref="FactionDefinition"/> draft. Gated by
        /// <see cref="FactionValidator.Validate"/> AND a per-unit <see cref="UnitDefinitionValidator"/> pass (closing the
        /// bare-faction-load deep-validation gap); roster-completeness (<c>ValidateComplete</c>) is deliberately NOT run,
        /// so an incomplete-but-well-formed draft still loads for further editing. See <see cref="GenerateUnitDraftAsync"/>
        /// for the callback contract.
        /// </summary>
        public void GenerateFactionDraftAsync(string prompt, FactionDraftContext ctx, Action<FactionDefinition?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _factionCts);   // DW-375: cancels AND disposes the superseded source
            RunDraftAsync(BuildFactionDraftPrompt(ctx), $"Create a faction for: {prompt}",
                json => ValidateFactionDraft(json, ctx), token, onComplete,
                maxTokens: FACTION_DRAFT_MAX_TOKENS);
        }

        /// <summary>Cancel any in-flight draft generation (all four kinds) plus the balance analysis. DW-375: a no-op
        /// after <see cref="Dispose"/>.</summary>
        public void CancelDrafts()
        {
            _unitCts?.Cancel();
            _abilityCts?.Cancel();
            _heroCts?.Cancel();
            _factionCts?.Cancel();
            _balanceCts?.Cancel();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Story 8.5 — AI balance analysis (editable, per-field suggestions)
        //
        // A provider-backed CRITIQUE flow mirroring the 8.4 draft framework: it rides the SAME no-fallback / availability /
        // StripMarkdown pipeline (RunDraftAsync) and returns an EDITABLE BalanceReport of per-field BalanceSuggestions.
        // Nothing is auto-applied — the creator reviews/edits/discards each suggestion, and applying one routes the
        // proposed value through BalanceSuggestionApplier.TryApply, which re-gates it with the EXISTING
        // UnitDefinitionValidator on a clone (no second float→Fixed path, no bare-definition hash). Stays Godot-free.
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Asynchronously request an AI balance analysis of a faction's roster. The focus <paramref name="prompt"/> is
        /// the creator's steer ("melee feels weak", …); <paramref name="ctx"/> carries the roster unit ids the router
        /// validates suggestions against and the closed tunable-field vocabulary. The callback is marshalled to the main
        /// thread via <see cref="DrainEvents"/>. On success: callback(report, null) with a non-null
        /// <see cref="BalanceReport"/> whose every suggestion names an existing unit id + tunable field + proposed value
        /// + rationale. On any failure (unavailable provider / provider error / unparseable-or-invalid report):
        /// callback(null, message). A full-roster analysis needs a large budget — reuse the faction 8192-token figure.
        /// </summary>
        public void GenerateBalanceAnalysisAsync(
            string prompt, BalanceAnalysisContext ctx, Action<BalanceReport?, string?> onComplete)
        {
            ThrowIfDisposed();
            CancellationToken token = ReplaceTokenSource(ref _balanceCts);   // DW-375: cancels AND disposes the superseded source
            RunDraftAsync(
                BuildBalanceAnalysisPrompt(ctx),
                $"Analyze this faction for balance. Focus: {prompt}",
                json => ValidateBalanceReport(json, ctx),
                token, onComplete, maxTokens: FACTION_DRAFT_MAX_TOKENS);
        }

        /// <summary>Cancel any in-flight balance-analysis request. DW-375: a no-op after <see cref="Dispose"/>.</summary>
        public void CancelBalanceAnalysis() => _balanceCts?.Cancel();

        /// <summary>Story 8.5: internal so the staleness-guard test can assert the prompt enumerates every member of
        /// <see cref="BalanceSuggestionApplier.TunableFields"/> (each heads its own line — exact-token match) and states
        /// the Fixed-safe range. A tunable-field member absent from this builder fails the guard.</summary>
        internal static string BuildBalanceAnalysisPrompt(BalanceAnalysisContext ctx)
        {
            IReadOnlyList<string> fields = ctx?.TunableFields != null && ctx.TunableFields.Count > 0
                ? ctx.TunableFields
                : BalanceSuggestionApplier.TunableFields;
            IReadOnlyList<string> unitIds = ctx?.UnitIds ?? System.Array.Empty<string>();

            var sb = new StringBuilder();
            sb.AppendLine("You are a balance analyst for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Critique the balance of the faction roster below and propose concrete, field-specific tuning");
            sb.AppendLine("suggestions the creator (address them as \"Commander\") can review, edit, and apply.");
            sb.AppendLine();
            sb.AppendLine("=== EXISTING UNIT IDS (a suggestion's unit_id MUST be one of these) ===");
            sb.AppendLine(unitIds.Count > 0 ? string.Join(", ", unitIds) : "(none)");
            sb.AppendLine();
            sb.AppendLine("=== TUNABLE FIELDS (a suggestion's field MUST be exactly one of these) ===");
            foreach (string f in fields)
                sb.AppendLine(f);   // each field HEADS its own line (exact-token staleness guard)
            sb.AppendLine();
            sb.AppendLine("=== OUTPUT SCHEMA ===");
            sb.AppendLine(@"{
  ""suggestions"": [
    {
      ""unit_id"": ""<an existing unit id>"",
      ""field"": ""<one tunable field name from the list above>"",
      ""current"": <the field's current value, for display>,
      ""proposed"": <the new numeric value you recommend>,
      ""rationale"": ""<one terse sentence explaining the change>""
    }
  ]
}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every proposed value MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Only propose changes to the listed tunable fields on the listed unit ids. Prefer a handful of");
            sb.AppendLine("high-impact, actionable suggestions over an exhaustive dump.");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>
        /// Parse a generated balance analysis into an editable <see cref="BalanceReport"/>, list-first-failing with a
        /// LOCATED error on any of: malformed JSON, a missing <c>suggestions</c> array, a suggestion whose <c>unit_id</c>
        /// is not in <see cref="BalanceAnalysisContext.UnitIds"/>, or whose <c>field</c> is not in the closed tunable set
        /// (<see cref="BalanceAnalysisContext.TunableFields"/>, defaulted from <see cref="BalanceSuggestionApplier"/>).
        /// Never mutates any faction/unit data — applying a suggestion is a separate, explicit, per-suggestion action
        /// through <see cref="BalanceSuggestionApplier.TryApply"/>.
        /// </summary>
        public static (BalanceReport? report, string? error) ValidateBalanceReport(string json, BalanceAnalysisContext ctx)
        {
            BalanceReport report;
            try
            {
                // DW-526: this site used to build a per-call option set that registered NO converters at all — so it
                // was the ONE model-output parse path that could not read a Fixed-typed field, and it rejected the
                // trailing commas / // comments every other LLM path tolerates. Now on the shared posture.
                report = JsonSerializer.Deserialize<BalanceReport>(json, ContentJson.ModelOutputOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            if (report.Suggestions == null)
                return (null, "Balance report has no 'suggestions' array.");

            var knownFields = new HashSet<string>(
                ctx?.TunableFields != null && ctx.TunableFields.Count > 0 ? ctx.TunableFields : BalanceSuggestionApplier.TunableFields,
                StringComparer.Ordinal);
            var knownIds = new HashSet<string>(ctx?.UnitIds ?? System.Array.Empty<string>(), StringComparer.Ordinal);

            for (int i = 0; i < report.Suggestions.Count; i++)
            {
                BalanceSuggestion s = report.Suggestions[i];
                if (s == null)
                    return (null, $"suggestions[{i}] is null.");
                if (!knownIds.Contains(s.UnitId ?? ""))
                    return (null, $"suggestions[{i}].unit_id='{s.UnitId}' is not a unit in this faction's roster.");
                if (!knownFields.Contains(s.Field ?? ""))
                    return (null, $"suggestions[{i}].field='{s.Field}' is not a tunable balance field.");
            }

            return (report, null);
        }

        /// <summary>
        /// The shared draft pipeline (factored from <see cref="GenerateTriggerAsync"/>): snapshot settings on the caller
        /// thread, then on a worker thread run <c>TryCreate</c> (the availability gate + NO request on false) → <c>GenerateAsync</c>
        /// → <c>!Ok</c> availability mapping via <see cref="AiAvailabilityMap.FromFailure"/> → <see cref="StripMarkdown"/> →
        /// <paramref name="validate"/> → enqueue the callback. The selected provider is authoritative — no fallback.
        /// Every callback is marshalled through the existing <see cref="_queue"/>/<see cref="DrainEvents"/> seam.
        /// </summary>
        private void RunDraftAsync<T>(
            string systemPrompt,
            string userMsg,
            Func<string, (T? def, string? error)> validate,
            CancellationToken token,
            Action<T?, string?> onComplete,
            int maxTokens = MAX_TOKENS) where T : class
        {
            // Snapshot the authoritative settings on the caller thread (see GenerateTriggerAsync). No fallback.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    // Synchronous-unavailable (no provider / no key / bad host) short-circuits with the availability
                    // message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(systemPrompt, userMsg, maxTokens), token);

                    if (!result.Ok)
                    {
                        // The selected provider's failure is surfaced (never masked) with the shared availability microcopy.
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    (T? def, string? error) = validate(StripMarkdown(result.Text));
                    _queue.Enqueue(() => onComplete(def, def == null ? error : null));
                }
                catch (OperationCanceledException) { /* silently dropped */ }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        // ── Validate routers (public-static; each routes through the EXISTING per-kind gate) ──

        /// <summary>
        /// Deserialize a generated unit through the lenient <see cref="FactionDefinition.JsonOptions"/> (plain float — the
        /// SAME options hand-authored units load with) and gate it with <see cref="UnitDefinitionValidator"/>. On any
        /// located error returns <c>(null, message)</c> naming the field path + offending value; never quantizes here
        /// (quantization stays at <c>EntityWorld.ApplyUnitDefinition</c>).
        /// </summary>
        public static (UnitDefinition? def, string? error) ValidateUnitDraft(string json, UnitDraftContext ctx)
        {
            if (!TryDeserializeUnit(json, out UnitDefinition? def, out string? jsonError))
                return (null, jsonError);
            return GateUnit(def!, ctx);
        }

        /// <summary>
        /// Like <see cref="ValidateUnitDraft"/> but additionally requires the HERO designation (<c>is_hero:true</c> + a
        /// non-null <c>hero</c> block) BEFORE the shared unit gate — a well-formed non-hero unit is rejected with a
        /// located error so <see cref="GenerateHeroDraftAsync"/> never silently yields a plain unit.
        /// </summary>
        public static (UnitDefinition? def, string? error) ValidateHeroDraft(string json, UnitDraftContext ctx)
        {
            if (!TryDeserializeUnit(json, out UnitDefinition? def, out string? jsonError))
                return (null, jsonError);
            if (!def!.IsHero || def.Hero == null)
                return (null, $"hero '{def.Id}'.is_hero: a hero draft requires is_hero:true and a 'hero' block " +
                    $"(got is_hero={def.IsHero}, hero={(def.Hero == null ? "null" : "present")}).");
            return GateUnit(def, ctx);
        }

        /// <summary>
        /// Route a generated ability through the EXACT hand-authored path: <see cref="AbilityLoader.Load"/> deserializes
        /// via <see cref="ContentJson.Options"/> (<see cref="FixedJsonConverter"/> quantizes each number to
        /// <see cref="Fixed"/> and rejects <c>|v| &gt;= 32768</c>/NaN/Inf at parse) and runs <see cref="AbilityValidator"/>.
        /// On failure returns <c>(null, located error)</c>.
        /// </summary>
        public static (AbilityDefinition? def, string? error) ValidateAbilityDraft(string json, AbilityDraftContext ctx)
        {
            AbilityValidationResult r = AbilityLoader.Load(json, "ai-draft");
            return r.Ok ? (r.Value.Value, null) : (null, r.Error);
        }

        /// <summary>
        /// DW-900 — parse an EXTEND reply: a bare JSON array of effect nodes to append. Goes through the same
        /// <see cref="ContentJson.Options"/> the hand-authored path uses, so the registered
        /// <c>EffectNodeJsonConverter</c> applies the identical closed-vocabulary rules (unknown kind rejected,
        /// unknown property rejected, numbers landing as <see cref="Fixed"/>). The APPENDED-TO ability is validated
        /// separately by the caller once merged — this only has to produce well-formed nodes.
        /// </summary>
        public static (List<ProjectChimera.Effects.EffectNode>? nodes, string? error) ValidateAbilityAddition(string json, AbilityDraftContext ctx)
        {
            List<ProjectChimera.Effects.EffectNode>? nodes;
            try
            {
                nodes = JsonSerializer.Deserialize<List<ProjectChimera.Effects.EffectNode>>(json, ContentJson.Options);
            }
            catch (Exception ex)
            {
                return (null, $"The AI's addition was not a valid effect-node array: {ex.Message}");
            }

            if (nodes is null || nodes.Count == 0)
                return (null, "The AI returned no effects to add.");
            foreach (ProjectChimera.Effects.EffectNode n in nodes)
                if (n is null) return (null, "The AI's addition contained an empty effect node.");

            return (nodes, null);
        }

        /// <summary>
        /// Deserialize a generated faction through <see cref="FactionDefinition.JsonOptions"/>, run the structural
        /// <see cref="FactionValidator.Validate"/> gate, AND loop <see cref="UnitDefinitionValidator"/> over
        /// <c>def.Units</c> (the deep per-unit validation bare faction load skips — closing that gap). Does NOT run
        /// <see cref="FactionValidator.ValidateComplete"/> (roster-completeness stays at the selectable boundary), so an
        /// incomplete-but-well-formed draft still loads for editing.
        /// </summary>
        public static (FactionDefinition? def, string? error) ValidateFactionDraft(string json, FactionDraftContext ctx)
        {
            FactionDefinition def;
            try
            {
                def = JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            FactionValidationResult structural = FactionValidator.Validate(def);
            if (!structural.Ok)
                return (null, JoinErrors(structural.Errors));

            // Deep-validate each unit (the gap bare faction load leaves — see Design Notes). A per-unit failure names
            // the offending unit + field.
            var validator = new UnitDefinitionValidator();
            if (def.Units != null)
            {
                foreach (UnitDefinition u in def.Units)
                {
                    if (u == null) continue;
                    UnitValidationResult ur = validator.Validate(
                        u, ctx.AbilityRegistry, ctx.BehaviorRegistry, ctx.ItemRegistry, def.Units, "unit");
                    if (!ur.Ok)
                        return (null, JoinErrors(ur.Errors));
                }
            }

            return (def, null);
        }

        // ── Router helpers ────────────────────────────────────────────────────

        private static bool TryDeserializeUnit(string json, out UnitDefinition? def, out string? error)
        {
            try
            {
                def = JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                def = null;
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }

        private static (UnitDefinition? def, string? error) GateUnit(UnitDefinition def, UnitDraftContext ctx)
        {
            UnitValidationResult result = new UnitDefinitionValidator().Validate(
                def, ctx.AbilityRegistry, ctx.BehaviorRegistry, ctx.ItemRegistry, ctx.Siblings, "unit");
            return result.Ok ? (def, null) : (null, JoinErrors(result.Errors));
        }

        /// <summary>Join every located field error into one message (list-all validators return several); each message
        /// already carries its path + offending value.</summary>
        private static string JoinErrors(IReadOnlyList<(string FieldPath, string Message)> errors)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(errors[i].Message);
            }
            return sb.ToString();
        }

        // ── Draft prompt builders (internal-static; staleness-guardable) ──────

        /// <summary>The Fixed-safe numeric ceiling every generated stat must stay below (mirrors
        /// <c>FixedJsonConverter.FixedRangeLimit</c> / <c>UnitDefinitionValidator</c>'s Range).</summary>
        private const int DraftFixedRange = 32768;

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt states the Fixed-safe range and
        /// the archetype+ability composition guidance (a draft is archetype + ability/behavior composition, never a bespoke
        /// subclass).</summary>
        internal static string BuildUnitDraftPrompt(UnitDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a unit-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON UnitDefinition object (snake_case keys).");
            sb.AppendLine();
            AppendUnitSchema(sb);
            sb.AppendLine();
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express behavior via archetype (category) + ability/behavior composition — NEVER a bespoke");
            sb.AppendLine("subclass. A \"healer\" is a Ranged archetype composed with a heal ability + a support behavior.");
            sb.AppendLine("Only reference ability/behavior ids from the AVAILABLE lists below; an unknown id is rejected.");
            sb.AppendLine();
            AppendUnitContext(sb, ctx);
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the HERO prompt states the Fixed-safe range,
        /// composition guidance, AND that a hero requires <c>is_hero:true</c> + a <c>hero</c> block.</summary>
        internal static string BuildHeroDraftPrompt(UnitDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a hero-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("A hero is a UnitDefinition with is_hero:true AND a 'hero' block (leveling curve + ability slots).");
            sb.AppendLine("Convert the user's description into a valid JSON UnitDefinition object (snake_case keys).");
            sb.AppendLine();
            AppendUnitSchema(sb);
            sb.AppendLine("The 'hero' block (REQUIRED — is_hero MUST be true):");
            sb.AppendLine(@"{
  ""max_level"": 5,          // 2..100
  ""base_xp"": 100.0,        // > 0
  ""xp_growth"": 1.4,        // 1..100
  ""xp_per_kill"": 40.0,
  ""xp_share_radius"": 20.0, // < 128
  ""health_per_level"": 25.0,
  ""damage_per_level"": 4.0,
  ""armor_per_level"": 1.0,
  ""signature_ability"": ""<ability_id or empty>"",
  ""ultimate_ability"": ""<ability_id or empty>""
}");
            sb.AppendLine();
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express behavior via archetype (category) + ability/behavior composition — NEVER a bespoke");
            sb.AppendLine("subclass. Signature and ultimate ability ids must differ and reference AVAILABLE ability ids.");
            sb.AppendLine();
            AppendUnitContext(sb, ctx);
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Set is_hero:true and author the 'hero' block — a hero draft is rejected without both.");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt enumerates every closed-set member
        /// of the effect-kind, targeting, and activation vocabularies (each heads its own line — exact-token match).</summary>
        internal static string BuildAbilityDraftPrompt(AbilityDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an ability-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON AbilityDefinition object (snake_case keys).");
            sb.AppendLine();
            sb.AppendLine("=== ABILITY SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""targeting"": ""None"" | ""Self"" | ""TargetUnit"" | ""GroundPoint"",
  ""activation"": ""active"" | ""aura"" | ""on_hit"" | ""while_alive"",
  ""cost_energy"": 0.0,
  ""cost_ore"": 0,
  ""cost_crystal"": 0,
  ""cost_health"": 0,
  ""cooldown"": 0.0,
  ""effect"": { EffectNode }
}");
            sb.AppendLine();
            // Each targeting/activation token HEADS its own line so the staleness guard's exact-line-token match holds.
            sb.AppendLine("=== VALID TARGETING TYPES ===");
            sb.AppendLine("None        — no target (passive)");
            sb.AppendLine("Self        — the caster is the target");
            sb.AppendLine("TargetUnit  — the player selects one unit");
            sb.AppendLine("GroundPoint — the player picks a ground point");
            sb.AppendLine();
            sb.AppendLine("=== VALID ACTIVATION TYPES ===");
            sb.AppendLine("active      — player-cast ability (default)");
            sb.AppendLine("aura        — while-alive aura (SearchArea → apply_modifier, refreshed each tick)");
            sb.AppendLine("on_hit      — rider that fires when this unit's attack lands");
            sb.AppendLine("while_alive — permanent/periodic self-passive installed at spawn");
            sb.AppendLine();
            sb.AppendLine("=== VALID EFFECT KINDS (the 'kind' field on every effect node) ===");
            sb.AppendLine("direct_hp_delta — { \"kind\": \"direct_hp_delta\", \"delta\": float }");
            sb.AppendLine("heal            — { \"kind\": \"heal\", \"amount\": float }");
            sb.AppendLine("damage          — { \"kind\": \"damage\", \"amount\": float, \"damage_type\": \"Normal\" }");
            sb.AppendLine("apply_modifier  — { \"kind\": \"apply_modifier\", \"modifier\": { … } }");
            sb.AppendLine("sequence        — { \"kind\": \"sequence\", \"children\": [ EffectNode, … ] }");
            sb.AppendLine("search_area     — { \"kind\": \"search_area\", \"radius\": float, \"child\": EffectNode }");
            sb.AppendLine("persistent      — { \"kind\": \"persistent\", \"period_ticks\": int, \"period_count\": int, … }");
            sb.AppendLine();
            if (ctx?.ExistingAbilityIds != null && ctx.ExistingAbilityIds.Count > 0)
            {
                sb.AppendLine("=== EXISTING ABILITY IDS (avoid colliding) ===");
                sb.AppendLine(string.Join(", ", ctx.ExistingAbilityIds));
                sb.AppendLine();
            }
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric value MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("An ability must declare at least one effect node. Costs/cooldown must be >= 0.");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>
        /// DW-900 — the EXTEND prompt: same vocabulary blocks as <see cref="BuildAbilityDraftPrompt"/>, but it shows
        /// the model the ability that already exists and asks for ONLY the new nodes, as a bare JSON ARRAY.
        ///
        /// <para>Asking for an array of additions rather than a rewritten whole ability is the design decision that
        /// makes "add more" mean it. A whole-ability reply cannot GUARANTEE the author's existing effects survive —
        /// the model is free to quietly reword them — which is the exact complaint this feature answers. An array of
        /// additions is structurally incapable of touching what is already there: the merge is done locally by
        /// <see cref="Core.Definitions.AbilityGraphMerge"/>, not by the model.</para>
        /// </summary>
        internal static string BuildAbilityExtendPrompt(AbilityDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an ability-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("The user already has an ability and wants to ADD to it. You are extending it, not rewriting it.");
            sb.AppendLine();
            sb.AppendLine("=== VALID EFFECT KINDS (the 'kind' field on every effect node) ===");
            sb.AppendLine("direct_hp_delta — { \"kind\": \"direct_hp_delta\", \"delta\": float }");
            sb.AppendLine("heal            — { \"kind\": \"heal\", \"amount\": float }");
            sb.AppendLine("damage          — { \"kind\": \"damage\", \"amount\": float, \"damage_type\": \"Normal\" }");
            sb.AppendLine("apply_modifier  — { \"kind\": \"apply_modifier\", \"modifier\": { … } }");
            sb.AppendLine("sequence        — { \"kind\": \"sequence\", \"children\": [ EffectNode, … ] }");
            sb.AppendLine("search_area     — { \"kind\": \"search_area\", \"radius\": float, \"child\": EffectNode }");
            sb.AppendLine("persistent      — { \"kind\": \"persistent\", \"period_ticks\": int, \"period_count\": int, … }");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(ctx?.CurrentAbilityJson))
            {
                sb.AppendLine("=== THE CURRENT ABILITY (do NOT rewrite or restate any of this) ===");
                sb.AppendLine(ctx!.CurrentAbilityJson);
                sb.AppendLine();
            }
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Return ONLY a JSON ARRAY of the NEW effect nodes to append, e.g. [ { \"kind\": … }, … ].");
            sb.AppendLine("Do NOT return an ability object. Do NOT include id, display_name, targeting, costs or cooldown.");
            sb.AppendLine("Do NOT repeat any effect that already exists above — return only what is being ADDED.");
            sb.AppendLine("Return 1 to 4 nodes. Prefer the smallest addition that satisfies the request.");
            sb.AppendLine($"Every numeric value MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt enumerates every closed-set
        /// <c>ai_preset</c> member (from the trusted context, seeded from <c>FactionValidator.KnownAiPresets</c>).</summary>
        internal static string BuildFactionDraftPrompt(FactionDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a faction-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON FactionDefinition object (snake_case keys).");
            sb.AppendLine();
            sb.AppendLine("=== FACTION SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""color"": [0.2, 0.5, 1.0, 1.0],   // [r, g, b, a] in [0, 1]
  ""ai_preset"": ""balanced"",
  ""units"": [ UnitDefinition, … ],
  ""buildings"": [ BuildingDefinition, … ]
}");
            sb.AppendLine();
            sb.AppendLine("=== VALID AI PRESETS ===");
            IReadOnlyList<string> presets = ctx?.AiPresets != null && ctx.AiPresets.Count > 0
                ? ctx.AiPresets
                : new[] { "balanced" };
            foreach (string p in presets)
                sb.AppendLine($"{p}");   // each preset HEADS its own line (exact-token staleness guard)
            sb.AppendLine();
            sb.AppendLine("=== UNIT SCHEMA (per entry in 'units') ===");
            AppendUnitSchema(sb);
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express each unit's behavior via archetype (category) + ability/behavior composition — NEVER a");
            sb.AppendLine("bespoke subclass. Include at least one Worker unit and one combat unit for a playable roster.");
            sb.AppendLine();
            AppendUnitContext(sb, new UnitDraftContext
            {
                AbilityRegistry = ctx?.AbilityRegistry,
                BehaviorRegistry = ctx?.BehaviorRegistry,
                ItemRegistry = ctx?.ItemRegistry,
            });
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>The shared UnitDefinition JSON schema block (snake_case), reused by the unit/hero/faction prompts.</summary>
        private static void AppendUnitSchema(StringBuilder sb)
        {
            sb.AppendLine("=== UNIT SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""category"": ""Worker"" | ""Melee"" | ""Ranged"" | ""Siege"" | ""Air"" | ""Structure"",
  ""hp"": 100.0,
  ""speed"": 4.0,
  ""attack_damage"": 10.0,
  ""attack_range"": 5.0,
  ""attack_speed"": 1.0,
  ""damage_type"": ""Normal"" | ""Pierce"" | ""Siege"" | ""Magic"",
  ""armor_type"": ""Unarmored"" | ""Light"" | ""Medium"" | ""Heavy"" | ""Fortified"",
  ""armor"": 0.0,
  ""cost_ore"": 50,
  ""cost_crystal"": 0,
  ""supply"": 1,
  ""vision_range"": 8.0,
  ""abilities"": [ ""<ability_id>"", … ],
  ""behaviors"": [ ""<behavior_id>"", … ]
}");
        }

        /// <summary>Append the AVAILABLE ability/behavior/item ids from the draft context so the model composes only with
        /// resolvable references (an unknown ref is a located reject at the validate gate).</summary>
        private static void AppendUnitContext(StringBuilder sb, UnitDraftContext ctx)
        {
            sb.AppendLine("=== AVAILABLE COMPOSITION IDS ===");
            sb.AppendLine("Ability ids: " + IdList(ctx?.AbilityRegistry));
            sb.AppendLine("Behavior ids: " + BehaviorIdList(ctx?.BehaviorRegistry));
            sb.AppendLine("Item ids: " + ItemIdList(ctx?.ItemRegistry));
            sb.AppendLine();
        }

        private static string IdList(AbilityRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }

        private static string BehaviorIdList(BehaviorRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }

        private static string ItemIdList(ItemRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }
    }

    // ── Story 8.4 draft context DTOs (Godot-free) ──────────────────────────────

    /// <summary>
    /// Context for a UNIT (and HERO) draft — the loaded registries the validator gates ability/behavior/item refs
    /// against, plus the faction's existing units (for the uniqueness rule). All optional: a null registry SKIPS its
    /// reference check (the validator's documented null-registry semantics), exactly as hand-authored editor validation.
    /// </summary>
    public sealed class UnitDraftContext
    {
        /// <summary>Loaded abilities the unit may compose (null skips the ability-ref check).</summary>
        public AbilityRegistry? AbilityRegistry { get; set; }

        /// <summary>Loaded behaviors the unit may compose (null skips the behavior-ref check).</summary>
        public BehaviorRegistry? BehaviorRegistry { get; set; }

        /// <summary>Loaded items a shop unit may stock (null skips the shop-stock-ref check).</summary>
        public ItemRegistry? ItemRegistry { get; set; }

        /// <summary>The faction's existing units, for the duplicate-id rule (null skips the uniqueness check).</summary>
        public IReadOnlyList<UnitDefinition>? Siblings { get; set; }
    }

    /// <summary>
    /// Context for an ABILITY draft. Ability validation is self-contained (<see cref="AbilityLoader"/>/
    /// <see cref="AbilityValidator"/> need no external registry), so this carries only optional existing-id hints the
    /// prompt lists so the model avoids id collisions.
    /// </summary>
    public sealed class AbilityDraftContext
    {
        /// <summary>Existing ability ids the prompt lists as "avoid colliding" (purely a prompt hint; not a gate).</summary>
        public IReadOnlyList<string>? ExistingAbilityIds { get; set; }

        /// <summary>
        /// DW-900 — the ability being EXTENDED, serialized, for the "Add more" flow. Null for a plain Generate (which
        /// is a create-from-nothing and deliberately sends no current state). When set, the prompt shows the model what
        /// already exists and asks for ONLY the new nodes, so the author's graph is never restated and never rewritten.
        /// </summary>
        public string? CurrentAbilityJson { get; set; }
    }

    /// <summary>
    /// Context for a FACTION draft — the registries used to deep-validate each generated unit, plus the closed-set
    /// <c>ai_preset</c> members (seeded from <c>FactionValidator.KnownAiPresets</c>) and signature-mechanic id hints the
    /// prompt enumerates.
    /// </summary>
    public sealed class FactionDraftContext
    {
        /// <summary>Loaded abilities each generated unit may compose (deep per-unit validation).</summary>
        public AbilityRegistry? AbilityRegistry { get; set; }

        /// <summary>Loaded behaviors each generated unit may compose.</summary>
        public BehaviorRegistry? BehaviorRegistry { get; set; }

        /// <summary>Loaded items a shop unit may stock.</summary>
        public ItemRegistry? ItemRegistry { get; set; }

        /// <summary>The closed set of recognized <c>ai_preset</c> ids the prompt enumerates (staleness-guarded).</summary>
        public IReadOnlyList<string> AiPresets { get; set; } = System.Array.Empty<string>();

        /// <summary>Optional signature-mechanic id hints the prompt may list (not a gate this story).</summary>
        public IReadOnlyList<string> SignatureIds { get; set; } = System.Array.Empty<string>();
    }

    // ── Story 8.5 balance-analysis DTOs (Godot-free) ───────────────────────────

    /// <summary>
    /// Context for a balance analysis — the faction's live roster ids the router validates every suggestion's
    /// <c>unit_id</c> against, plus the closed tunable-field vocabulary (defaulted from
    /// <see cref="BalanceSuggestionApplier.TunableFields"/> — the single source of truth shared by the prompt builder,
    /// the validate router, and the apply mapper).
    /// </summary>
    public sealed class BalanceAnalysisContext
    {
        /// <summary>The roster unit ids a suggestion may target; a suggestion citing an id outside this set is a located reject.</summary>
        public IReadOnlyList<string> UnitIds { get; set; } = System.Array.Empty<string>();

        /// <summary>The closed set of tunable field names (defaults to <see cref="BalanceSuggestionApplier.TunableFields"/>).</summary>
        public IReadOnlyList<string> TunableFields { get; set; } = BalanceSuggestionApplier.TunableFields;
    }

    /// <summary>One editable, per-field balance suggestion: a target unit id, a tunable field (snake_case), the proposed
    /// new value, the current value (advisory/display only), and a one-line rationale. Nothing is auto-applied — the
    /// creator reviews/edits/discards it, and an applied value is re-gated through
    /// <see cref="BalanceSuggestionApplier.TryApply"/>.</summary>
    public sealed class BalanceSuggestion
    {
        [JsonPropertyName("unit_id")]   public string UnitId { get; set; } = "";
        [JsonPropertyName("field")]     public string Field { get; set; } = "";
        [JsonPropertyName("proposed")]  public double Proposed { get; set; }
        [JsonPropertyName("current")]   public double Current { get; set; }
        [JsonPropertyName("rationale")] public string Rationale { get; set; } = "";
    }

    /// <summary>The parsed, editable result of a balance analysis — a flat list of per-field <see cref="BalanceSuggestion"/>s.</summary>
    public sealed class BalanceReport
    {
        [JsonPropertyName("suggestions")]
        public List<BalanceSuggestion> Suggestions { get; set; } = new();
    }
}
