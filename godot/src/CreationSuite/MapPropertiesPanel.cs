#nullable enable
using Godot;
using System;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI.Components;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 6.7 — the New-Map flow + editable Map-Properties surface, built from the design-system components
    /// (<see cref="ChimeraComponents"/> Input/Select/NumInput + the <see cref="ChimeraDialog"/> scrim/
    /// focus-trap modal). Two entry points:
    ///   • <see cref="OpenNewMapDialog"/> — a modal collecting name/author/description/suggested-players/size, which
    ///     produces a blank map via the Godot-free <see cref="ScenarioData.CreateBlank"/> factory.
    ///   • <see cref="BuildPropertiesEditor"/> — a field stack bound live to an existing <see cref="ScenarioData"/>
    ///     so the same properties are editable after creation and persist on save.
    /// Presentation-only.
    /// </summary>
    public static class MapPropertiesPanel
    {
        // The suggested-player options offered by the pickers (2–4; engine ceiling Faction.Player4).
        private static readonly string[] PlayerOptions = { "2", "3", "4" };

        // Story 6.7 (review pass 2) — input length caps. The pre-6.7 WinConditionPhase LineEdits capped Name at 64 and
        // Author at 40; the design-system Input controls set no MaxLength, so the caps were lost when this panel took
        // over authoring (unbounded strings would flow into scenario.json, the manifest, and the export slug/filename).
        // Restored here; Description (new in 6.7) gets a roomier cap befitting a "short description".
        private const int NameMaxLength        = 64;
        private const int AuthorMaxLength      = 40;
        private const int DescriptionMaxLength = 240;

        /// <summary>
        /// Open the New-Map modal under <paramref name="parent"/>. On confirm, builds a valid blank
        /// <see cref="ScenarioData"/> (flat terrain, chosen size, 2–4 spread start slots) and hands it to
        /// <paramref name="onCreate"/>. Cancel/Esc/scrim-click dismiss without creating anything.
        /// </summary>
        public static void OpenNewMapDialog(Node parent, Action<ScenarioData> onCreate)
        {
            var form = new VBoxContainer();
            form.AddThemeConstantOverride("separation", 8);
            form.CustomMinimumSize = new Vector2(360f, 0f);

            form.AddChild(ChimeraComponents.FieldLabel("Map Name"));
            var nameField = ChimeraComponents.Input("My New Map", "My New Map");
            nameField.MaxLength = NameMaxLength; // Story 6.7 (review pass 2) — restore the cap the old LineEdit had.
            form.AddChild(nameField);

            form.AddChild(ChimeraComponents.FieldLabel("Author"));
            var authorField = ChimeraComponents.Input("Author", "");
            authorField.MaxLength = AuthorMaxLength;
            form.AddChild(authorField);

            form.AddChild(ChimeraComponents.FieldLabel("Description"));
            var descField = ChimeraComponents.Input("Short description", "");
            descField.MaxLength = DescriptionMaxLength;
            form.AddChild(descField);

            form.AddChild(ChimeraComponents.FieldLabel("Suggested Players"));
            var playersSelect = ChimeraComponents.Select(PlayerOptions);
            playersSelect.Selected = 1; // default 3 players
            form.AddChild(playersSelect);

            form.AddChild(ChimeraComponents.FieldLabel("Map Size"));
            var sizeSelect = ChimeraComponents.Select(SizeLabels());
            sizeSelect.Selected = IndexOfSize(MapSize.Medium);
            form.AddChild(sizeSelect);

            var dlg = ChimeraDialog.CreateCustom("New Map", form);
            dlg.AddCancel("Cancel");
            dlg.AddConfirm("Create");
            dlg.Confirmed += () =>
            {
                int players = Mathf.Max(0, playersSelect.Selected) + 2; // 0→2, 1→3, 2→4 (Selected can be -1)
                MapSize size = MapSizes.All[Mathf.Clamp(sizeSelect.Selected, 0, MapSizes.All.Length - 1)];
                var scenario = ScenarioData.CreateBlank(
                    nameField.Text.Trim(), authorField.Text.Trim(), descField.Text.Trim(), players, size);
                onCreate(scenario);
            };
            dlg.Open(parent);
        }

        /// <summary>
        /// Build a field stack that edits <paramref name="scenario"/>'s authoring properties in place: DisplayName,
        /// Author, Description, SuggestedPlayers, and MapBounds (via the <see cref="MapSize"/> picker). Every field
        /// writes straight back onto the live model so the values persist on the next Save. Returns a Control the
        /// caller docks into the editor.
        /// </summary>
        public static Control BuildPropertiesEditor(ScenarioData scenario)
        {
            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 6);

            box.AddChild(ChimeraComponents.FieldLabel("Map Name"));
            var nameField = ChimeraComponents.Input("Map name", scenario.DisplayName);
            nameField.MaxLength = NameMaxLength; // Story 6.7 (review pass 2) — restore the cap the old LineEdit had.
            nameField.TextChanged += t => scenario.DisplayName = t;
            box.AddChild(nameField);

            box.AddChild(ChimeraComponents.FieldLabel("Author"));
            var authorField = ChimeraComponents.Input("Author", scenario.Author ?? "");
            authorField.MaxLength = AuthorMaxLength;
            authorField.TextChanged += t => scenario.Author = t;
            box.AddChild(authorField);

            box.AddChild(ChimeraComponents.FieldLabel("Description"));
            var descField = ChimeraComponents.Input("Description", scenario.Description ?? "");
            descField.MaxLength = DescriptionMaxLength;
            descField.TextChanged += t => scenario.Description = t;
            box.AddChild(descField);

            box.AddChild(ChimeraComponents.FieldLabel("Suggested Players"));
            var playersSelect = ChimeraComponents.Select(PlayerOptions);
            playersSelect.Selected = Mathf.Clamp(scenario.SuggestedPlayers - 2, 0, PlayerOptions.Length - 1);
            playersSelect.ItemSelected += idx => scenario.SuggestedPlayers = (int)idx + 2;
            box.AddChild(playersSelect);

            box.AddChild(ChimeraComponents.FieldLabel("Map Size"));
            // Story 6.7 (patch 11) — normalize an unsupported (legacy/hand-authored) bounds to Medium ON BIND so the
            // shown picker value and the model agree (no spurious change event: this is a direct field write, not a
            // picker signal). A supported bounds is left untouched.
            if (!MapSizes.IsSupportedBounds(scenario.MapBounds))
                scenario.MapBounds = MapSizes.ToBounds(MapSize.Medium);
            var sizeSelect = ChimeraComponents.Select(SizeLabels());
            sizeSelect.Selected = IndexOfSize(MapSizes.FromBounds(scenario.MapBounds));
            sizeSelect.ItemSelected += idx =>
                scenario.MapBounds = MapSizes.ToBounds(MapSizes.All[Mathf.Clamp((int)idx, 0, MapSizes.All.Length - 1)]);
            box.AddChild(sizeSelect);

            return box;
        }

        private static string[] SizeLabels()
        {
            var labels = new string[MapSizes.All.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = MapSizes.Label(MapSizes.All[i]);
            return labels;
        }

        private static int IndexOfSize(MapSize size)
        {
            for (int i = 0; i < MapSizes.All.Length; i++)
                if (MapSizes.All[i] == size) return i;
            return 0;
        }
    }
}
