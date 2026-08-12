#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the wire type a data edge carries (its "wire color"). 7.2 shipped only <see cref="Boolean"/>
    /// (the condition→trigger gate); Story 7.4 appends <see cref="Int"/>/<see cref="Fixed"/>/<see cref="Point"/>
    /// for the typed expression sublanguage (wire color = type). Members are APPENDED, never reordered/renamed —
    /// serialization is NAME-ONLY via the registered <c>JsonStringEnumConverter(allowIntegerValues:false)</c> so a
    /// numeric wire value fails closed and the existing <c>Boolean</c> name is untouched.
    /// </summary>
    public enum DataWireType
    {
        /// <summary>A boolean value (the condition→trigger gate wire and boolean expression wires).</summary>
        Boolean,
        /// <summary>A signed 32-bit integer expression value (Story 7.4).</summary>
        Int,
        /// <summary>A 16.16 fixed-point expression value, carried as <c>Fixed.Raw</c> (Story 7.4).</summary>
        Fixed,
        /// <summary>A 2D point (two <c>Fixed.Raw</c> ints, X then Z) — consumed only by <c>distance</c> (Story 7.4).</summary>
        Point,
    }

    /// <summary>
    /// DW-337 — the DETERMINISTIC hash primitive behind <see cref="ExecEdge.GetHashCode"/> and
    /// <see cref="DataEdge.GetHashCode"/>. <c>System.HashCode.Combine</c> mixes in a PROCESS-SEED that is
    /// re-randomized on every launch, so two runs of the same build hand the same edge two different hash
    /// codes. Nothing hashes edges today (all ordering goes through <c>IComparable.CompareTo</c>), but the
    /// moment graph editing/dedup puts edges in a <c>HashSet</c>/<c>Dictionary</c>, bucket layout — and with
    /// it any enumeration order that survives a remove/re-add — becomes run-dependent, which is a determinism
    /// break in a lockstep sim. This fold is FNV-1a-32 over the fields as 4 little-endian bytes each, in a
    /// FIXED field order: pure integer arithmetic, no seed, no runtime-version dependence, so it is stable
    /// across processes, machines and .NET runtimes. Same primitive as <c>SimChecksum.Mix</c> /
    /// <c>CanonicalModelHash</c> (32-bit variant); deliberately duplicated here so <c>src/Dsl</c> keeps no
    /// dependency on the checksum module and neither can drift the other.
    ///
    /// The values are NOT folded into <c>SimChecksum</c>, <c>CanonicalModelHash</c> or <c>StartStateHash</c>
    /// — those hash the edge FIELDS in sorted order, never <c>GetHashCode</c> — so changing this fold moves
    /// no golden. It must still never change casually: it is the stability contract callers rely on.
    /// </summary>
    internal static class GraphEdgeHash
    {
        /// <summary>FNV-1a-32 offset basis — the fold's fixed, seed-free starting state.</summary>
        internal const uint Seed = 2166136261u;
        private const uint Prime = 16777619u;

        /// <summary>
        /// FNV-1a mix of one 32-bit value as 4 little-endian bytes. <c>unchecked</c> is explicit so the fold
        /// keeps wrapping (and stays byte-identical) even if the project ever turns on overflow checking.
        /// </summary>
        internal static uint Mix(uint hash, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                hash ^= v & 0xFF;         hash *= Prime;
                hash ^= (v >> 8) & 0xFF;  hash *= Prime;
                hash ^= (v >> 16) & 0xFF; hash *= Prime;
                hash ^= (v >> 24) & 0xFF; hash *= Prime;
                return hash;
            }
        }

        /// <summary>The four-field topology fold shared by both edge kinds (<c>Src, SrcPort, Dst, DstPort</c>).</summary>
        internal static uint Topology(int src, int srcPort, int dst, int dstPort)
        {
            uint h = Seed;
            h = Mix(h, src);
            h = Mix(h, srcPort);
            h = Mix(h, dst);
            h = Mix(h, dstPort);
            return h;
        }
    }

    /// <summary>
    /// A sparse exec edge (control flow): "after <see cref="Src"/>'s <see cref="SrcPort"/> fires, run
    /// <see cref="Dst"/>'s <see cref="DstPort"/>." EventNode→Trigger (event-in) and the linear
    /// Trigger→Action0→…→Action_n chain are exec edges. Immutable value; a total
    /// <c>(Src,SrcPort,Dst,DstPort)</c> order drives canonical serialization.
    ///
    /// DETERMINISM (DW-337): <see cref="GetHashCode"/> is a seed-free FNV-1a-32 fold (see
    /// <see cref="GraphEdgeHash"/>), NOT <c>HashCode.Combine</c>, so a hash-based edge collection lays out
    /// identically in every process. Even so, prefer the ordered list + <see cref="CompareTo"/> whenever the
    /// RESULT ORDER matters (canonical emission, IR compilation, checksum folds): a <c>HashSet</c>/
    /// <c>Dictionary</c> keyed on edges is fine for membership/dedup, but sort what you enumerate out of it —
    /// hash-container enumeration order is an unspecified implementation detail, not a contract.
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

        /// <summary>
        /// DW-337 — a DETERMINISTIC hash: seed-free FNV-1a-32 over <c>(Src, SrcPort, Dst, DstPort)</c> in that
        /// fixed order (was <c>HashCode.Combine</c>, whose per-process random seed made the value differ between
        /// runs of the same build). Covers exactly the fields <see cref="Equals(ExecEdge)"/> compares.
        /// </summary>
        public override int GetHashCode() =>
            unchecked((int)GraphEdgeHash.Topology(Src, SrcPort, Dst, DstPort));
    }

    /// <summary>
    /// A sparse typed data edge (dataflow): <see cref="Src"/>'s <see cref="SrcPort"/> supplies a value of
    /// <see cref="Wire"/> type to <see cref="Dst"/>'s <see cref="DstPort"/>. In 7.2 the only data edge is the
    /// ConditionNode→Trigger Boolean gate. Immutable value; a total <c>(Src,SrcPort,Dst,DstPort,Wire)</c> order
    /// drives canonical serialization.
    ///
    /// DETERMINISM (DW-337): <see cref="GetHashCode"/> is a seed-free FNV-1a-32 fold (see
    /// <see cref="GraphEdgeHash"/>), NOT <c>HashCode.Combine</c>, so a hash-based edge collection lays out
    /// identically in every process. Even so, prefer the ordered list + <see cref="CompareTo"/> whenever the
    /// RESULT ORDER matters (canonical emission, IR compilation, checksum folds): a <c>HashSet</c>/
    /// <c>Dictionary</c> keyed on edges is fine for membership/dedup, but sort what you enumerate out of it —
    /// hash-container enumeration order is an unspecified implementation detail, not a contract.
    ///
    /// <para>DW-754 — <see cref="CompareTo"/> IS total (it ends on <see cref="Wire"/>). It used to stop at the
    /// topology tuple, which made it inconsistent with <see cref="Equals(DataEdge)"/> (that one DOES compare
    /// <see cref="Wire"/>): two data edges sharing a topology tuple but differing in wire compared EQUAL while
    /// being unequal values, so every <c>OrderBy(e =&gt; e)</c> in the module left their relative order to LINQ's
    /// sort stability — i.e. to AUTHORING order. <c>TriggerGraph.ToCanonicalJson</c> inherited that, so two
    /// structurally-equal graphs built in different orders could serialize to different bytes, weakening the
    /// documented byte-identity claim. <c>CanonicalModelHash.MixTriggerGraph</c> had already hand-broken the tie
    /// with <c>.ThenBy(x =&gt; x.Wire)</c>; with the order total that tiebreak is a redundant no-op kept as
    /// belt-and-braces, and no consumer has to remember the footgun any more.</para>
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

        /// <summary>
        /// TOTAL order by <c>(Src, SrcPort, Dst, DstPort, Wire)</c> for canonical emission (DW-754). The
        /// <see cref="Wire"/> tiebreak is what makes it total AND consistent with <see cref="Equals(DataEdge)"/>:
        /// <c>CompareTo(other) == 0</c> now holds exactly when <c>Equals(other)</c> does, so no consumer's
        /// <c>OrderBy</c> can fall back to sort stability (= authoring order) on a duplicate-topology pair.
        /// <see cref="Wire"/> is ordered by its ORDINAL — safe for the same reason the hash folds it that way:
        /// <see cref="DataWireType"/> members are append-only, never reordered or renamed.
        /// </summary>
        public int CompareTo(DataEdge other)
        {
            int c = Src.CompareTo(other.Src);
            if (c != 0) return c;
            c = SrcPort.CompareTo(other.SrcPort);
            if (c != 0) return c;
            c = Dst.CompareTo(other.Dst);
            if (c != 0) return c;
            c = DstPort.CompareTo(other.DstPort);
            if (c != 0) return c;
            return ((int)Wire).CompareTo((int)other.Wire);
        }

        public bool Equals(DataEdge other) =>
            Src == other.Src && SrcPort == other.SrcPort && Dst == other.Dst && DstPort == other.DstPort && Wire == other.Wire;

        public override bool Equals(object? obj) => obj is DataEdge e && Equals(e);

        /// <summary>
        /// DW-337 — a DETERMINISTIC hash: seed-free FNV-1a-32 over <c>(Src, SrcPort, Dst, DstPort, Wire)</c> in
        /// that fixed order (was <c>HashCode.Combine</c>, whose per-process random seed made the value differ
        /// between runs of the same build). <see cref="Wire"/> is folded by its ORDINAL — safe because
        /// <see cref="DataWireType"/> members are append-only, never reordered/renamed, and the same reason the
        /// JSON layer pins the enum by name. Covers exactly the fields <see cref="Equals(DataEdge)"/> compares.
        /// </summary>
        public override int GetHashCode() =>
            unchecked((int)GraphEdgeHash.Mix(GraphEdgeHash.Topology(Src, SrcPort, Dst, DstPort), (int)Wire));
    }

    /// <summary>
    /// Story 7.7 — the fail-closed <see cref="DataEdge"/> converter. Before 7.7 the struct deserialized through
    /// its <c>[JsonConstructor]</c>, where an authored data edge MISSING its <c>wire</c> silently defaulted to
    /// <see cref="DataWireType.Boolean"/> (a fail-OPEN typing hole in the "wire color = type" contract). This
    /// converter makes EVERY missing key a LOCATED parse reject (review follow-up: a missing endpoint previously
    /// defaulted to node 0 — a real node the edge then silently rewired onto); a DUPLICATE key is a located reject
    /// too (mirroring <c>NodeBaseJsonConverter.RejectUnknownProperties</c> — <c>JsonDocument</c> permits duplicate
    /// names, and a second value could otherwise smuggle past validation); the unknown-property posture stays as-is
    /// (rejected — the POCO layer's <c>UnmappedMemberHandling.Disallow</c> equivalent, re-applied here because a
    /// custom converter bypasses it); and the wire enum parses by NAME only, case-SENSITIVE, never by value —
    /// <c>Enum.TryParse</c> alone would accept <c>"2"</c> numerically, against the converter's documented strict
    /// posture (mirroring <c>JsonStringEnumConverter(allowIntegerValues:false)</c>). <see cref="Write"/> emits the
    /// exact property layout the POCO serialization produced (src, src_port, dst, dst_port, wire-by-name), so
    /// canonical bytes are unchanged for every existing graph.
    /// </summary>
    public sealed class DataEdgeJsonConverter : JsonConverter<DataEdge>
    {
        /// <inheritdoc />
        public override DataEdge Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"data edge must be a JSON object, got {el.ValueKind}.");

            int src = 0, srcPort = 0, dst = 0, dstPort = 0;
            DataWireType wire = default;
            bool hasSrc = false, hasSrcPort = false, hasDst = false, hasDstPort = false, hasWire = false;
            foreach (JsonProperty p in el.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "src":      RejectDuplicate(hasSrc, "src");           src     = ReadInt(p, "src");      hasSrc = true; break;
                    case "src_port": RejectDuplicate(hasSrcPort, "src_port");  srcPort = ReadInt(p, "src_port"); hasSrcPort = true; break;
                    case "dst":      RejectDuplicate(hasDst, "dst");           dst     = ReadInt(p, "dst");      hasDst = true; break;
                    case "dst_port": RejectDuplicate(hasDstPort, "dst_port");  dstPort = ReadInt(p, "dst_port"); hasDstPort = true; break;
                    case "wire":
                        RejectDuplicate(hasWire, "wire");
                        if (p.Value.ValueKind != JsonValueKind.String)
                            throw new JsonException(
                                $"data edge 'wire' must be a wire-type NAME string (Boolean/Int/Fixed/Point), got {p.Value.ValueKind}.");
                        string name = p.Value.GetString()!;
                        // NAME-only, case-sensitive: a digit/sign-leading string parses BY VALUE through
                        // Enum.TryParse ("2" → Fixed), so reject it before the parse ever runs.
                        if (name.Length == 0 || !char.IsLetter(name[0])
                            || !Enum.TryParse(name, ignoreCase: false, out wire) || !Enum.IsDefined(typeof(DataWireType), wire))
                            throw new JsonException($"data edge 'wire' value '{name}' is not a known wire type (Boolean/Int/Fixed/Point, exact name).");
                        hasWire = true;
                        break;
                    default:
                        throw new JsonException($"data edge property '{p.Name}' is unknown (data edges are closed: src/src_port/dst/dst_port/wire).");
                }
            }
            // ALL five keys are required — a missing endpoint would silently default to node/port 0 (fail-open).
            if (!hasSrc)     throw new JsonException("data edge is missing its required 'src' node id (it would silently default to node 0).");
            if (!hasSrcPort) throw new JsonException($"data edge (src {src}) is missing its required 'src_port'.");
            if (!hasDst)     throw new JsonException($"data edge (src {src}:{srcPort}) is missing its required 'dst' node id (it would silently default to node 0).");
            if (!hasDstPort) throw new JsonException($"data edge ({src}:{srcPort} → {dst}) is missing its required 'dst_port'.");
            if (!hasWire)
                throw new JsonException(
                    $"data edge ({src}:{srcPort} → {dst}:{dstPort}) is missing its required 'wire' type — " +
                    "wire color = type is load-bearing and no longer defaults to Boolean (Story 7.7 fail-closed parse).");
            return new DataEdge(src, srcPort, dst, dstPort, wire);
        }

        /// <summary>Located duplicate-key reject (each field may appear at most once — the AR-22 posture).</summary>
        private static void RejectDuplicate(bool seen, string name)
        {
            if (seen)
                throw new JsonException($"data edge property '{name}' is a duplicate (each field may appear at most once).");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, DataEdge value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("src", value.Src);
            writer.WriteNumber("src_port", value.SrcPort);
            writer.WriteNumber("dst", value.Dst);
            writer.WriteNumber("dst_port", value.DstPort);
            writer.WriteString("wire", value.Wire.ToString()); // enum by NAME (the JsonStringEnumConverter layout)
            writer.WriteEndObject();
        }

        private static int ReadInt(JsonProperty p, string name)
        {
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int v))
                throw new JsonException($"data edge '{name}' must be a 32-bit integer.");
            return v;
        }
    }

    /// <summary>
    /// DW-357 — the fail-closed <see cref="ExecEdge"/> converter, symmetric to <see cref="DataEdgeJsonConverter"/>
    /// (minus the wire — exec edges are untyped control flow). Before this, the struct deserialized through its
    /// <c>[JsonConstructor]</c>, where a hand-authored <c>exec_edge</c> OMITTING <c>src</c>/<c>dst</c> silently
    /// defaulted the missing endpoint to node/port 0 — and because node 0 usually exists (the first trigger),
    /// <c>GraphStructureGate</c>'s "every endpoint must exist" check was satisfied and the malformed edge REROUTED
    /// the exec chain onto node 0 undetected. This converter makes EVERY missing endpoint key a LOCATED parse
    /// reject; a DUPLICATE key is a located reject too (mirroring the data-edge posture — <c>JsonDocument</c>
    /// permits duplicate names, and a second value could otherwise smuggle past validation); an unknown property
    /// rejects (the closed-shape posture a custom converter must re-apply because it bypasses
    /// <c>UnmappedMemberHandling.Disallow</c>). <see cref="Write"/> emits the exact property layout the POCO
    /// serialization produced (src, src_port, dst, dst_port), so canonical bytes are unchanged for every existing
    /// graph. Sanctioned editor output always writes full endpoints — only direct/hand-crafted JSON is affected.
    /// </summary>
    public sealed class ExecEdgeJsonConverter : JsonConverter<ExecEdge>
    {
        /// <inheritdoc />
        public override ExecEdge Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument doc = JsonDocument.ParseValue(ref reader);
            JsonElement el = doc.RootElement;
            if (el.ValueKind != JsonValueKind.Object)
                throw new JsonException($"exec edge must be a JSON object, got {el.ValueKind}.");

            int src = 0, srcPort = 0, dst = 0, dstPort = 0;
            bool hasSrc = false, hasSrcPort = false, hasDst = false, hasDstPort = false;
            foreach (JsonProperty p in el.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "src":      RejectDuplicate(hasSrc, "src");           src     = ReadInt(p, "src");      hasSrc = true; break;
                    case "src_port": RejectDuplicate(hasSrcPort, "src_port");  srcPort = ReadInt(p, "src_port"); hasSrcPort = true; break;
                    case "dst":      RejectDuplicate(hasDst, "dst");           dst     = ReadInt(p, "dst");      hasDst = true; break;
                    case "dst_port": RejectDuplicate(hasDstPort, "dst_port");  dstPort = ReadInt(p, "dst_port"); hasDstPort = true; break;
                    default:
                        throw new JsonException($"exec edge property '{p.Name}' is unknown (exec edges are closed: src/src_port/dst/dst_port).");
                }
            }
            // ALL four keys are required — a missing endpoint would silently default to node/port 0 (fail-open:
            // node 0 usually exists, so the mis-wired edge would pass the structural gate and reroute the chain).
            if (!hasSrc)     throw new JsonException("exec edge is missing its required 'src' node id (it would silently default to node 0).");
            if (!hasSrcPort) throw new JsonException($"exec edge (src {src}) is missing its required 'src_port'.");
            if (!hasDst)     throw new JsonException($"exec edge (src {src}:{srcPort}) is missing its required 'dst' node id (it would silently default to node 0).");
            if (!hasDstPort) throw new JsonException($"exec edge ({src}:{srcPort} → {dst}) is missing its required 'dst_port'.");
            return new ExecEdge(src, srcPort, dst, dstPort);
        }

        /// <summary>Located duplicate-key reject (each field may appear at most once — the AR-22 posture).</summary>
        private static void RejectDuplicate(bool seen, string name)
        {
            if (seen)
                throw new JsonException($"exec edge property '{name}' is a duplicate (each field may appear at most once).");
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, ExecEdge value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("src", value.Src);
            writer.WriteNumber("src_port", value.SrcPort);
            writer.WriteNumber("dst", value.Dst);
            writer.WriteNumber("dst_port", value.DstPort);
            writer.WriteEndObject();
        }

        private static int ReadInt(JsonProperty p, string name)
        {
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out int v))
                throw new JsonException($"exec edge '{name}' must be a 32-bit integer.");
            return v;
        }
    }
}
