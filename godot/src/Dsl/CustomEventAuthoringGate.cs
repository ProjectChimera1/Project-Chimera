#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectChimera.Core;              // FactionRegistry
using ProjectChimera.Core.Definitions;  // ScenarioCustomEvent / ScenarioEventParam

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// The Godot-free authoring gate for ONE custom-event declaration typed into the trigger editor's events
    /// section (name / "name:Type, …" params / "0,1" allowed-raiser slots). Extracted from
    /// <c>TriggerEditorPanel.OnEventAddPressed</c> so the authoring rules are Tier-1 testable — the panel keeps
    /// only the LineEdit reads and the status-label write.
    ///
    /// <para>Runs the SAME closed-registry rules the pre-tick gate applies
    /// (<see cref="EventDispatchPlan.ValidateRegistry"/>) over the WOULD-BE registry, so a bad declaration is
    /// refused at authoring time with the gate's own message instead of failing the whole scenario later. Fail-closed
    /// and atomic: on any error <c>candidate</c> stays null, so the caller can never half-persist.</para>
    ///
    /// <para><b>DW-384.</b> The raiser-slot ceiling is derived from <see cref="FactionRegistry.PLAYER_COUNT"/> — the
    /// SAME bound <c>ScenarioValidator</c> passes at load time. The panel previously hardcoded
    /// <c>(int)Faction.Player4</c>, so the editor rejected allowed-raiser slots 4–7 that the engine accepts.</para>
    /// </summary>
    public static class CustomEventAuthoringGate
    {
        /// <summary>The exclusive faction-slot ceiling an authored allowed-raiser slot is checked against. Derived
        /// from <see cref="FactionRegistry.PLAYER_COUNT"/> (never a literal) so the authoring surface and the
        /// load-time gate can never disagree about which slots may raise an event (DW-384).</summary>
        public static int MaxRaiserSlotExclusive => FactionRegistry.PLAYER_COUNT;

        /// <summary>The authored param types a custom event may declare (mirrors ValidateRegistry's Int/Fixed/Bool
        /// rule, spelled here so the text surface can reject a typo before constructing anything).</summary>
        private static readonly string[] ParamTypeNames = { "Int", "Fixed", "Bool" };

        /// <summary>
        /// Validate a typed-in declaration against <paramref name="existing"/> and produce the WOULD-BE registry.
        /// </summary>
        /// <param name="existing">The scenario's currently declared custom events (may be null/empty).</param>
        /// <param name="rawName">The raw event-name text (trimmed here).</param>
        /// <param name="rawParams">The raw params text: comma-separated <c>name:Type</c> pairs; empty ⇒ none.</param>
        /// <param name="rawRaisers">The raw allowed-raiser text: comma-separated slot numbers; empty ⇒ system-only.</param>
        /// <param name="candidate">On success, <paramref name="existing"/> + the new declaration; null on failure.</param>
        /// <param name="error">On failure, an author-facing message (no decoration); null on success.</param>
        /// <returns>True when the declaration is accepted.</returns>
        public static bool TryDeclare(
            IReadOnlyList<ScenarioCustomEvent>? existing,
            string? rawName,
            string? rawParams,
            string? rawRaisers,
            out ScenarioCustomEvent[]? candidate,
            out string? error)
        {
            candidate = null;
            error     = null;

            string name = (rawName ?? string.Empty).Trim();
            if (name.Length == 0) { error = "An event needs a name."; return false; }

            // Duplicate + built-in-shadow checks first, so the author gets the short message rather than the
            // located registry form. A null slot in `existing` can never match a non-empty name; ValidateRegistry
            // below reports it as `custom_events[i] is null.`
            if (existing != null)
                for (int i = 0; i < existing.Count; i++)
                    if (existing[i]?.Name == name) { error = $"'{name}' is already declared."; return false; }

            // The AUTHORITATIVE graph event vocabulary (EventTypes ∪ custom_event) — not a hand-copied list, so a
            // built-in appended in NodeKinds is shadow-checked here on the same edit.
            if (NodeKinds.InSet(NodeKinds.GraphEventTypes, name))
            { error = $"'{name}' shadows a built-in event kind."; return false; }

            var paramList = new List<ScenarioEventParam>();
            string paramsText = (rawParams ?? string.Empty).Trim();
            if (paramsText.Length > 0)
            {
                foreach (string chunk in paramsText.Split(','))
                {
                    string[] halves = chunk.Split(':');
                    string pn = halves[0].Trim();
                    string pt = halves.Length > 1 ? halves[1].Trim() : "";
                    if (halves.Length != 2 || pn.Length == 0 || !NodeKinds.InSet(ParamTypeNames, pt))
                    {
                        error = $"'{chunk.Trim()}' is not a 'name:Type' pair (types: Int / Fixed / Bool).";
                        return false;
                    }
                    paramList.Add(new ScenarioEventParam { Name = pn, Type = Enum.Parse<DslValueType>(pt) });
                }
            }

            var raiserList = new List<int>();
            string raisersText = (rawRaisers ?? string.Empty).Trim();
            if (raisersText.Length > 0)
            {
                foreach (string chunk in raisersText.Split(','))
                {
                    if (!int.TryParse(chunk.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot))
                    {
                        error = $"'{chunk.Trim()}' is not a faction slot number.";
                        return false;
                    }
                    raiserList.Add(slot);
                }
            }

            var list = new List<ScenarioCustomEvent>(existing ?? Array.Empty<ScenarioCustomEvent>())
            {
                new ScenarioCustomEvent
                {
                    Name           = name,
                    Params         = paramList.Count  > 0 ? paramList.ToArray()  : null,
                    AllowedRaisers = raiserList.Count > 0 ? raiserList.ToArray() : null,
                },
            };

            // The engine's own closed-registry rules (caps, identifier params, raiser range/dups) at the SAME
            // ceiling the load-time gate uses.
            error = EventDispatchPlan.ValidateRegistry(list, MaxRaiserSlotExclusive);
            if (error != null) return false;

            candidate = list.ToArray();
            return true;
        }
    }
}
