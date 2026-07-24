#nullable enable
using ProjectChimera.Core; // SimulationLoop.TICKS_PER_SECOND — the single authoritative sim tick rate

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// Story 9.11 — Godot-free formatting/policy helpers shared by the replay browser + playback controls (so the
    /// browser panel, the overlay, and MainScene never each hardcode the 30-tps conversion or the speed clamp). All
    /// pure functions — Tier-1 unit-testable — keyed off the ONE authoritative
    /// <see cref="SimulationLoop.TICKS_PER_SECOND"/> constant (never a local literal).
    /// </summary>
    public static class ReplayFormat
    {
        /// <summary>The single sim tick rate every replay duration/clock conversion references.</summary>
        public const int TicksPerSecond = SimulationLoop.TICKS_PER_SECOND;

        /// <summary>A tick count → mm:ss clock string (e.g. 0 → "0:00", 1800 → "1:00" at 30 tps).</summary>
        public static string Duration(uint tick)
        {
            uint sec = tick / (uint)TicksPerSecond;
            return $"{sec / 60}:{sec % 60:D2}";
        }

        /// <summary>The result-trailer outcome as display text: "Player N won" / "no victor" / "incomplete".</summary>
        public static string ResultText(int winnerFaction, bool completed)
        {
            if (!completed) return "incomplete";
            return winnerFaction > 0 ? $"Player {winnerFaction} won" : "no victor";
        }

        /// <summary>Clamp a requested playback speed (sim ticks/frame) to the supported 1..8 range (0 → 1, 9 → 8).</summary>
        public static int ClampSpeed(int speed) => speed < 1 ? 1 : speed > 8 ? 8 : speed;
    }
}
