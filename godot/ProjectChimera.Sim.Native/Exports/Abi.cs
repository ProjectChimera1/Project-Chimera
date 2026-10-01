#nullable enable
using System.Runtime.InteropServices;

namespace ProjectChimera.SimNative
{
    /// <summary>One entity slot as the C ABI sees it: 56 bytes, identical in layout to <c>ChimeraUnit</c> in
    /// <c>include/chimera_sim.h</c> (the ABI of record). Fixed raw values, Godot axes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChimeraUnit
    {
        public int Id;
        public int Ref;
        public int PosX, PosY, PosZ;
        public int PrevX, PrevY, PrevZ;
        public int VelX, VelY, VelZ;
        public int HpRaw;
        public int MaxHpRaw;
        public byte Faction;
        public byte MeshType;
        public byte Flags;
        public byte Command;
    }

    /// <summary>One building slot: 28 bytes, identical in layout to <c>ChimeraBuilding</c> in <c>chimera_sim.h</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChimeraBuilding
    {
        public int Slot;
        public int PosX, PosY, PosZ;
        public int HpRaw;
        public int MaxHpRaw;
        public byte Faction;
        public byte Type;
        public byte Alive;
        public byte Pad;
    }

    /// <summary>Status codes and constants of the C ABI (<c>chimera_sim.h</c>).</summary>
    internal static class Abi
    {
        public const int Version = 0x00010000;
        public const int UnitSize = 56;
        public const int BuildingSize = 28;
        public const int PreTickCount = 6;
        public const int StatsCount = 8;
        public const uint FlagAi = 1u;
        public const int MaxSessions = 16;

        public const int Ok = 0;
        public const int EArg = -1;
        public const int ESession = -2;
        public const int EContent = -3;
        public const int EScenario = -4;
        public const int EBuffer = -5;
        public const int EException = -6;
        public const int EAbi = -7;
    }
}
