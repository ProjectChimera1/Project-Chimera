#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>Which of the 5 Faction Definer wizard steps a located error belongs to (Story 5.5, FR-17/UX-DR40) —
    /// the panel jumps to this step when Finish is blocked. Ordinal order matches the wizard's step sequence
    /// (<c>ChimeraTabs</c> segment order in <c>FactionDefinerPanel</c>): Name &amp; Color, Roster, Buildings &amp;
    /// Tech, Starting Conditions, AI Preset.</summary>
    public enum FactionDefinerStep
    {
        NameColor = 0,
        Roster = 1,
        BuildingsTech = 2,
        StartingConditions = 3,
        AiPreset = 4,
    }

    /// <summary>One pickable preset option surfaced by <see cref="FactionDefinerWizardCore.ScanPresets"/> — pairs a
    /// deep-cloned authored definition with the faction file id it was scanned from (so the picker UI can label an
    /// option "[alpha] worker" and disambiguate an id that recurs across scanned files).</summary>
    public sealed class FactionPresetOption<T>
    {
        public string SourceFactionId { get; }
        public T Def { get; }
        public FactionPresetOption(string sourceFactionId, T def)
        {
            SourceFactionId = sourceFactionId;
            Def = def;
        }
    }

    /// <summary>The scanned pool of units/buildings/research available to the wizard's Roster / Buildings &amp; Tech
    /// preset-picker steps (Story 5.5). Populated by <see cref="FactionDefinerWizardCore.ScanPresets"/>.</summary>
    public sealed class FactionPresetPool
    {
        public List<FactionPresetOption<UnitDefinition>> Units { get; } = new();
        public List<FactionPresetOption<BuildingDefinition>> Buildings { get; } = new();
        public List<FactionPresetOption<ResearchDefinition>> Research { get; } = new();
    }

    /// <summary>The outcome of a Faction Definer Finish/save attempt (Story 5.5, AR-39). On failure, carries every
    /// located <see cref="FactionValidator"/>/target-exists error (list-all) plus the step the FIRST error maps to,
    /// so the panel can jump straight to the offending step. On success, carries the absolute path written.</summary>
    public readonly struct FactionDefinerFinishResult
    {
        public bool Ok { get; }
        public IReadOnlyList<(string FieldPath, string Message)> Errors { get; }
        public FactionDefinerStep? Step { get; }
        public string? WrittenPath { get; }

        private FactionDefinerFinishResult(bool ok, IReadOnlyList<(string, string)> errors, FactionDefinerStep? step, string? writtenPath)
        {
            Ok = ok;
            Errors = errors;
            Step = step;
            WrittenPath = writtenPath;
        }

        public static FactionDefinerFinishResult Success(string writtenPath) =>
            new(true, Array.Empty<(string, string)>(), null, writtenPath);

        public static FactionDefinerFinishResult Failure(IReadOnlyList<(string FieldPath, string Message)> errors)
        {
            FactionDefinerStep step = errors.Count > 0
                ? FactionDefinerWizardCore.StepForError(errors[0].FieldPath, errors[0].Message)
                : FactionDefinerStep.NameColor;
            return new FactionDefinerFinishResult(false, errors, step, null);
        }
    }

    /// <summary>
    /// Story 5.5 (FR-17, UX-DR40) — the Godot-free core of the Faction Definer guided wizard: on-disk preset-pool
    /// scanning + deep-clone, the located-error → step mapping the panel uses to jump to the offending step, and the
    /// <see cref="FactionValidator.ValidateComplete"/>-gated atomic Finish/save (tmp write +
    /// <see cref="FactionDefinition.LoadFromFile"/> self-check + <see cref="File.Move(string, string, bool)"/>,
    /// target-exists guard). Mirrors the established "Godot-free core of a presentation feature" pattern so the
    /// wizard's testable assembly/finish logic lives under <c>src/Core/Definitions</c> and is Tier-1 unit-testable
    /// without a live Panel node — the presentation panel (<c>FactionDefinerPanel</c>/<c>.Steps.cs</c>) is a thin
    /// Godot wrapper that resolves <c>res://</c> paths at the Godot edge (<c>ProjectSettings.GlobalizePath</c>) and
    /// calls these plain methods. Pure C# — no <c>using Godot</c>, no logging.
    /// </summary>
    public static class FactionDefinerWizardCore
    {
        /// <summary>Human-readable re-serialize options (2-space indent), matching <see cref="FactionWriter"/>'s
        /// own <c>IndentedOptions</c> so a wizard-written file looks like every other hand-authored/editor-written
        /// faction file.</summary>
        private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

        /// <summary>
        /// The suffix+extension a Finish-written faction file gets: <c>&lt;id&gt;_faction.json</c>. Named (DW-528)
        /// rather than inlined so the one seam this entry is about — the decoration between a free-text id and the
        /// name that hits the filesystem — is a single obvious edit point. Shortening, parameterizing or dropping it
        /// needs NO change to the reserved-device guard in <see cref="TryFinish"/>: that guard inspects the assembled
        /// file name, so it re-derives the right verdict from whatever this becomes.
        /// <para>Must stay consistent with the <c>"*_faction.json"</c> discovery globs a written file is later found
        /// by (<see cref="FactionDefinition"/>'s directory load and <c>SkirmishCatalog</c>) — a file this wizard
        /// writes under a name those globs miss would save successfully and then be invisible.</para>
        /// </summary>
        public const string FactionFileSuffix = "_faction.json";

        /// <summary>
        /// Scan the given absolute faction-JSON paths (alpha/beta today — Story 5.5's "Epics 2-4 content" pool) for
        /// the Roster / Buildings &amp; Tech preset pools. A path that fails to load (missing file, invalid JSON) is
        /// skipped defensively — never throws; the pool is a picker convenience surface, not a load-time gate. Every
        /// option is DEEP-CLONED off the parsed def (via <see cref="DeepClone{T}"/>) so a later pick/unpick, or a
        /// second wizard session reusing the same scan, can never alias back into a shared instance.
        /// </summary>
        public static FactionPresetPool ScanPresets(IEnumerable<string> factionJsonAbsolutePaths)
        {
            var pool = new FactionPresetPool();
            if (factionJsonAbsolutePaths == null) return pool;

            foreach (string path in factionJsonAbsolutePaths)
            {
                FactionDefinition def;
                try { def = FactionDefinition.LoadFromFile(path); }
                catch { continue; }

                string sourceId = string.IsNullOrEmpty(def.Id) ? Path.GetFileNameWithoutExtension(path) : def.Id;

                foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
                {
                    if (u == null) continue;
                    try { pool.Units.Add(new FactionPresetOption<UnitDefinition>(sourceId, DeepClone(u))); }
                    catch { continue; }
                }
                foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
                {
                    if (b == null) continue;
                    try { pool.Buildings.Add(new FactionPresetOption<BuildingDefinition>(sourceId, DeepClone(b))); }
                    catch { continue; }
                }
                foreach (ResearchDefinition r in def.Research ?? new List<ResearchDefinition>())
                {
                    if (r == null) continue;
                    try { pool.Research.Add(new FactionPresetOption<ResearchDefinition>(sourceId, DeepClone(r))); }
                    catch { continue; }
                }
            }
            return pool;
        }

        /// <summary>Deep-clone a definition via a JSON round-trip through <see cref="FactionDefinition.JsonOptions"/>
        /// — so a picked preset added to the new faction's list never aliases the scanned source file's instance (or
        /// another picker's pooled instance). <typeparamref name="T"/> must be the CONCRETE runtime type (e.g.
        /// <see cref="BuildingDefinition"/>, not <see cref="UnitDefinition"/>) so the clone preserves the derived
        /// fields.</summary>
        public static T DeepClone<T>(T source) where T : notnull
        {
            string json = JsonSerializer.Serialize(source, FactionDefinition.JsonOptions);
            return JsonSerializer.Deserialize<T>(json, FactionDefinition.JsonOptions)
                   ?? throw new InvalidOperationException("DeepClone: round-trip produced null.");
        }

        /// <summary>
        /// Map one <see cref="FactionValidator"/>-located (or target-exists / raw-JSON parse) error to the wizard step
        /// it belongs to. Most field paths are unambiguous (<c>color</c>/<c>id</c> → Name &amp; Color,
        /// <c>ai_preset</c>/<c>signature_mechanic*</c> → AI Preset, <c>units</c>/<c>hero_unit_id</c> → Roster,
        /// <c>starting_ore</c>/<c>starting_crystal</c> → Starting Conditions, <c>raw_json</c> → Name &amp; Color).
        /// The rest (<c>buildings</c>, <c>prerequisites</c>, <c>cost</c>, <c>mesh_path</c>, <c>research</c>, and the
        /// building-only required fields <c>hp</c>/<c>construction_time</c>/<c>supply_bonus</c>/
        /// <c>produces_category</c>) are shared between a unit (Roster) and a building (Buildings &amp; Tech) —
        /// disambiguated by sniffing the message's leading <c>"unit '"</c>/<c>"building '"</c> kind label, the shared
        /// wording convention <see cref="TechTreeValidator"/>/<see cref="ResourceCostValidator"/>/<see
        /// cref="BuildingDefinitionValidator"/> all use. Falls back to Buildings &amp; Tech when neither prefix
        /// matches (a faction-level structural message, e.g. a null list, or a research entry — research lives in the
        /// combined Buildings &amp; Tech step per this story's spec).
        ///
        /// <para><b>DW-114/DW-116 (step-route hardening).</b> Every field path any error-producing surface can name is
        /// now EXPLICIT here rather than relying on the Buildings &amp; Tech sniff-default, which has no UI for any of
        /// them. That default is a fallback for genuinely-ambiguous shared paths, not a place for a known field to
        /// land: a creator sent to Buildings &amp; Tech for a negative <c>starting_ore</c> sees no offending control at
        /// all. <c>starting_ore</c>/<c>starting_crystal</c> became LIVE (not latent) once
        /// <see cref="FactionValidator.Validate"/> gained DW-115's finite-and-non-negative check — they are the two
        /// controls the Starting Conditions step actually renders, so that is where the remedy is. The remaining
        /// additions are still unreachable today (no validator emits them) and are pre-wired so a later check cannot
        /// silently misroute: <c>signature_mechanic</c>/<c>signature_mechanic_display</c> join the already-routed
        /// <c>signature_mechanic_effect_id</c> at AI Preset, the faction-config-level step that hosts the other
        /// descriptor fields; <c>raw_json</c> (a <see cref="TryFinishFromRawJson"/> parse failure) maps to Name &amp;
        /// Color, the wizard's first step — Advanced mode has no step tabs to jump to and
        /// <c>FactionDefinerPanel.OnFinishPressed</c> deliberately skips the jump there, so this exists purely so any
        /// FUTURE consumer of <see cref="FactionDefinerFinishResult.Step"/> (logging, an error-label step chip, a
        /// different UI surface) reads a defensible step instead of a misleading Buildings &amp; Tech.</para>
        ///
        /// <para><b>DW-735/DW-776 (the last field-path hole).</b> <c>faction</c> — the path both null-def guards emit,
        /// <see cref="TryFinish"/>'s and <see cref="FactionValidator.Validate"/>'s — was the one remaining known name
        /// still relying on the sniff-default. It now joins <c>raw_json</c> at Name &amp; Color on the same reasoning:
        /// the error names the whole draft, not a control, so the first step is the defensible landing spot. With this
        /// case the DW-114 invariant is CLOSED — every field path any error-producing surface in this codebase can
        /// name is explicit above, and the fallthrough below is reserved for genuinely-shared item paths.</para>
        ///
        /// <para><b>DW-505 (the kind-label sniff reads the REASON).</b> The sniff used to run on the raw message, so it
        /// only matched a message whose kind label was literally first. <see cref="FactionValidator.ValidateComplete"/>
        /// wrapped its two item-level <c>mesh_path</c> errors in the FACTION-level <c>"faction '&lt;id&gt;'.mesh_path: "</c>
        /// prefix, which pushed the label off the front and dropped every missing-UNIT-mesh_path error on the
        /// Buildings &amp; Tech fallback — a step with no roster control, in the one flow (Finish) where the author is
        /// already blocked. That validator now emits the item-level shape, and this method additionally strips a
        /// faction-located prefix before sniffing, so neither side alone can re-open the mis-route. The two mesh_path
        /// producers (that validator and <see cref="MeshAssetLint"/>) now share one message shape.</para>
        /// </summary>
        public static FactionDefinerStep StepForError(string fieldPath, string message)
        {
            switch (fieldPath)
            {
                case "color": return FactionDefinerStep.NameColor;
                case "id": return FactionDefinerStep.NameColor;        // the target-exists collision names the id field
                case "ai_preset": return FactionDefinerStep.AiPreset;
                case "units": return FactionDefinerStep.Roster;
                // DW-106 / DW-114: a hero is a roster unit — the Roster step is where the remedy lives (pick/unpick
                // the referenced unit). A signature_mechanic_* field is not editable in any Simple step; route it to
                // AI Preset, a defensible faction-config-level default (per DW-114's routing note).
                case "hero_unit_id": return FactionDefinerStep.Roster;
                case "signature_mechanic": return FactionDefinerStep.AiPreset;
                case "signature_mechanic_display": return FactionDefinerStep.AiPreset;
                case "signature_mechanic_effect_id": return FactionDefinerStep.AiPreset;
                // DW-114/DW-115: the two economy fields the Starting Conditions step renders as editable inputs.
                case "starting_ore": return FactionDefinerStep.StartingConditions;
                case "starting_crystal": return FactionDefinerStep.StartingConditions;
                // DW-116: a raw-JSON parse failure names no wizard field at all — the whole document is wrong. Name &
                // Color (the first step) is the defensible landing spot; Advanced mode itself never reads Step.
                case "raw_json": return FactionDefinerStep.NameColor;
                // DW-735/DW-776: the last hole in the DW-114 invariant. Both null-def guards — TryFinish's and
                // FactionValidator.Validate's — emit ("faction", "faction is null."), a field path that names the
                // WHOLE draft rather than any one control, so it used to fall through to the Buildings & Tech
                // sniff-default (a step with no relevant UI whatsoever). Same remedy as raw_json above and for the
                // same reason: nothing on any step edits "the faction is missing", so Name & Color — the first step —
                // is the defensible landing spot. Latent, not live: the panel always holds a live _draft, so no
                // caller observes a null def today; the case exists so a FUTURE producer cannot silently misroute.
                case "faction": return FactionDefinerStep.NameColor;
            }
            // DW-505: sniff the kind label on the REASON, not on the raw message. The label normally leads
            // (MeshAssetLint / UnitDefinitionValidator / BuildingDefinitionValidator / TechTreeValidator /
            // ResourceCostValidator all emit "unit '<id>'.<field>: <reason>"), but a faction-level validator can wrap
            // an item-level reason in its own "faction '<id>'.<field>: " prefix — which used to defeat this match
            // outright and drop a missing-unit-mesh_path error on the Buildings & Tech fallback. FactionValidator no
            // longer produces that shape (it emits the item-level form for its two mesh_path errors), and stripping
            // the prefix here keeps the routing correct for any future faction-located message that carries a kind
            // label. A genuine faction-level reason ("units list is null.", "duplicate building id 'x' …") carries no
            // kind label, so it still falls through to Buildings & Tech exactly as before.
            string reason = StripFactionLocatedPrefix(message);
            if (reason.StartsWith("unit '", StringComparison.Ordinal))
                return FactionDefinerStep.Roster;
            return FactionDefinerStep.BuildingsTech;
        }

        /// <summary>The head of <see cref="FactionValidator"/>'s faction-level located idiom,
        /// <c>"faction '{id}'.{path}: {reason}"</c>.</summary>
        private const string FactionLocatedHead = "faction '";

        /// <summary>
        /// Return <paramref name="message"/> with a leading faction-level located prefix
        /// (<c>"faction '{id}'.{path}: "</c>) removed, or the message unchanged when it carries no such prefix
        /// (DW-505). Null/empty reads as <c>""</c>. Parses positionally — the first <c>'.</c> after the opening quote
        /// ends the id, the first <c>": "</c> after that ends the field path — rather than assuming a fixed id shape,
        /// because a faction id is free text. A pathological id containing <c>'.</c> can mis-split, which at worst
        /// yields a substring that matches no kind label and lands on the same Buildings &amp; Tech fallback an
        /// unparsed message would have: a wrong tab is the entire blast radius, never an exception.
        /// </summary>
        private static string StripFactionLocatedPrefix(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            if (!message.StartsWith(FactionLocatedHead, StringComparison.Ordinal)) return message;

            int idEnd = message.IndexOf("'.", FactionLocatedHead.Length, StringComparison.Ordinal);
            if (idEnd < 0) return message;

            int reasonStart = message.IndexOf(": ", idEnd + 2, StringComparison.Ordinal);
            if (reasonStart < 0) return message;

            return message[(reasonStart + 2)..];
        }

        /// <summary>
        /// Clear a dangling <see cref="FactionDefinition.HeroUnitId"/> — one that no longer names any unit in
        /// <paramref name="draft"/>'s <see cref="FactionDefinition.Units"/> — so a hero pick that survives a later
        /// roster unpick (Simple mode: Back → Roster → uncheck the hero's unit) or an Advanced raw-JSON edit can
        /// never persist a reference to a unit that isn't actually in the faction (Story 5.6, Spec Change Log,
        /// review pass 1). Null-guards <paramref name="draft"/> and <paramref name="draft"/>.<see
        /// cref="FactionDefinition.Units"/> (a raw-JSON-deserialized def, or a hand-built one, can carry a null
        /// <c>Units</c> list) before scanning — never throws. Returns true when a clear actually happened (i.e.
        /// <see cref="FactionDefinition.HeroUnitId"/> was non-empty and did not match any unit's <c>Id</c>); false
        /// when there was nothing to clear (already null/empty, or it still resolves).
        /// </summary>
        public static bool ClearStaleHeroReference(FactionDefinition? draft)
        {
            if (draft == null) return false;
            if (string.IsNullOrEmpty(draft.HeroUnitId)) return false;

            bool stillExists = draft.Units != null && draft.Units.Any(u => u != null && u.Id == draft.HeroUnitId);
            if (stillExists) return false;

            draft.HeroUnitId = null;
            return true;
        }

        /// <summary>
        /// Advanced-mode Finish (Story 5.6, FR-18): parse <paramref name="json"/> into a <see cref="FactionDefinition"/>
        /// via <see cref="FactionDefinition.JsonOptions"/> (the same lenient options — comments/trailing commas
        /// tolerated — every other loader in this codebase uses), then delegate UNCHANGED to <see cref="TryFinish"/>
        /// — the Advanced raw-JSON pane runs through the exact same <see cref="FactionValidator.ValidateComplete"/>
        /// gate (including the <see cref="ClearStaleHeroReference"/> call inside it, and DW-104's <c>mesh_path</c>
        /// disk-existence lint — <paramref name="meshExists"/> is threaded straight through) as the Simple
        /// guided-wizard path; no separate/weaker validation. A parse failure (malformed JSON) or a null deserialize result (e.g.
        /// the literal <c>"null"</c>) is a located <c>("raw_json", …)</c> <see cref="FactionDefinerFinishResult.Failure"/>
        /// — never throws.
        ///
        /// <para>DW-117: the POCO deserialize leaves <see cref="FactionDefinition.AiPreset"/> at its C# default
        /// (<c>"balanced"</c>) when the document OMITS the <c>ai_preset</c> key entirely, which would silently pass
        /// validation with an unauthored preset (the opposite of Simple mode, whose forced empty <c>AiPreset</c>
        /// makes omission impossible). To close that bypass, after a successful deserialize this re-inspects the SAME
        /// text via <see cref="JsonDocument"/> (with equivalently-lenient options: comments/trailing commas tolerated,
        /// and — critically — duplicate property names accepted exactly as <see cref="JsonSerializer"/> accepts them,
        /// which <see cref="JsonNode"/> does NOT); if the root object has no <c>ai_preset</c> key,
        /// <see cref="FactionDefinition.AiPreset"/> is forced to <c>""</c> so the omitted key flows through the exact
        /// same <see cref="FactionValidator.ValidateComplete"/> "must be authored" rejection Simple mode produces. A
        /// key that is PRESENT (even <c>""</c>/<c>null</c>) keeps its existing outcome. The re-inspection is
        /// best-effort and guarded so it can never turn an accepted document into a throw.</para>
        /// </summary>
        public static FactionDefinerFinishResult TryFinishFromRawJson(string json, string factionsDirAbsolute,
            AbilityRegistry? abilityRegistry = null, Func<string, bool>? meshExists = null)
        {
            FactionDefinition? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<FactionDefinition>(json ?? "", FactionDefinition.JsonOptions);
            }
            catch (Exception ex)
            {
                return FactionDefinerFinishResult.Failure(new (string, string)[]
                {
                    ("raw_json", $"could not parse JSON: {ex.Message}"),
                });
            }

            if (parsed == null)
            {
                return FactionDefinerFinishResult.Failure(new (string, string)[]
                {
                    ("raw_json", "JSON parsed to no faction object (e.g. the literal 'null') — a faction object is required."),
                });
            }

            // DW-117: distinguish "ai_preset key omitted" (which would inherit the silent "balanced" default and
            // bypass the "must be authored" gate Simple mode enforces) from "key present". Use JsonDocument.Parse,
            // NOT JsonNode.Parse: JsonDocument tolerates duplicate property names exactly as the JsonSerializer
            // deserialize above does (last-wins), so every document the deserialize accepted also re-parses here.
            // JsonNode.Parse instead THROWS on duplicate keys — which the best-effort catch would swallow, silently
            // skipping this check and reopening the bypass for a document that omits ai_preset AND duplicates some
            // other key. Case-sensitive on the exact literal "ai_preset" to match FactionDefinition.JsonOptions (no
            // PropertyNameCaseInsensitive), so an off-case key is correctly treated as absent. Best-effort: a
            // re-parse failure leaves parsed unchanged rather than throwing, preserving the never-throws contract.
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json ?? "",
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && !doc.RootElement.TryGetProperty("ai_preset", out _))
                {
                    parsed.AiPreset = "";   // omitted key == Simple mode's forced "" -> validator "must be authored"
                }
            }
            catch { /* re-inspection is best-effort; never turn an accepted doc into a throw */ }

            return TryFinish(parsed, factionsDirAbsolute, abilityRegistry, meshExists);
        }

        /// <summary>
        /// Run the Finish/save gate (Story 5.5, D-1): <see cref="FactionValidator.ValidateComplete"/> first — block
        /// and locate on ANY failure (list-all). Only on a clean pass does this check whether the target
        /// <c>{id}_faction.json</c> already exists under <paramref name="factionsDirAbsolute"/> — this wizard always
        /// creates a BRAND-NEW file (unlike a sibling editor's legitimate patch-in-place), so an existing target
        /// refuses instead of overwriting. Only when the target is free: write a <c>.tmp</c> file via
        /// <see cref="SerializeDraftClean"/> (a hand-built top-level scalar object plus <c>units</c>/<c>buildings</c>/
        /// <c>research</c> arrays assembled from <see cref="FactionWriter"/>'s per-item clean serializers — NEVER a
        /// whole-object <c>JsonSerializer.Serialize</c>, which would leak the computed <c>Parsed*</c> getters and a
        /// duplicated <c>PrimaryUnit</c>, see <see cref="FactionWriter"/>'s own doc comment), self-check via
        /// <see cref="FactionDefinition.LoadFromFile"/>, then <see cref="File.Move(string, string, bool)"/> with
        /// <c>overwrite:false</c>. Never throws — every failure mode returns a located
        /// <see cref="FactionDefinerFinishResult"/>; a stray <c>.tmp</c> is cleaned up on any exception.
        ///
        /// <para><b>DW-104: the mesh_path disk-existence lint (recorded decision 2026-07-19, re-affirmed
        /// 2026-07-25).</b> <see cref="FactionValidator.ValidateComplete"/> only knows whether <c>mesh_path</c> is
        /// non-blank, so a DANGLING path shipped silently. Between that gate and the write, every authored
        /// <c>res://</c> <c>mesh_path</c> is now checked for actual existence by <see cref="MeshAssetLint"/> — the
        /// separate, explicitly-named content lint the decision asked for, so the sim validator itself stays
        /// filesystem-free. <paramref name="meshExists"/> lets a caller inject a better probe (Godot's
        /// <c>ResourceLoader.Exists</c>, which also understands import-remapped resources); when it is null the probe
        /// is derived from <paramref name="factionsDirAbsolute"/> by walking up to the enclosing <c>project.godot</c>,
        /// and when there is no such project tree on disk (an exported build, a unit test's bare temp directory) the
        /// lint is skipped rather than rejecting every path it cannot resolve.</para>
        ///
        /// <para><b>DW-528: the reserved-device-basename guard.</b> The id is free text and this method is where it
        /// becomes a file name, so alongside the path-separator/traversal reject the assembled
        /// <c>&lt;id&gt;<see cref="FactionFileSuffix"/></c> is run through
        /// <see cref="UnitDefinitionValidator.IsReservedDeviceFileName"/> — the same DW-454 convention the unit,
        /// building and item id gates enforce. Checked on the assembled NAME (not the bare id) because Win32 reserves
        /// only the segment before the first <c>'.'</c>: that both spares an id the suffix already makes safe
        /// (<c>con</c> → <c>con_faction.json</c>, an ordinary file) and catches one it does not (<c>con.x</c> →
        /// <c>con.x_faction.json</c>, the CON device). A portability gate — see the inline comment for what modern
        /// Windows builds actually enforce.</para>
        ///
        /// <para><b>DW-112: the <c>File.Exists</c> → <c>File.Move</c> TOCTOU window.</b> The target-exists pre-check
        /// and the <c>overwrite:false</c> move are two separate filesystem observations, so a target that appears
        /// BETWEEN them (a second wizard session, an external tool) used to fall through to the generic
        /// <c>"save failed: {ex.Message}"</c> branch and hand the creator a raw OS string ("Cannot create a file when
        /// that file already exists.") for the exact situation the pre-check words helpfully. The move now classifies
        /// its own failure through <see cref="TryClassifyTargetCollision"/>: when the destination name turns out to be
        /// taken, the SAME located <c>id</c> error the pre-check produces is returned instead — one shared builder
        /// (<see cref="TargetFileExistsFailure"/>) so the two wordings cannot drift. The atomic move was never the
        /// risk (<c>overwrite:false</c> still refuses to clobber); this is purely the UX half. A destination occupied
        /// by a DIRECTORY is the same class of problem and the same remedy (choose a different id) — the pre-check's
        /// <c>File.Exists</c> cannot see it at all, so it is classified here with its own accurate wording rather than
        /// left on the opaque generic branch. Any other write failure keeps the generic message unchanged.</para>
        /// </summary>
        public static FactionDefinerFinishResult TryFinish(FactionDefinition def, string factionsDirAbsolute,
            AbilityRegistry? abilityRegistry = null, Func<string, bool>? meshExists = null)
        {
            if (def == null)
                return FactionDefinerFinishResult.Failure(new (string, string)[] { ("faction", "faction is null.") });

            // Story 5.6 (Spec Change Log, review pass 1): the SOLE enforcement point for "a dangling HeroUnitId
            // never reaches a written file" — the Panel's step-render call to this same method (BuildAiPresetStep)
            // is early UI feedback only, since Finish is reachable from any step (Simple) and the Advanced raw-JSON
            // path never renders the AI Preset step at all. Runs before ValidateComplete so a cleared reference
            // never trips a downstream check that doesn't even look at HeroUnitId (nothing does today).
            ClearStaleHeroReference(def);

            FactionValidationResult validation = FactionValidator.ValidateComplete(def, abilityRegistry);
            if (!validation.Ok)
                return FactionDefinerFinishResult.Failure(validation.Errors.ToList());

            // DW-104: the mesh_path disk-existence lint — deliberately NOT inside FactionValidator (see the method
            // doc). Runs after the completeness gate so a blank mesh_path is reported once, by the validator that owns
            // that axis, instead of twice.
            IReadOnlyList<(string FieldPath, string Message)> missingMeshes = MeshAssetLint.FindMissingMeshFiles(
                def, meshExists ?? MeshAssetLint.TryMakeResExistsProbe(factionsDirAbsolute));
            if (missingMeshes.Count > 0)
                return FactionDefinerFinishResult.Failure(missingMeshes);

            string id = def.Id ?? "";
            if (string.IsNullOrWhiteSpace(id))
            {
                return FactionDefinerFinishResult.Failure(new (string, string)[]
                {
                    ("id", "faction id must be authored (in the Name & Color step) before Finish."),
                });
            }

            // Reject path-separator/traversal/invalid-filename characters BEFORE the id is used to build a
            // filesystem path below — the id is free-text from the Name & Color step's Input field, and Path.Combine
            // does not itself guard against escaping factionsDirAbsolute (e.g. an id of "../evil" or "sub/dir").
            // '/' and '\' are checked explicitly (not just via Path.GetInvalidFileNameChars(), which is platform-
            // dependent and does not flag '\' as invalid on Linux) since an authored id must be safe as a bare
            // filename segment on every platform this project targets (Windows desktop, Linux headless server).
            if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains("..", StringComparison.Ordinal)
                || id.Contains('/') || id.Contains('\\'))
            {
                return FactionDefinerFinishResult.Failure(new (string, string)[]
                {
                    ("id", $"faction id '{id}' contains characters that are not allowed in a filename " +
                           "(no path separators, no '..')."),
                });
            }

            // The single place the free-text id becomes a file name. Built into a local FIRST so the reserved-device
            // guard below inspects the exact name that is about to be written, not a reconstruction of it.
            string fileName = $"{id}{FactionFileSuffix}";

            // DW-528: wire DW-454's reserved-device-basename convention (the one the unit/building/item id validators
            // already enforce) into this free-text filename path, so the safety here is EXPLICIT rather than an
            // accident of the "_faction" suffix.
            //
            // Checked on the ASSEMBLED fileName, not on the bare id, because Win32 reserves only the leading segment
            // — everything before the FIRST '.' ("NUL.tar.gz is equivalent to NUL", Naming Files/Paths/Namespaces).
            // That distinction is the whole point: today's suffix makes a bare "con" harmless (`con_faction.json` is
            // an ordinary file, and refusing it would be a gratuitous restriction), while an id of "con.x" or "nul."
            // is NOT harmless — it passes the separator/traversal guard above ('.' is a legal filename char and there
            // is no "..") and still assembles a reserved leading segment. Reading the assembled name also means this
            // guard stays correct with no further edit if FactionFileSuffix is ever shortened, parameterized or
            // dropped. The sibling ".tmp" write needs no separate check: it shares this name's leading segment.
            //
            // Scope, measured rather than assumed: whether the filesystem itself REFUSES such a name depends on the
            // Windows build — this project's dev machine (Win11 26200) creates `con.json` happily, from .NET and from
            // cmd.exe alike, so DW-454's "the write throws an opaque Save failed" symptom is not observable there.
            // The reject is therefore a PORTABILITY gate, not a local crash fix: a faction file whose basename is a
            // DOS device is still unopenable by every Windows build and third-party tool that does enforce the
            // reservation, and authored content is meant to be shared. Authoring-time reject only — nothing folded
            // into SimChecksum/ContentHash/StartStateHash moves.
            if (UnitDefinitionValidator.IsReservedDeviceFileName(fileName))
            {
                return FactionDefinerFinishResult.Failure(new (string, string)[]
                {
                    ("id", $"faction id '{id}' makes the file '{fileName}', whose basename is a Windows reserved " +
                           $"device name ({UnitDefinitionValidator.ReservedPipeList}) — Windows matches everything " +
                           "before the first '.', and systems that enforce the reservation cannot open such a file, " +
                           "so rename before saving."),
                });
            }

            string targetAbs = Path.Combine(factionsDirAbsolute, fileName);
            if (File.Exists(targetAbs))
                return TargetFileExistsFailure(targetAbs);

            string tmp = targetAbs + ".tmp";
            // DW-112: distinguishes "the MOVE failed" from "the serialize/write/self-check failed". Only a move
            // failure may be re-read as a target-name collision — a write that failed for its own reason (a locked
            // .tmp, a full disk) must keep reporting that reason even if some other process happens to have taken
            // the target name in the meantime, because that reason is the truthful account of what went wrong.
            bool moveAttempted = false;
            try
            {
                string json = SerializeDraftClean(def);
                File.WriteAllText(tmp, json);
                _ = FactionDefinition.LoadFromFile(tmp);   // self-check: refuse to report success for a file that won't reload
                moveAttempted = true;
                File.Move(tmp, targetAbs, overwrite: false);
                return FactionDefinerFinishResult.Success(targetAbs);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave no stray .tmp */ }

                // DW-112: the pre-check above and the overwrite:false move are two separate observations of the same
                // fact, so a target created in the window between them lands here. Classify ONCE (the classifier
                // re-probes the filesystem, and probing twice could itself observe two different states), and fall
                // back to the unchanged generic message for every failure that is not a name collision.
                FactionDefinerFinishResult? collision =
                    moveAttempted ? TryClassifyTargetCollision(ex, targetAbs) : null;
                return collision ?? FactionDefinerFinishResult.Failure(
                    new (string, string)[] { ("id", $"save failed: {ex.Message}") });
            }
        }

        /// <summary>
        /// DW-112 — re-read a failed <see cref="File.Move(string, string, bool)"/> as a target-name collision, or
        /// return null when it is not one (leaving the caller's generic <c>"save failed: …"</c> message in place).
        ///
        /// <para>Classifies by re-probing the destination rather than by inspecting the exception's platform-specific
        /// <see cref="Exception.HResult"/>/errno: an <c>overwrite:false</c> move that fails while the destination is
        /// occupied IS the collision, on every platform, and the probe stays readable. Only
        /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> are eligible (the two families a
        /// filesystem move raises) so an unrelated failure type can never be dressed up as a collision.</para>
        ///
        /// <para>Internal, not private, so the Tier-1 suite can pin the concurrent-FILE arm directly — that arm needs
        /// a target that materialises strictly between the pre-check and the move, which no single-threaded test can
        /// stage. The directory arm IS reachable end-to-end (<see cref="File.Exists"/> is blind to a directory, so the
        /// pre-check waves it through and the move collides), and covers the wiring from the catch into this method.</para>
        /// </summary>
        internal static FactionDefinerFinishResult? TryClassifyTargetCollision(Exception moveFailure, string targetAbs)
        {
            if (moveFailure is not IOException and not UnauthorizedAccessException) return null;
            if (File.Exists(targetAbs)) return TargetFileExistsFailure(targetAbs);
            if (Directory.Exists(targetAbs)) return TargetDirectoryBlocksFailure(targetAbs);
            return null;
        }

        /// <summary>DW-112 — the SINGLE producer of the "that faction file already exists, pick another id" located
        /// error, shared by <see cref="TryFinish"/>'s target-exists pre-check and by
        /// <see cref="TryClassifyTargetCollision"/>'s post-move re-read, so the two can never word the same fact
        /// differently. Names the <c>id</c> field, which <see cref="StepForError"/> routes to
        /// <see cref="FactionDefinerStep.NameColor"/> — the step holding the control that fixes it.</summary>
        private static FactionDefinerFinishResult TargetFileExistsFailure(string targetAbs) =>
            FactionDefinerFinishResult.Failure(new (string, string)[]
            {
                ("id", $"a faction file already exists at '{targetAbs}' — choose a different id " +
                       "(an existing faction file is never overwritten)."),
            });

        /// <summary>DW-112 — the destination NAME is taken by a directory. Same remedy as
        /// <see cref="TargetFileExistsFailure"/> (choose a different id) and the same <c>id</c> field, but worded
        /// accurately: nothing is being overwritten and there is no existing faction file to preserve. Only reachable
        /// after the move, since <see cref="File.Exists"/> reports false for a directory.</summary>
        private static FactionDefinerFinishResult TargetDirectoryBlocksFailure(string targetAbs) =>
            FactionDefinerFinishResult.Failure(new (string, string)[]
            {
                ("id", $"a folder already exists at '{targetAbs}', so the faction file cannot be written there — " +
                       "choose a different id."),
            });

        /// <summary>
        /// Assemble the Finish-write JSON for a brand-new faction file: a fresh top-level <see cref="JsonObject"/>
        /// for the faction's scalar fields (<c>id</c>/<c>display_name</c>/<c>color</c>/<c>ai_preset</c>/
        /// <c>signature_mechanic*</c>/<c>hero_unit_id</c>/<c>persistence_enabled</c>/<c>starting_ore</c>/
        /// <c>starting_crystal</c>) plus <c>units</c>/<c>buildings</c>/<c>research</c> arrays built by parsing each
        /// picked <see cref="UnitDefinition"/>/<see cref="BuildingDefinition"/>/<see cref="ResearchDefinition"/>
        /// through <see cref="FactionWriter.SerializeUnitClean"/>/<see cref="FactionWriter.SerializeBuildingClean"/>/
        /// <see cref="FactionWriter.SerializeResearchClean"/> — each already returns a clean, indented single-object
        /// JSON string with no <c>Parsed*</c> getter and no ballooned default. This is the ONLY sanctioned way to
        /// turn the wizard's picked items into JSON (Design Notes / Spec Change Log, Review Loop 1): a direct
        /// <c>JsonSerializer.Serialize</c> on the whole <see cref="FactionDefinition"/>/<see cref="UnitDefinition"/>/
        /// <see cref="BuildingDefinition"/> graph would leak six computed <c>Parsed*</c> int fields per unit/building
        /// plus <see cref="FactionDefinition.PrimaryUnit"/> as a duplicated nested object — corruption invisible to
        /// the Finish self-check because <see cref="FactionDefinition.LoadFromFile"/>'s deserialize silently ignores
        /// unmapped JSON keys.
        /// </summary>
        public static string SerializeDraftClean(FactionDefinition def)
        {
            var root = new JsonObject
            {
                ["id"] = def.Id ?? "",
                ["display_name"] = def.DisplayName ?? "",
            };

            var colorArr = new JsonArray();
            foreach (float c in def.Color ?? Array.Empty<float>())
                colorArr.Add((JsonNode)c);
            root["color"] = colorArr;

            var unitsArr = new JsonArray();
            foreach (UnitDefinition u in def.Units ?? new List<UnitDefinition>())
                if (u != null)
                    unitsArr.Add(JsonNode.Parse(FactionWriter.SerializeUnitClean(u)));
            root["units"] = unitsArr;

            var buildingsArr = new JsonArray();
            foreach (BuildingDefinition b in def.Buildings ?? new List<BuildingDefinition>())
                if (b != null)
                    buildingsArr.Add(JsonNode.Parse(FactionWriter.SerializeBuildingClean(b)));
            root["buildings"] = buildingsArr;

            var researchArr = new JsonArray();
            foreach (ResearchDefinition r in def.Research ?? new List<ResearchDefinition>())
                if (r != null)
                    researchArr.Add(JsonNode.Parse(FactionWriter.SerializeResearchClean(r)));
            root["research"] = researchArr;

            root["ai_preset"] = def.AiPreset ?? "";
            if (!string.IsNullOrEmpty(def.SignatureMechanicId)) root["signature_mechanic"] = def.SignatureMechanicId;
            if (!string.IsNullOrEmpty(def.SignatureMechanicDisplay)) root["signature_mechanic_display"] = def.SignatureMechanicDisplay;
            if (!string.IsNullOrEmpty(def.SignatureMechanicEffectId)) root["signature_mechanic_effect_id"] = def.SignatureMechanicEffectId;
            if (!string.IsNullOrEmpty(def.HeroUnitId)) root["hero_unit_id"] = def.HeroUnitId;
            root["persistence_enabled"] = def.PersistenceEnabled;
            root["starting_ore"] = def.StartingOre;
            root["starting_crystal"] = def.StartingCrystal;

            return root.ToJsonString(IndentedOptions);
        }
    }
}
