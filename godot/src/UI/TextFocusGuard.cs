#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// The ONE rule for "the player is typing, so global hotkeys must not fire".
    ///
    /// <para>Godot delivers <c>_Input</c> BEFORE the focused Control gets the event and
    /// <c>_UnhandledInput</c> AFTER it. Any hotkey handled in <c>_Input</c> therefore fires while a
    /// <see cref="LineEdit"/> has focus, and — because these handlers call
    /// <c>GetViewport().SetInputAsHandled()</c> — it also SWALLOWS the keystroke, so the character never
    /// reaches the field at all. That is why typing an "r" into the map-generator prompt rotated the
    /// placement ghost and produced no text, and why "b"/"u"/Tab drove the entity palette from inside a
    /// menu (reported by Alec, 2026-08-04).</para>
    ///
    /// <para>Two ways to be correct, in order of preference:
    /// <list type="number">
    /// <item>Handle the hotkey in <c>_UnhandledInput</c> — the focused Control consumes it first and the
    /// problem cannot exist (<c>PathabilityTool</c>'s K/P toggles are already right for this reason).</item>
    /// <item>If it MUST be <c>_Input</c> (the tool needs the event before the placer/selection sees it),
    /// call <see cref="IsTyping"/> and bail before touching any key binding.</item>
    /// </list></para>
    ///
    /// <para>Covers <see cref="LineEdit"/> and <see cref="TextEdit"/> (hence <c>CodeEdit</c>), which also
    /// covers a <see cref="SpinBox"/> — its editable field IS an internal <c>LineEdit</c> and is what owns
    /// focus while the user types a number.</para>
    /// </summary>
    public static class TextFocusGuard
    {
        /// <summary>
        /// True when a text-editing control owns keyboard focus. Call from any <c>_Input</c> override that
        /// binds character keys, BEFORE the binding runs. Null-safe: a node outside the tree (no viewport)
        /// reports false, matching the "nothing is focused" case.
        /// </summary>
        public static bool IsTyping(Node node)
        {
            Control? focus = node?.GetViewport()?.GuiGetFocusOwner();
            return focus is LineEdit || focus is TextEdit;
        }
    }
}
