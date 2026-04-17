using ProjectChimera.Core;

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Recalculates supply consumed per faction each simulation tick.
    ///
    /// Runs last in the sim loop (after deaths from CombatSystem) so the count
    /// is always accurate without needing callbacks from Destroy().
    ///
    /// Workers have SupplyCost == 0; combat units have SupplyCost >= 1.
    /// </summary>
    public class SupplySystem : ISimSystem
    {
        private readonly ResourceStore _resources;

        public SupplySystem(ResourceStore resources)
        {
            _resources = resources;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            // Reset counts
            for (int f = 0; f < _resources.SupplyUsed.Length; f++)
                _resources.SupplyUsed[f] = 0;

            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if ((world.Flags[i] & EntityFlags.Alive) == 0) continue;
                _resources.SupplyUsed[(int)world.FactionOf[i]] += world.SupplyCost[i];
            }
        }
    }
}
