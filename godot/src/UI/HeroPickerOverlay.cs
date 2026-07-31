#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using ProjectChimera.Core;                 // Faction
using ProjectChimera.Core.Definitions;      // ScenarioData, PlayerProfile, IProfileSource, HeroProfileLoader, OnlineHeroLaunchGate, AttestationOutcome, FactionDefinition, UnitDefinition
using ProjectChimera.Multiplayer;            // NakamaService (Story 9.12 online attest gate)
using ProjectChimera.UI.Components;          // ChimeraComponents, ChimeraListRow, ListRowGroup, ChimeraMark, ChimeraDialog, ChimeraToastHost, ChimeraTooltip
using ProjectChimera.UI.Theme;               // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;              // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 3.9 — the OFFLINE, player-facing hero picker (UX-DR75, AR-12 M2 / FR-7d / FR-7e). Shown at the offline
    /// "Play Skirmish" start ONLY when the loaded scenario's <see cref="PersistenceManifest.Enabled"/> is true (else the
    /// skirmish launches straight to Play). It lists the player's saved heroes as slot cards built purely from the 3.1
    /// kit (<see cref="ChimeraMark"/> portrait + level/XP <c>Readout</c> + <c>Progress(Xp)</c> + faction <c>Tag</c> +
    /// signature), single-select via a <see cref="ListRowGroup"/>, and offers Deploy / Save / Overwrite / Delete plus a
    /// non-blocking "Play without a saved hero".
    ///
    /// <para><b>Determinism boundary.</b> The picker only chooses WHICH profile to deploy; the actual mint into
    /// <see cref="HeroStore"/> as deterministic initial state (and the byte-identical <see cref="StartStateHash"/>) is
    /// done by <see cref="HeroProfileLoader.LoadInto"/> through the phase's launch callback. Deploy stashes the chosen
    /// profile and launches; Overwrite/Delete are <see cref="ChimeraDialog"/>-confirmed (danger). Every new control gets
    /// <see cref="AttachFieldTip"/> (UX-DR53, hover + keyboard focus). A live GLB portrait is NOT required this story —
    /// the asset-free <see cref="ChimeraMark"/> sigil is the placeholder.</para>
    /// </summary>
    public partial class HeroPickerOverlay : Node
    {
        // ── Layout constants (component-intrinsic; spacing/color TOKENS come from the theme) ──
        private const float PANEL_W = 640f;
        private const float PANEL_H = 560f;
        private const int   PORTRAIT = 48;

        // ── Kit context ──
        private GodotTheme        _theme  = null!;

        // ── Deps (wired by HeroPickerPhase / LobbyUi after AddChild) ──
        private ScenarioData?          _scenario;
        private IProfileSource?        _source;
        private FactionDefinition?[]   _slotFactionDefs = System.Array.Empty<FactionDefinition?>();
        private Action<PlayerProfile?> _launch = _ => { };

        /// <summary>Story 9.12: when set (online mode via <see cref="EnableOnlineAttestation"/>), a Deploy is gated on a
        /// successful SERVER attestation of the selected profile (<see cref="NakamaService.AttestHeroProfileAsync"/> →
        /// <see cref="OnlineHeroLaunchGate"/>); an unattested/invalid profile — or a failed/errored attestation call
        /// (fail-closed) — keeps the player in the picker with a surfaced reason and never reaches the launch callback.
        /// Null (the default) = offline: the launch path is unchanged.</summary>
        private NakamaService? _online;

        /// <summary>Story 9.12: the online source (same object as <see cref="_source"/> when online) — used for its ASYNC
        /// load/save so the online picker never blocks the Godot main thread on a Nakama round-trip (P2). Null offline.</summary>
        private OnlineProfileSource? _onlineSource;

        /// <summary>Story 9.12 (P3): true while a server attestation await is in flight — the footer is disabled and a
        /// late continuation must not act on a dismissed overlay.</summary>
        private bool _serverOpInFlight;

        /// <summary>Story 9.12: when set (online mode), a profile is deployable only when the placed hero it matches is
        /// owned by THIS faction (the server-assigned <c>_assignedFaction</c>'s <see cref="FactionDefinition"/>). Null
        /// (offline / faction not yet assigned) leaves the compatibility check slot-agnostic (unchanged behaviour).</summary>
        private FactionDefinition? _localFactionFilter;

        /// <summary>Story 3.13 (D6) / Story 3.16 / DW-27 / DW-32: supplies the end-of-match hero harvest (live Level/Xp +
        /// carried loadout) captured on return-to-Edit — so Save/Overwrite persist the REAL grown values + loadout instead
        /// of the authored placeholders. Wired by <c>HeroPickerPhase</c> as a live-read closure over <c>SceneContext.Harvest</c>;
        /// null / <c>None</c> ⇒ the authored fallbacks. The has-vs-fallback rule lives in <see cref="HeroHarvestResolver"/>.</summary>
        public System.Func<HeroHarvestResolver.HeroHarvest>? HarvestProvider;

        // ── Nodes ──
        private CanvasLayer     _canvas   = null!;
        private ColorRect       _scrim    = null!;
        private PanelContainer  _panel    = null!;
        private VBoxContainer   _listHost = null!;   // the slot-card list
        private Label           _statusLabel = null!;
        private Godot.Button    _deployBtn    = null!;
        private Godot.Button    _saveBtn      = null!;
        private Godot.Button    _overwriteBtn = null!;
        private Godot.Button    _deleteBtn    = null!;
        private Godot.Button    _playWithoutBtn = null!;   // Story 9.12 (P3): disabled during an attest await
        private ChimeraToastHost _toastHost   = null!;

        private readonly ListRowGroup _group = new();
        private PlayerProfile? _selected;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the overlay to the live scenario + the offline disk rail + per-slot faction defs (for card
        /// metadata) + the phase's launch callback. Called by <c>HeroPickerPhase</c> after <c>AddChild</c>. Hidden by
        /// default; shown by <see cref="RequestSkirmishLaunch"/>.</summary>
        public void Initialize(ScenarioData? scenario, IProfileSource source,
                               FactionDefinition?[] slotFactionDefs, Action<PlayerProfile?> launch)
        {
            _scenario        = scenario;
            _source          = source;
            _onlineSource    = source as OnlineProfileSource; // non-null online → drives the async (non-blocking) load/save
            _slotFactionDefs = slotFactionDefs ?? System.Array.Empty<FactionDefinition?>();
            _launch          = launch ?? (_ => { });
        }

        /// <summary>Story 9.12: put the picker in ONLINE mode — a Deploy then requires a successful server attestation
        /// (via <paramref name="nakama"/>) before the launch callback fires. Offline callers never call this, so their
        /// path is unchanged.</summary>
        public void EnableOnlineAttestation(NakamaService nakama) => _online = nakama;

        /// <summary>Story 9.12 (online): restrict deployable heroes to those placed for <paramref name="factionDef"/>
        /// (the local player's server-assigned faction). Null = slot-agnostic (offline / faction not yet assigned).
        /// Re-callable as the server assigns/updates the local faction; refreshes the card list if visible.</summary>
        public void SetLocalFactionFilter(FactionDefinition? factionDef)
        {
            _localFactionFilter = factionDef;
            if (_canvas != null && _canvas.Visible) Refresh();
        }

        /// <summary>Story 9.12: show the picker for the ONLINE lobby flow (attestation-gated). Unlike the offline
        /// <see cref="RequestSkirmishLaunch"/> it ALWAYS shows — the online rail requires a picked-and-attested hero
        /// before the player may Ready.</summary>
        public void ShowForOnline() => Show();

        /// <summary>
        /// The offline Play-Skirmish launch authority (D-4). When the loaded scenario's persistence manifest is Enabled,
        /// show the picker; otherwise launch straight to Play exactly as today (minting nothing). Single authority — the
        /// caller (<c>MainMenuPhase.OnPlaySkirmish</c>) never toggles Play itself.
        /// </summary>
        public void RequestSkirmishLaunch()
        {
            if (_scenario?.PersistenceManifest is { Enabled: true })
                Show();
            else
                _launch(null); // persistence not configured / disabled → straight to Play (nothing minted)
        }

        private void Show()
        {
            _selected = null;
            Refresh();
            _canvas.Visible = true;
        }

        private void Hide() => _canvas.Visible = false;


        // ── UI construction ────────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 20 }; // mirrors MainMenuOverlay (topmost) — NOT the Edit-mode idiom
            AddChild(_canvas);

            // Scrim eats input so clicks don't fall through to the game while the picker is open.
            _scrim = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
            _scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _canvas.AddChild(_scrim);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _canvas.AddChild(center);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Theme = _theme;   // _panel is a Control (this Node has no Theme) — propagates to the subtree
            center.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // ── Title ──
            var title = ChimeraComponents.Heading("Choose Your Hero", ThemeTokens.Tlg);
            root.AddChild(title);

            var subtitle = ChimeraComponents.Body("Deploy a saved hero as your starting state, save the current hero, or play without one.",
                                ThemeTokens.TextMid);
            subtitle.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            subtitle.AutowrapMode = TextServer.AutowrapMode.Word;
            root.AddChild(subtitle);

            // ── Slot-card list ──
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            _listHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _listHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            scroll.AddChild(_listHost);

            _statusLabel = ChimeraComponents.Body("", ThemeTokens.TextLo);
            _statusLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            // ── Action footer ──
            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(footer);

            _deployBtn = ChimeraComponents.Button("Deploy", ChimeraComponents.ButtonVariant.Primary);
            _deployBtn.Pressed += OnDeployPressed;
            AttachFieldTip(_deployBtn, "Deploy",
                "Start the match with the selected hero minted as deterministic starting state (its level and XP).");
            footer.AddChild(_deployBtn);

            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Secondary);
            _saveBtn.Pressed += OnSavePressed;
            AttachFieldTip(_saveBtn, "Save",
                "Save this scenario's hero into a new slot (captures the manifest's selected attributes — level, XP).");
            footer.AddChild(_saveBtn);

            _overwriteBtn = ChimeraComponents.Button("Overwrite", ChimeraComponents.ButtonVariant.Secondary);
            _overwriteBtn.Pressed += OnOverwritePressed;
            AttachFieldTip(_overwriteBtn, "Overwrite",
                "Replace the selected saved hero with the current hero's attributes. Requires confirmation.");
            footer.AddChild(_overwriteBtn);

            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger);
            _deleteBtn.Pressed += OnDeletePressed;
            AttachFieldTip(_deleteBtn, "Delete",
                "Permanently delete the selected saved hero from disk. Requires confirmation.");
            footer.AddChild(_deleteBtn);

            // Spacer pushes the non-blocking "play without a hero" to the right.
            footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            _playWithoutBtn = ChimeraComponents.Button("Play without a saved hero", ChimeraComponents.ButtonVariant.Ghost);
            _playWithoutBtn.Pressed += OnPlayWithoutPressed;
            AttachFieldTip(_playWithoutBtn, "Play without a saved hero",
                "Start the match now with no saved hero — nothing is minted. Always available (offline).");
            footer.AddChild(_playWithoutBtn);

            // ── Toast host for "Saved" feedback ──
            _toastHost = ChimeraToastHost.Create();
            AddChild(_toastHost);

            _canvas.Visible = false;   // hidden until RequestSkirmishLaunch shows it
        }

        // ── Reflect saved profiles into slot cards ──────────────────────────────────

        private void Refresh()
        {
            // Story 9.12 (P2): online, the profile list comes from Nakama — load it ASYNCHRONOUSLY and marshal the result
            // back to the main thread, so the picker never blocks the Godot main thread on a network round-trip. Offline
            // is a synchronous disk read (unchanged).
            if (_online != null) { RefreshOnlineAsync(); return; }

            ClearCards();
            PopulateCards(_source?.LoadAll() ?? System.Array.Empty<PlayerProfile>());
        }

        /// <summary>Story 9.12 (P2): clear the card list + show a "loading" status, then prefetch the single server-owned
        /// profile off-thread and marshal the populate back to the main thread. Never blocks.</summary>
        private void RefreshOnlineAsync()
        {
            ClearCards();
            _statusLabel.Visible = true;
            _statusLabel.Text = "Loading your hero from the server…";
            UpdateButtonStates();
            _ = PrefetchThenPopulateAsync();
        }

        private async System.Threading.Tasks.Task PrefetchThenPopulateAsync()
        {
            try { if (_onlineSource != null) await _onlineSource.PrefetchAsync(); }
            catch { /* fail-soft: an unreachable server yields the empty (no-hero) list, never a throw */ }

            // Marshal back to the Godot main thread — no off-thread scene mutation from the awaited continuation.
            Callable.From(() =>
            {
                if (!IsInstanceValid(this) || _canvas == null || !_canvas.Visible) return; // picker dismissed mid-load
                PopulateCards(_onlineSource?.LoadAll() ?? System.Array.Empty<PlayerProfile>());
            }).CallDeferred();
        }

        /// <summary>Remove all card rows and reset the authoritative selection.</summary>
        private void ClearCards()
        {
            foreach (Node c in _listHost.GetChildren()) { _listHost.RemoveChild(c); c.QueueFree(); }
            // The group's stale reference to a now-freed row is guarded by IsInstanceValid on the next Select — safe to
            // leave; the authoritative selection is _selected, reset here.
            _selected = null;
        }

        /// <summary>Build the slot cards for <paramref name="profiles"/> (0 or 1 online; N offline) and refresh buttons.</summary>
        private void PopulateCards(IReadOnlyList<PlayerProfile> profiles)
        {
            ClearCards();
            if (profiles.Count == 0)
            {
                _statusLabel.Visible = true;
                _statusLabel.Text = _online != null
                    ? "No hero saved on the server yet. Save this scenario's hero to play online."
                    : "No saved heroes yet. Save this scenario's hero, or play without one.";
            }
            else
            {
                _statusLabel.Visible = false;
                foreach (PlayerProfile p in profiles)
                    _listHost.AddChild(BuildCard(p, IsCompatible(p)));
            }

            UpdateButtonStates();
        }

        /// <summary>A profile is compatible (selectable) when the scenario PLACES a hero unit whose id matches the
        /// profile's <see cref="PlayerProfile.HeroDefId"/> (D-3). Apply is the authoritative gate (it mints only for
        /// spawned <see cref="UnitDefinition.IsHero"/> entities); the card grey is the softer up-front signal.</summary>
        private bool IsCompatible(PlayerProfile profile)
        {
            if (_scenario == null) return false;
            foreach (ScenarioUnit u in _scenario.Units)
            {
                if (u.UnitId != profile.HeroDefId) continue;
                // Resolve the placed unit to its faction def and require IsHero — the SAME gate the authoritative mint
                // uses (LoadInto mints only IsHero placed units). This keeps the card's deployable-greying honest: a
                // non-hero unit sharing the id no longer shows deployable then mints nothing (D-3).
                int fIdx = u.Slot + 1; // slot 0 → Player1
                if (fIdx < 0 || fIdx >= _slotFactionDefs.Length) continue;
                FactionDefinition? fdef = _slotFactionDefs[fIdx];
                // Story 9.12 (online): only heroes placed for the local player's assigned faction are deployable — a
                // profile never lands on another slot's same-id hero. Null filter (offline / unassigned) is slot-agnostic.
                if (_localFactionFilter != null && fdef?.Id != _localFactionFilter.Id) continue;
                UnitDefinition? def = fdef?.GetUnit(u.UnitId);
                if (def != null && def.IsHero) return true;
            }
            return false;
        }

        private ChimeraListRow BuildCard(PlayerProfile profile, bool compatible)
        {
            ChimeraListRow row = ChimeraListRow.Create("", _group);
            // Drop Create's placeholder label so the card composes cleanly.
            foreach (Node c in row.Content.GetChildren()) { row.Content.RemoveChild(c); c.QueueFree(); }
            row.Content.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));

            // Portrait (procedural sigil placeholder — no GLB required this story).
            var mark = ChimeraMark.Create(PORTRAIT);
            mark.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.Content.AddChild(mark);

            // Name + signature column.
            var nameCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            nameCol.AddThemeConstantOverride("separation", 2);
            var nameLbl = ChimeraComponents.Heading(string.IsNullOrEmpty(profile.DisplayName) ? profile.HeroDefId : profile.DisplayName, ThemeTokens.Tmd);
            nameCol.AddChild(nameLbl);
            string sig = string.IsNullOrEmpty(profile.SignatureAbility) ? "No signature ability" : $"Signature: {profile.SignatureAbility}";
            var sigLbl = ChimeraComponents.Body(sig, ThemeTokens.TextLo);
            sigLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            nameCol.AddChild(sigLbl);
            row.Content.AddChild(nameCol);

            // Level readout.
            Color accent = _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type);
            var lvl = ChimeraComponents.Readout(accent, profile.Level.ToString(), "LVL");
            lvl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.Content.AddChild(lvl);

            // XP readout + decorative XP bar.
            var xpCol = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            xpCol.AddThemeConstantOverride("separation", 2);
            xpCol.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.AccentDim, ThemeTokens.Type), profile.Xp.ToInt().ToString(), "XP"));
            var bar = ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Xp, XpBarValue(profile));
            bar.CustomMinimumSize = new Vector2(96, ChimeraComponents.Const(ThemeTokens.S2) + 6);
            xpCol.AddChild(bar);
            row.Content.AddChild(xpCol);

            // Story 3.16: saved inventory (item-def id + charges) — a compact count tag with the item list in a tooltip.
            if (profile.Inventory != null && profile.Inventory.Count > 0)
            {
                var invTag = ChimeraComponents.Tag($"⛃ {profile.Inventory.Count}", ChimeraComponents.TagVariant.Accent);
                invTag.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                var sb = new System.Text.StringBuilder();
                for (int ii = 0; ii < profile.Inventory.Count; ii++)
                {
                    if (ii > 0) sb.Append(", ");
                    var pit = profile.Inventory[ii];
                    sb.Append(pit.ItemId);
                    if (pit.Charges > 0) sb.Append(" (").Append(pit.Charges).Append(')');
                }
                AttachFieldTip(invTag, "Saved inventory", sb.ToString());
                row.Content.AddChild(invTag);
            }

            // Faction tag (card metadata — display only).
            if (!string.IsNullOrEmpty(profile.FactionId))
            {
                var tag = ChimeraComponents.Tag(profile.FactionId, ChimeraComponents.TagVariant.Neutral);
                tag.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                row.Content.AddChild(tag);
            }

            if (!compatible)
            {
                // Greyed / non-deployable: the scenario places no hero with this id.
                row.SetLocked(true);
                AttachFieldTip(row, profile.DisplayName,
                    "This scenario does not place this hero, so it cannot be deployed here.");
            }
            else
            {
                PlayerProfile captured = profile;
                row.Selected += () => OnRowSelected(captured);
                AttachFieldTip(row, profile.DisplayName,
                    $"Level {profile.Level} • {profile.Xp.ToInt()} XP. Select, then Deploy to start with this hero.");
            }
            return row;
        }

        // A decorative XP-bar fill (the real per-level leveling curve is Story 3.13). Presentation-only. MONOTONIC in
        // XP and saturating toward 100 — so it never resets and can never contradict the numeric XP readout beside it
        // (a higher XP never shows a shorter bar), unlike a raw `xp % 100` which emptied at every round hundred.
        private static double XpBarValue(PlayerProfile profile)
        {
            double xp = Math.Max(0, profile.Xp.ToInt());
            return 100.0 * xp / (xp + 100.0); // 0→0, strictly increasing, asymptotic to 100
        }

        private void OnRowSelected(PlayerProfile profile)
        {
            _selected = profile; // ChimeraListRow emits Selected only on selection (never deselection)
            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            // Story 9.12 (P3): while a server attestation is in flight the whole footer is frozen — no button may fire a
            // second launch/save or dismiss the overlay under the awaited continuation.
            if (_serverOpInFlight) { SetFooterEnabled(false); return; }

            bool hasSelection = _selected != null;
            _deployBtn.Disabled    = !hasSelection;
            _overwriteBtn.Disabled = !hasSelection;
            // Story 9.12 (P5): online heroes are server-owned (No-Client-Write, no delete RPC in this slice) — a client
            // cannot delete them, so the Delete affordance is disabled online rather than showing a false success toast.
            _deleteBtn.Disabled    = !hasSelection || _online != null;
            _deleteBtn.TooltipText = _online != null ? "Online heroes are server-owned and can't be deleted in this build." : "";
            // Save only needs a placeable hero in the scenario (no selection required).
            _saveBtn.Disabled = !TryResolvePrimaryHero(out _, out _, out _, out _);
            // Story 9.12: "Play without a saved hero" is offline-only. Online it satisfies no attestation gate (the lobby
            // ignores a null profile) yet would still Hide() the picker, silently leaving the player un-readied with the
            // picker dismissed — a confusing dead-end contradicting "keep the player until an attested profile". Disable
            // it online so the only online exits are an attested Deploy or Cancel (its tooltip already says "(offline)").
            _playWithoutBtn.Disabled = _online != null;
        }

        /// <summary>Story 9.12 (P3): enable/disable every footer button as a unit for the duration of an async server
        /// round-trip (attest / online save), so no stale click drives Ready/launch on a dismissed overlay.</summary>
        private void SetFooterEnabled(bool enabled)
        {
            _deployBtn.Disabled      = !enabled;
            _saveBtn.Disabled        = !enabled;
            _overwriteBtn.Disabled   = !enabled;
            _deleteBtn.Disabled      = !enabled;
            _playWithoutBtn.Disabled = !enabled;
        }

        // ── Actions ─────────────────────────────────────────────────────────────────

        /// <summary>Story 9.12: "Play without a saved hero" — offline launches straight to Play (mints nothing). Online
        /// this does NOT satisfy the attestation gate (the lobby's launch callback ignores a null profile), so the player
        /// stays un-readied; disabled entirely during an attest await (P3).</summary>
        private void OnPlayWithoutPressed()
        {
            if (_serverOpInFlight) return;
            Hide();
            _launch(null);
        }

        private void OnDeployPressed()
        {
            if (_selected == null || _serverOpInFlight) return;
            PlayerProfile chosen = _selected;

            // Story 9.12: online mode gates the launch on a successful SERVER attestation. Offline (the default) launches
            // straight away, exactly as before.
            if (_online != null) { _ = DeployWithAttestationAsync(chosen); return; }

            Hide();
            _launch(chosen); // stash-and-launch: the phase sets PendingHeroProfile + mints before the hash
        }

        /// <summary>Story 9.12: attest the chosen profile with the server, then gate the launch on
        /// <see cref="OnlineHeroLaunchGate.CanEnterMatch"/>. On any failure (invalid/unattested OR a failed/errored/timed-out
        /// attestation call — FAIL-CLOSED) the player stays in the picker with the reason surfaced; only an
        /// attested-and-valid profile reaches the launch callback, so a tampered/unattested profile can never enter online
        /// play. <b>All post-<c>await</c> scene/UI mutation is marshaled to the main thread</b> via
        /// <see cref="Callable"/>.<c>CallDeferred</c> — the Nakama continuation runs off the Godot main thread.</summary>
        private async Task DeployWithAttestationAsync(PlayerProfile chosen)
        {
            // P10: a blank ProfileId can never bind server-side — treat as fail-closed rather than attesting "whatever
            // is stored".
            if (string.IsNullOrEmpty(chosen.ProfileId))
            {
                ShowStatus("Cannot enter match — the selected hero has no id. Pick a saved hero.");
                return;
            }

            // Pre-await mutations run on the main thread (this is a UI signal handler). P3: freeze the WHOLE footer so no
            // sibling button (Play-without / Save / Delete) can fire under the awaited continuation.
            _serverOpInFlight = true;
            SetFooterEnabled(false);
            ShowStatus("Attesting hero with the server…");

            AttestationOutcome outcome;
            try { outcome = await _online!.AttestHeroProfileAsync(chosen.ProfileId); }
            catch { outcome = AttestationOutcome.CallFailed; } // defensive: the async method is already guarded

            // Marshal the result-handling (which touches Godot nodes) back onto the main thread — no off-thread scene
            // mutation from the awaited continuation.
            Callable.From(() => FinishOnlineDeploy(outcome, chosen)).CallDeferred();
        }

        /// <summary>Story 9.12: main-thread continuation of <see cref="DeployWithAttestationAsync"/> — refuses the launch
        /// fail-closed on a failed gate (keeping the player in the picker with a reason), else launches. P3: bails if the
        /// overlay was dismissed while the attest was in flight, so a late attestation never launches a torn-down flow.</summary>
        private void FinishOnlineDeploy(AttestationOutcome outcome, PlayerProfile chosen)
        {
            _serverOpInFlight = false;

            // P3: the overlay was dismissed (cancel / disconnect / play-without freed the canvas) while attesting — do
            // NOT launch. The lobby's own online-mode guard is the second line of defence.
            if (!IsInstanceValid(this) || _canvas == null || !_canvas.Visible)
                return;

            if (!OnlineHeroLaunchGate.CanEnterMatch(outcome))
            {
                string why = !outcome.CallSucceeded
                    ? "the server could not be reached or returned an unexpected reply — try again"
                    : outcome.Reason == ProfileInvalidReason.None
                        ? "no attested hero on the server"
                        : $"the server rejected it ({outcome.Reason})";
                ShowStatus($"Cannot enter match — {why}. Save a valid hero, then pick an attested one.");
                SetFooterEnabled(true);
                UpdateButtonStates();
                return;
            }

            Hide();
            _launch(chosen);
        }

        /// <summary>Story 3.13 (D6): the harvested end-of-match Level/Xp for <paramref name="heroDefId"/> if the last
        /// playtest grew that hero, else the supplied fallback (authored base for a fresh Save, or the profile's own
        /// values for Overwrite). Takes the caller's already-captured <paramref name="h"/> so Level/Xp and inventory are
        /// resolved against the SAME harvest read (one provider invocation per Save/Overwrite, no read-skew).</summary>
        private static (int Level, Fixed Xp) ResolveHeroProgress(in HeroHarvestResolver.HeroHarvest h, string heroDefId,
                                                                 int fallbackLevel, Fixed fallbackXp)
            => HeroHarvestResolver.ResolveProgress(in h, heroDefId, fallbackLevel, fallbackXp);

        private void OnSavePressed()
        {
            if (_source == null || _scenario?.PersistenceManifest == null) return;
            if (!TryResolvePrimaryHero(out string unitId, out string display, out string? sig, out string factionId))
            {
                ShowStatus("This scenario places no hero to save.");
                return;
            }

            // Story 3.13 (D6): source the harvested end-of-match Level/Xp for this hero (if the last playtest grew it),
            // else the authored base (level 1, 0 XP). Routed through the manifest shape → BuildProfile.
            HeroHarvestResolver.HeroHarvest h = HarvestProvider?.Invoke() ?? HeroHarvestResolver.HeroHarvest.None;
            (int level, Fixed xp) = ResolveHeroProgress(in h, unitId, fallbackLevel: 1, fallbackXp: Fixed.Zero);
            string profileId = _source.NextProfileId(unitId);
            PlayerProfile profile = HeroProfileLoader.BuildProfile(
                profileId, unitId, factionId, display, sig, level, xp,
                _scenario.PersistenceManifest.DeriveProfileShape(),
                HeroHarvestResolver.ResolveInventory(in h, unitId)); // Story 3.16: capture the live loadout when the shape carries hero.inventory

            // Story 9.12 (P2): online, Save routes through the ASYNC validating server RPC (no main-thread block). Offline
            // is the synchronous disk write (unchanged).
            if (_online != null) { _ = SaveOnlineAsync(profile, display); return; }

            _source.Save(profile);
            _toastHost.Show("Saved", $"{display} saved as a new hero slot.", ChimeraToastHost.ToastVariant.Ok);
            Refresh();
        }

        /// <summary>Story 9.12 (P2): persist a profile online through the async validating RPC, freezing the footer for the
        /// round-trip and marshaling the result back to the main thread (mirrors the attest path). Never blocks.</summary>
        private async Task SaveOnlineAsync(PlayerProfile profile, string display)
        {
            _serverOpInFlight = true;
            SetFooterEnabled(false);
            ShowStatus("Saving your hero to the server…");

            StorageWriteResult result;
            try { result = _onlineSource != null ? await _onlineSource.SaveAsync(profile) : StorageWriteResult.Failed("no_source"); }
            catch (Exception e) { result = StorageWriteResult.Failed(e.Message); }

            Callable.From(() => FinishOnlineSave(result, display)).CallDeferred();
        }

        private void FinishOnlineSave(StorageWriteResult result, string display)
        {
            _serverOpInFlight = false;
            if (!IsInstanceValid(this) || _canvas == null || !_canvas.Visible) return; // picker dismissed mid-save

            if (!result.Ok)
            {
                ShowStatus($"Could not save hero to the server: {result.Reason ?? "unknown"}.");
                SetFooterEnabled(true);
                UpdateButtonStates();
                return;
            }
            _toastHost.Show("Saved", $"{display} saved to the server.", ChimeraToastHost.ToastVariant.Ok);
            SetFooterEnabled(true);
            Refresh(); // re-loads the server object (async) → the saved hero appears
        }

        private void OnOverwritePressed()
        {
            if (_source == null || _selected == null || _scenario?.PersistenceManifest == null) return;
            PlayerProfile target = _selected;

            var dialog = ChimeraDialog.Create("Overwrite saved hero?",
                $"Replace \"{(string.IsNullOrEmpty(target.DisplayName) ? target.HeroDefId : target.DisplayName)}\" " +
                "with the current hero's attributes? This cannot be undone.");
            dialog.AddConfirm("Overwrite", danger: true);
            dialog.AddCancel("Cancel");
            dialog.Confirmed += () =>
            {
                // Story 3.13 (D6): rebuild the SAME profile id, capturing the harvested end-of-match Level/Xp for this
                // hero (if the last playtest grew it), else the profile's own values — through the manifest shape.
                HeroHarvestResolver.HeroHarvest h = HarvestProvider?.Invoke() ?? HeroHarvestResolver.HeroHarvest.None;
                (int level, Fixed xp) = ResolveHeroProgress(in h, target.HeroDefId, fallbackLevel: target.Level, fallbackXp: target.Xp);
                // Story 3.16 review: the resolver returns null when the harvested hero != this overwrite target — mirror the
                // level/xp fallback and keep the target's PREVIOUSLY-SAVED loadout, rather than wiping it to an empty list.
                IReadOnlyList<ProfileInventoryItem>? inventory =
                    HeroHarvestResolver.ResolveInventory(in h, target.HeroDefId) ?? target.Inventory;
                PlayerProfile rebuilt = HeroProfileLoader.BuildProfile(
                    target.ProfileId, target.HeroDefId, target.FactionId, target.DisplayName, target.SignatureAbility,
                    level, xp, _scenario.PersistenceManifest.DeriveProfileShape(),
                    inventory); // Story 3.16
                // Story 9.12 (P2): online overwrite routes through the async server RPC (no main-thread block).
                if (_online != null)
                {
                    string label = string.IsNullOrEmpty(target.DisplayName) ? target.HeroDefId : target.DisplayName;
                    _ = SaveOnlineAsync(rebuilt, label);
                    return;
                }
                _source.Save(rebuilt);
                _toastHost.Show("Saved", "Saved hero overwritten.", ChimeraToastHost.ToastVariant.Ok);
                Refresh();
            };
            dialog.Open(this);
        }

        private void OnDeletePressed()
        {
            if (_source == null || _selected == null) return;
            // Story 9.12 (P5): online heroes are server-owned (No-Client-Write, no delete RPC in this slice) — never a
            // false "deleted" toast. The button is already disabled online; guard defensively with an honest message.
            if (_online != null)
            {
                ShowStatus("Online heroes are server-owned and can't be deleted in this build.");
                return;
            }
            PlayerProfile target = _selected;

            var dialog = ChimeraDialog.Create("Delete saved hero?",
                $"Permanently delete \"{(string.IsNullOrEmpty(target.DisplayName) ? target.HeroDefId : target.DisplayName)}\"? " +
                "This cannot be undone.");
            dialog.AddConfirm("Delete", danger: true);
            dialog.AddCancel("Cancel");
            dialog.Confirmed += () =>
            {
                _source.Delete(target.ProfileId);
                _toastHost.Show("Deleted", "Saved hero removed.", ChimeraToastHost.ToastVariant.Warn);
                Refresh();
            };
            dialog.Open(this);
        }

        /// <summary>Resolve the scenario's first PLACED hero unit (for the Save metadata): its unit id, display name,
        /// signature ability, and owning faction id. Returns false when the scenario places no <see cref="UnitDefinition.IsHero"/>
        /// unit.</summary>
        private bool TryResolvePrimaryHero(out string unitId, out string display, out string? signature, out string factionId)
        {
            unitId = ""; display = ""; signature = null; factionId = "";
            if (_scenario == null) return false;

            foreach (ScenarioUnit u in _scenario.Units)
            {
                int fIdx = u.Slot + 1; // slot 0 → Player1
                if (fIdx < 0 || fIdx >= _slotFactionDefs.Length) continue;
                FactionDefinition? fdef = _slotFactionDefs[fIdx];
                UnitDefinition? def = fdef?.GetUnit(u.UnitId);
                if (def == null || !def.IsHero) continue;

                unitId    = def.Id;
                display   = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                signature = def.Hero?.SignatureAbility;
                factionId = fdef?.Id ?? "";
                return true;
            }
            return false;
        }

        private void ShowStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
        }


        /// <summary>Attach a hover-AND-keyboard-focus tooltip (UX-DR53 / NFR-2). The keyboard half needs
        /// <c>FocusMode.All</c>; descendants are made mouse-transparent so the composite is the unambiguous hover target.</summary>
        private void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            MakeChildrenMouseIgnore(target);
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        private static void MakeChildrenMouseIgnore(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
                MakeChildrenMouseIgnore(child);
            }
        }
    }
}
