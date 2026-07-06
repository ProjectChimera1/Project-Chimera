#nullable enable
using Godot;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// The UX-DR55 located validation badge (Story 3.4, D-4) — a small reusable kit helper that anchors a
    /// <see cref="ChimeraComponents.TagVariant.Danger"/> pill to a field control and carries the full located error
    /// sentence in a field-role <see cref="ChimeraTooltip"/> (revealed on hover AND keyboard focus, per UX-DR53/NFR-2).
    /// A matching <see cref="ChimeraComponents.TagVariant.Ok"/> "valid" state is available.
    ///
    /// <para><b>Why net-new (D-4).</b> Nothing in the 3.1 kit is a validation badge: <c>ChimeraMark</c> is the
    /// decorative Seal, and the AbilityEditor uses a single panel-level status line. Stories 3.5/3.6/3.7 all need "a
    /// located badge on the offending field", so this is logged per UX-DR33 (a missing primitive proven by 4-story
    /// reuse). Presentation layer; built from the <see cref="ChimeraComponents"/> factory (requires
    /// <see cref="ChimeraComponents.Initialize"/> — the hosting panel bootstraps it).</para>
    /// </summary>
    public partial class ChimeraValidationBadge : HBoxContainer
    {
        /// <summary>Create a hidden badge (place it in a field row, then <see cref="ShowError"/> / <see cref="Hide"/>).</summary>
        public static ChimeraValidationBadge Create()
        {
            var b = new ChimeraValidationBadge
            {
                Visible = false,
                SizeFlagsVertical = SizeFlags.ShrinkCenter,
            };
            b.AddThemeConstantOverride("separation", 0);
            return b;
        }

        /// <summary>Show the danger pill with the located error <paramref name="message"/> in a field tooltip.</summary>
        public void ShowError(string message) =>
            Rebuild("!", ChimeraComponents.TagVariant.Danger, "Invalid", message);

        /// <summary>Show the ok pill (the "valid" state) with an optional tooltip sentence.</summary>
        public void ShowValid(string message = "This field is valid.") =>
            Rebuild("✓", ChimeraComponents.TagVariant.Ok, "Valid", message);

        /// <summary>Clear + hide the badge (no pill shown). Named <c>Clear</c> (not <c>Hide</c>) to avoid shadowing
        /// <see cref="Godot.CanvasItem.Hide"/>.</summary>
        public void Clear()
        {
            ClearChildren();
            Visible = false;
        }

        private void Rebuild(string glyph, ChimeraComponents.TagVariant variant, string term, string message)
        {
            ClearChildren();
            PanelContainer pill = ChimeraComponents.Tag(glyph, variant);
            // The pill is the unambiguous hover/focus target for the tooltip (the 3.3 AttachTip lesson): Stop so the
            // mouse reveal fires, All so the keyboard-focus reveal fires (a PanelContainer defaults to FocusMode.None).
            pill.MouseFilter = MouseFilterEnum.Stop;
            pill.FocusMode = FocusModeEnum.All;
            MakeChildrenMouseIgnore(pill);
            ChimeraTooltip.Attach(pill, term, message, ChimeraTooltip.TooltipRole.Field);
            AddChild(pill);
            Visible = true;
        }

        private void ClearChildren()
        {
            foreach (Node c in GetChildren()) { RemoveChild(c); c.QueueFree(); }
        }

        private static void MakeChildrenMouseIgnore(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control c) c.MouseFilter = MouseFilterEnum.Ignore;
                MakeChildrenMouseIgnore(child);
            }
        }
    }
}
