#nullable enable
namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.10 — the "wire color = type" palette: a Godot-free, float-free map from each
    /// <see cref="DataWireType"/> to a stable sRGB hex string, plus one control color for exec edges. The T3 view
    /// (<c>DslGraphEditorPanel</c>) is the ONLY consumer and converts a hex to a <c>Godot.Color</c> via
    /// <c>Color.FromHtml</c> — so no Godot type ever enters <c>src/Dsl/**</c> and the mapping stays Tier-1
    /// unit-testable (stable + all four data colors mutually distinct).
    ///
    /// <para>The four data colors are FIXED (never derived at runtime): a layout/theme change must not silently
    /// remap a wire's meaning. They are chosen distinct in hue so Boolean/Int/Fixed/Point read apart at a glance;
    /// the exec color is a neutral light control tone distinct from all four.</para>
    /// </summary>
    public static class DataWireColorPalette
    {
        /// <summary>Boolean data wire (the condition→trigger gate + boolean expression wires) — blue.</summary>
        public const string BooleanHex = "#4f9dff";
        /// <summary>Int data wire — green.</summary>
        public const string IntHex = "#5fd75f";
        /// <summary>Fixed (16.16) data wire — orange.</summary>
        public const string FixedHex = "#ffa94f";
        /// <summary>Point (X,Z) data wire — magenta.</summary>
        public const string PointHex = "#d75fd7";
        /// <summary>Exec (control-flow) edge color — neutral light grey, distinct from every data hue.</summary>
        public const string ExecHex = "#dcdcdc";

        /// <summary>The stable sRGB hex for <paramref name="wire"/>'s data color (defaults to Boolean's for any
        /// unforeseen enum value — the enum is closed, so unreachable in practice).</summary>
        public static string HexFor(DataWireType wire) => wire switch
        {
            DataWireType.Boolean => BooleanHex,
            DataWireType.Int     => IntHex,
            DataWireType.Fixed   => FixedHex,
            DataWireType.Point   => PointHex,
            _                    => BooleanHex,
        };
    }
}
