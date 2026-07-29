#nullable enable
using System;
using Godot;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.UI.Components; // ChimeraToastHost

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.4 (FR-74) — the match-feedback coordinator. A READ-ONLY drainer of the presentation
    /// <see cref="CombatEventQueue"/>, modeled on <c>AudioManager</c>: it reads the queue each frame BEFORE
    /// <c>CombatFeedbackBridge</c>'s single <c>Clear()</c> (MainScene calls <see cref="Update"/> in its presentation
    /// tail) and NEVER clears it. It filters events to <c>EffectiveLocalFaction</c> and raises:
    ///   • under-attack alerts (Danger toast + minimap flash + SFX) — ONLY for a local unit/building hit OUTSIDE the
    ///     current viewport (<see cref="RtsCameraController.IsInView"/> == false), throttled per region/time window;
    ///   • guard-sourced denial cues (reason→text + SFX) for a rejected local order;
    ///   • production/research completion cues.
    ///
    /// Purely presentation — it touches no sim store, so it keeps the SimChecksum parity the whole story rests on.
    /// </summary>
    public partial class MatchAlertBridge : Node
    {
        private static readonly Color ALLY_PING_COLOR = new Color(0.35f, 0.9f, 0.55f);

        private CombatEventQueue?     _events;
        private MinimapBridge?        _minimap;
        private RtsCameraController?  _cam;
        private AudioManager?         _audio;
        private ChimeraToastHost?     _toasts;
        private AllianceStore?        _alliances;
        private Func<Faction>         _localFaction = () => Faction.Player1;

        private readonly UnderAttackThrottle _throttle = new();
        private double _nowSec;

        public void Initialize(CombatEventQueue events, MinimapBridge? minimap, RtsCameraController? cam,
                               AudioManager? audio, ChimeraToastHost? toasts, Func<Faction> localFaction,
                               AllianceStore? alliances)
        {
            _events       = events;
            _minimap      = minimap;
            _cam          = cam;
            _audio        = audio;
            _toasts       = toasts;
            _localFaction = localFaction;
            _alliances    = alliances;
        }

        /// <summary>
        /// Drain the queue read-only. MUST be called by MainScene BEFORE <c>CombatFeedbackBridge._Process</c> (the sole
        /// <c>Clear()</c> owner) each frame — exactly the AudioManager sibling posture. Never calls <c>Clear()</c>.
        /// </summary>
        public void Update(double delta)
        {
            if (_events == null) return;
            _nowSec += delta;

            Faction me = _localFaction();
            int count = _events.Count;
            for (int i = 0; i < count; i++)
            {
                CombatEvent e = _events.Get(i);
                if (e.Faction != me) continue; // local-only feedback (an enemy's identical event raises nothing)

                switch (e.Type)
                {
                    case CombatEventType.MeleeHit:
                    case CombatEventType.RangedHit:
                    case CombatEventType.SplashHit:
                    case CombatEventType.UnitKilled:
                    case CombatEventType.BuildingDestroyed:
                        HandleLocalHit(e);
                        break;

                    case CombatEventType.OrderDenied:
                        HandleDenial(e);
                        break;

                    case CombatEventType.TrainingComplete:
                        _audio?.PlayTrainingComplete();
                        break;

                    case CombatEventType.ResearchComplete:
                        _audio?.PlayResearchComplete();
                        break;
                }
            }
        }

        /// <summary>A local unit/building was hit. Alert ONLY when it is off-screen (the player cannot already see it)
        /// and the region/time throttle allows it, so a sustained raid is one alert stream, not spam.</summary>
        private void HandleLocalHit(CombatEvent e)
        {
            Vector3 pos = e.Position.ToGodotVector3();
            if (_cam != null && _cam.IsInView(pos)) return;                 // on-screen → no alert (normal hit feedback only)
            if (!_throttle.ShouldAlert(e.Position, _nowSec)) return;        // same region within the window → suppressed

            _toasts?.Show("Under attack!", "Your forces are taking damage.", ChimeraToastHost.ToastVariant.Danger, 6f);
            _minimap?.FlashAlert(e.Position.X.ToFloat(), e.Position.Z.ToFloat());
            _audio?.PlayUnderAttack();
        }

        private void HandleDenial(CombatEvent e)
        {
            string text = DenialReasonText.For(e.Reason);
            if (string.IsNullOrEmpty(text)) text = "Order denied";
            // P7: play the denial cue only when a NEW toast was shown — when it coalesced (rapid repeat denials on one
            // busy building), the sound is suppressed too, so repeat denials are ONE sound like they are one toast.
            bool shown = _toasts?.Show("Can't do that", text, ChimeraToastHost.ToastVariant.Warn, 2.5f) ?? true;
            if (shown) _audio?.PlayDenied();
        }

        /// <summary>Story 11.4: an ally's minimap ping arrived over the reliable side-channel (MP). P1: render it ONLY
        /// when the sender is on the local player's team (WC3 ally-only ping semantics) — a non-allied sender's ping is
        /// DROPPED (never shown/heard), so in 1v1 an enemy ping is not painted friendly. World XZ arrives as whole-unit
        /// ints (a display ping needs no sub-unit precision).</summary>
        public void HandleMpPing(Faction sender, int worldX, int worldZ)
        {
            Faction me = _localFaction();
            if (sender == me) return; // our own ping already showed locally at click time
            // Ally-only: if we have no alliance table (defensive), fall back to same-faction-only rather than showing
            // an enemy ping as friendly.
            bool allied = _alliances != null ? _alliances.AreAllied(sender, me) : (sender == me);
            if (!allied) return; // drop a non-allied (enemy) ping — never render or sound it
            _minimap?.AddPing(worldX, worldZ, ALLY_PING_COLOR);
            _audio?.PlayPing();
        }

        /// <summary>Clear the throttle memory (on a match reset) so the first hit of the next match alerts immediately.</summary>
        public void ResetForMatch() => _throttle.Clear();
    }
}
