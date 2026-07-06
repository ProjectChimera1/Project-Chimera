#nullable enable
using Godot;

namespace ProjectChimera.UI.Components
{
    /// <summary>
    /// Story 3.1c shared feedback-layer machinery: the transient-overlay z-order strategy (Task 2) that the
    /// 3.1b kit deliberately lacked (it had zero Popup/scrim/CanvasLayer code — grep-confirmed). Composite
    /// components float above the base UI two ways:
    ///   • <b>PopupPanel</b> (a Popup) for the menu + the <c>.select</c> dropdown — native above-everything
    ///     layer, positions near the trigger, closes on outside-click (OK to take focus).
    ///   • a <b>high CanvasLayer</b> (≈100+, above the base UI's ≈14) for the tooltip (a plain Control, no
    ///     focus-steal), the dialog (scrim + panel), and the toast host.
    ///
    /// The dialog + toast host each ARE a CanvasLayer and set their own <see cref="OverlayLayer"/>; the
    /// tooltip needs somewhere to park a plain Control, so <see cref="GetOverlayLayer"/> lazily mints one
    /// shared CanvasLayer per (scene, name). Part of the <see cref="ChimeraComponents"/> factory.
    ///
    /// Presentation layer.
    /// </summary>
    public static partial class ChimeraComponents
    {
        // ── Transient-overlay z-order (above the base UI's ≈14; dialogs over toasts, tooltips over all) ──

        /// <summary>CanvasLayer for the toast host (100).</summary>
        internal const int OverlayLayerToast = 100;
        /// <summary>CanvasLayer for a modal dialog scrim + panel (101 — above toasts).</summary>
        internal const int OverlayLayerDialog = 101;
        /// <summary>CanvasLayer for tooltips (102 — above everything, incl. an open dialog).</summary>
        internal const int OverlayLayerTooltip = 102;

        /// <summary>
        /// Get (or lazily create) a shared, named high <see cref="CanvasLayer"/> to host a top-level overlay
        /// Control (the tooltip's home — Task 2's "add a Control as a top-level overlay" helper). Parented to
        /// the current scene when there is one (so it is freed with the scene, not leaked to the root across
        /// scene swaps), else to the tree root. Reused by name, and re-created if a prior instance was freed.
        /// </summary>
        internal static CanvasLayer GetOverlayLayer(Node context, string name, int layer)
        {
            SceneTree tree = context.GetTree();
            // Prefer the running scene so the layer's lifetime tracks the scene; fall back to the root.
            Node host = tree.CurrentScene ?? (Node)tree.Root;
            var existing = host.GetNodeOrNull<CanvasLayer>(name);
            if (existing != null && GodotObject.IsInstanceValid(existing)) return existing;

            var cl = new CanvasLayer { Name = name, Layer = layer };
            host.AddChild(cl);
            return cl;
        }
    }
}
