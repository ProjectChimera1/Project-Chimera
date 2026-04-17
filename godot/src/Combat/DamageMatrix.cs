namespace ProjectChimera.Combat
{
    /// <summary>
    /// The type of damage an attacker deals.
    /// </summary>
    public enum DamageType : byte
    {
        Normal = 0,
        Pierce = 1,
        Siege = 2,
        Magic = 3,
        COUNT = 4
    }

    /// <summary>
    /// The armor classification of a unit.
    /// </summary>
    public enum ArmorType : byte
    {
        Unarmored = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3,
        Fortified = 4, // Buildings
        COUNT = 5
    }

    /// <summary>
    /// Damage effectiveness multipliers: DamageMatrix.Get(damageType, armorType).
    /// Expressed as Fixed fractions — 1.0 = full damage, 0.5 = half, 2.0 = double.
    /// Data-driven: load from JSON in Phase 1. Defaults are hardcoded here for Phase 0.
    /// </summary>
    public static class DamageMatrix
    {
        // [DamageType, ArmorType] stored as hundredths of a float → converted to Fixed at init
        // Rows = DamageType (Normal, Pierce, Siege, Magic)
        // Cols = ArmorType  (Unarmored, Light, Medium, Heavy, Fortified)
        private static readonly float[,] _table = new float[,]
        {
            //  Unarmored  Light   Medium  Heavy   Fortified
            {   1.00f,     1.00f,  0.75f,  0.50f,  0.35f  }, // Normal
            {   1.50f,     1.00f,  0.75f,  0.35f,  0.25f  }, // Pierce
            {   0.50f,     0.50f,  1.00f,  1.00f,  1.50f  }, // Siege
            {   1.00f,     1.00f,  1.00f,  1.00f,  0.50f  }, // Magic
        };

        private static readonly Core.Fixed[,] _fixed;

        static DamageMatrix()
        {
            int rows = (int)DamageType.COUNT;
            int cols = (int)ArmorType.COUNT;
            _fixed = new Core.Fixed[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    _fixed[r, c] = Core.Fixed.FromFloat(_table[r, c]);
        }

        /// <summary>
        /// Returns the damage multiplier for a given damage/armor pair.
        /// </summary>
        public static Core.Fixed Get(DamageType damage, ArmorType armor) =>
            _fixed[(int)damage, (int)armor];
    }
}
