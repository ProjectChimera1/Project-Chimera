#nullable enable
using System;
using System.Text.Json.Serialization;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the wire type a data edge carries (its "wire color"). 7.2 ships only <see cref="Boolean"/>
    /// (the condition→trigger gate); 7.4 extends this with Int/Fixed/… . Serialized NAME-ONLY via the registered
    /// <c>JsonStringEnumConverter(allowIntegerValues:false)</c> so a numeric wire value fails closed.
    /// </summary>
    public enum DataWireType
    {
        Boolean,
    }

    /// <summary>
    /// A sparse exec edge (control flow): "after <see cref="Src"/>'s <see cref="SrcPort"/> fires, run
    /// <see cref="Dst"/>'s <see cref="DstPort"/>." EventNode→Trigger (event-in) and the linear
    /// Trigger→Action0→…→Action_n chain are exec edges. Immutable value; a total
    /// <c>(Src,SrcPort,Dst,DstPort)</c> order drives canonical serialization.
    /// </summary>
    public readonly struct ExecEdge : IComparable<ExecEdge>, IEquatable<ExecEdge>
    {
        [JsonPropertyName("src")]      public int Src { get; }
        [JsonPropertyName("src_port")] public int SrcPort { get; }
        [JsonPropertyName("dst")]      public int Dst { get; }
        [JsonPropertyName("dst_port")] public int DstPort { get; }

        [JsonConstructor]
        public ExecEdge(int src, int srcPort, int dst, int dstPort)
        {
            Src = src; SrcPort = srcPort; Dst = dst; DstPort = dstPort;
        }

        /// <summary>Total order by <c>(Src, SrcPort, Dst, DstPort)</c> for canonical emission.</summary>
        public int CompareTo(ExecEdge other)
        {
            int c = Src.CompareTo(other.Src);
            if (c != 0) return c;
            c = SrcPort.CompareTo(other.SrcPort);
            if (c != 0) return c;
            c = Dst.CompareTo(other.Dst);
            if (c != 0) return c;
            return DstPort.CompareTo(other.DstPort);
        }

        public bool Equals(ExecEdge other) =>
            Src == other.Src && SrcPort == other.SrcPort && Dst == other.Dst && DstPort == other.DstPort;

        public override bool Equals(object? obj) => obj is ExecEdge e && Equals(e);
        public override int GetHashCode() => HashCode.Combine(Src, SrcPort, Dst, DstPort);
    }

    /// <summary>
    /// A sparse typed data edge (dataflow): <see cref="Src"/>'s <see cref="SrcPort"/> supplies a value of
    /// <see cref="Wire"/> type to <see cref="Dst"/>'s <see cref="DstPort"/>. In 7.2 the only data edge is the
    /// ConditionNode→Trigger Boolean gate. Immutable value; a total <c>(Src,SrcPort,Dst,DstPort)</c> order drives
    /// canonical serialization (the wire type is NOT part of the sort key — the topology tuple is total on its own).
    /// </summary>
    public readonly struct DataEdge : IComparable<DataEdge>, IEquatable<DataEdge>
    {
        [JsonPropertyName("src")]      public int Src { get; }
        [JsonPropertyName("src_port")] public int SrcPort { get; }
        [JsonPropertyName("dst")]      public int Dst { get; }
        [JsonPropertyName("dst_port")] public int DstPort { get; }
        [JsonPropertyName("wire")]     public DataWireType Wire { get; }

        [JsonConstructor]
        public DataEdge(int src, int srcPort, int dst, int dstPort, DataWireType wire)
        {
            Src = src; SrcPort = srcPort; Dst = dst; DstPort = dstPort; Wire = wire;
        }

        /// <summary>Total order by <c>(Src, SrcPort, Dst, DstPort)</c> for canonical emission.</summary>
        public int CompareTo(DataEdge other)
        {
            int c = Src.CompareTo(other.Src);
            if (c != 0) return c;
            c = SrcPort.CompareTo(other.SrcPort);
            if (c != 0) return c;
            c = Dst.CompareTo(other.Dst);
            if (c != 0) return c;
            return DstPort.CompareTo(other.DstPort);
        }

        public bool Equals(DataEdge other) =>
            Src == other.Src && SrcPort == other.SrcPort && Dst == other.Dst && DstPort == other.DstPort && Wire == other.Wire;

        public override bool Equals(object? obj) => obj is DataEdge e && Equals(e);
        public override int GetHashCode() => HashCode.Combine(Src, SrcPort, Dst, DstPort, Wire);
    }
}
