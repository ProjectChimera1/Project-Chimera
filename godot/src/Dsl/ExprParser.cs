#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core; // Fixed (raw 16.16 integer math for decimal literals)

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.4 — the T2 authoring surface: a Godot-free, recursive-descent, CEL-shaped TEXT parser that compiles
    /// expression text into the SAME graph-IR expression subgraph raw-IR authors hand-write (a projection of the
    /// one IR — never a second executable form). Appends nodes/edges to the caller's <see cref="TriggerGraph"/>
    /// and returns the root node id plus its inferred wire type.
    ///
    /// Closed grammar (nothing else parses): binary <c>+ - * / %</c>, comparisons <c>&gt; &lt; &gt;= &lt;= == !=</c>,
    /// boolean <c>&amp;&amp; || !</c>, unary <c>-</c>, grouping <c>( )</c>, literals (Int <c>123</c>, Fixed
    /// <c>1.5</c>, Bool <c>true</c>/<c>false</c>), declared-variable reads (<c>name</c>, per-player
    /// <c>name[k]</c> with an integer-literal slot), and the built-ins count/distance/min/max/abs. Standard
    /// precedence: unary &gt; <c>* / %</c> &gt; <c>+ -</c> &gt; comparisons &gt; <c>&amp;&amp;</c> &gt; <c>||</c>.
    ///
    /// Determinism: decimal literals (e.g. <c>1.5</c>) convert to <c>Fixed.Raw</c> with pure integer/long
    /// arithmetic over invariant '0'..'9' char digits — never floating-point parsing. Types are inferred bottom-up
    /// with the compiler's strict rules so every emitted data edge carries the correct wire color; a mismatch is a
    /// located reject here (and the compiler re-verifies downstream). All errors are located
    /// <see cref="JsonException"/>s ("expr text (pos N): reason"), matching the module's fail-closed posture.
    /// </summary>
    public static class ExprParser
    {
        /// <summary>
        /// Parse <paramref name="text"/> into an expression subgraph appended to <paramref name="graph"/> (fresh
        /// node ids past the graph's current maximum). Returns the root node id and its inferred wire type.
        /// Throws a located <see cref="JsonException"/> on any syntax/type/limit violation. Nodes/edges are
        /// appended EAGERLY as parsing progresses, so a mid-stream throw can leave PARTIAL expression nodes/edges
        /// on the graph — callers must treat the graph as throwaway on failure (both production callers,
        /// <see cref="TriggerGraph.BuildExpressionTrigger"/> and the editor's manual form, build a fresh graph
        /// and discard it when this throws).
        /// </summary>
        /// <param name="arrayDecls">Story 7.6 — declared Array names → (element type, capacity). Null (every 7.4
        /// caller) means no arrays are declared, so <c>arr[expr]</c>/<c>length(arr)</c> reject located.</param>
        /// <param name="eventParams">Story 7.5 — the subscribed event's parameter map (name → slot + SURFACED
        /// type). Null (every non-event caller) means no event params are in scope, so <c>event.&lt;name&gt;</c>
        /// rejects located. Callers pass it by NAME (<c>eventParams:</c>) — position 4 is arrayDecls.</param>
        public static (int RootId, DataWireType Wire) Parse(
            string text, TriggerGraph graph,
            IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> declaredVars,
            IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)>? arrayDecls = null,
            IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? eventParams = null)
        {
            if (text is null)
                throw new JsonException("expr text (pos 0): expression text is null.");
            if (text.Length > ExprBounds.MaxExprTextLength)
                throw new JsonException(
                    $"expr text (pos 0): expression text length {text.Length} exceeds ExprBounds.MaxExprTextLength={ExprBounds.MaxExprTextLength}.");

            int nextId = 0;
            foreach (NodeBase n in graph.Nodes)
                if (n.Id + 1 > nextId) nextId = n.Id + 1;

            var p = new P { Text = text, Pos = 0, Graph = graph, Vars = declaredVars, ArrayDecls = arrayDecls, EventParams = eventParams, NextId = nextId };
            Node root = ParseOr(p);
            p.SkipWs();
            if (p.Pos != text.Length)
                throw p.Fail(p.Pos, $"unexpected trailing input '{text[p.Pos]}'");
            return (root.Id, ExprCompiler.WireOf(root.Type));
        }

        // ── Parser state ─────────────────────────────────────────────────────────

        /// <summary>A built subexpression: its node id, inferred type, and node-nesting depth (root of the
        /// subtree = its own depth; leaves = 1).</summary>
        private readonly struct Node
        {
            public readonly int Id;
            public readonly DslValueType Type;
            public readonly int Depth;
            public Node(int id, DslValueType type, int depth) { Id = id; Type = type; Depth = depth; }
        }

        private sealed class P
        {
            public string Text = "";
            public int Pos;
            public TriggerGraph Graph = null!;
            public IReadOnlyDictionary<string, (DslValueType Type, VarScope Scope)> Vars = null!;
            public IReadOnlyDictionary<string, (DslValueType Elem, int Capacity)>? ArrayDecls;
            /// <summary>Story 7.5 — the subscribed event's parameter map (name → slot + SURFACED type; ref-typed
            /// payload params already surface Int). Null = no event params available (event.&lt;name&gt; rejects).</summary>
            public IReadOnlyDictionary<string, (int Slot, DslValueType Type)>? EventParams;
            public int NextId;

            public void SkipWs()
            {
                while (Pos < Text.Length && (Text[Pos] == ' ' || Text[Pos] == '\t' || Text[Pos] == '\r' || Text[Pos] == '\n'))
                    Pos++;
            }

            /// <summary>True (and consumes) when the next non-ws chars are exactly <paramref name="tok"/>.</summary>
            public bool Match(string tok)
            {
                SkipWs();
                if (Pos + tok.Length > Text.Length) return false;
                for (int i = 0; i < tok.Length; i++)
                    if (Text[Pos + i] != tok[i]) return false;
                Pos += tok.Length;
                return true;
            }

            public char Peek()
            {
                SkipWs();
                return Pos < Text.Length ? Text[Pos] : '\0';
            }

            public JsonException Fail(int pos, string reason) => new JsonException($"expr text (pos {pos}): {reason}.");
        }

        private static bool IsDigit(char c)  => c >= '0' && c <= '9';
        private static bool IsIdentStart(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
        private static bool IsIdentChar(char c)  => IsIdentStart(c) || IsDigit(c);

        /// <summary>Story 7.5 — true when <paramref name="s"/> is a well-formed identifier under THIS parser's
        /// rules (letters/digits/underscore, not digit-leading). Custom-event parameter names must satisfy it so
        /// <c>event.&lt;name&gt;</c> text can always address a declared param.</summary>
        internal static bool IsIdentifier(string? s)
        {
            if (string.IsNullOrEmpty(s) || !IsIdentStart(s![0])) return false;
            for (int i = 1; i < s.Length; i++)
                if (!IsIdentChar(s[i])) return false;
            return true;
        }

        // ── Node construction (with the depth cap enforced as the tree grows) ────

        private static Node AddLeaf(P p, int pos, NodeBase node, DslValueType type)
        {
            node.Id = p.NextId++;
            p.Graph.Nodes.Add(node);
            return new Node(node.Id, type, 1);
        }

        private static Node AddUnary(P p, int pos, string op, in Node child, DslValueType type)
        {
            int depth = child.Depth + 1;
            if (depth > ExprBounds.MaxExprDepth)
                throw p.Fail(pos, $"expression depth {depth} exceeds ExprBounds.MaxExprDepth={ExprBounds.MaxExprDepth}");
            var node = new ExprUnaryNode { Id = p.NextId++, Op = op };
            p.Graph.Nodes.Add(node);
            Wire(p, child, node.Id, TriggerGraph.ExprOperandPort0);
            return new Node(node.Id, type, depth);
        }

        private static Node AddBinary(P p, int pos, string op, in Node left, in Node right, DslValueType type)
        {
            int depth = (left.Depth > right.Depth ? left.Depth : right.Depth) + 1;
            if (depth > ExprBounds.MaxExprDepth)
                throw p.Fail(pos, $"expression depth {depth} exceeds ExprBounds.MaxExprDepth={ExprBounds.MaxExprDepth}");
            var node = new ExprBinaryNode { Id = p.NextId++, Op = op };
            p.Graph.Nodes.Add(node);
            Wire(p, left,  node.Id, TriggerGraph.ExprOperandPort0);
            Wire(p, right, node.Id, TriggerGraph.ExprOperandPort1);
            return new Node(node.Id, type, depth);
        }

        private static void Wire(P p, in Node src, int dstId, int dstPort) =>
            p.Graph.DataEdges.Add(new DataEdge(src.Id, TriggerGraph.ExprDataOutPort, dstId, dstPort, ExprCompiler.WireOf(src.Type)));

        // ── Grammar (lowest to highest precedence) ───────────────────────────────

        private static Node ParseOr(P p)
        {
            Node left = ParseAnd(p);
            while (true)
            {
                p.SkipWs();
                int opPos = p.Pos;
                if (!p.Match("||")) return left;
                Node right = ParseAnd(p);
                if (left.Type != DslValueType.Bool || right.Type != DslValueType.Bool)
                    throw p.Fail(opPos, $"'||' requires Bool operands, got {left.Type} and {right.Type}");
                left = AddBinary(p, opPos, "or", left, right, DslValueType.Bool);
            }
        }

        private static Node ParseAnd(P p)
        {
            Node left = ParseCmp(p);
            while (true)
            {
                p.SkipWs();
                int opPos = p.Pos;
                if (!p.Match("&&")) return left;
                Node right = ParseCmp(p);
                if (left.Type != DslValueType.Bool || right.Type != DslValueType.Bool)
                    throw p.Fail(opPos, $"'&&' requires Bool operands, got {left.Type} and {right.Type}");
                left = AddBinary(p, opPos, "and", left, right, DslValueType.Bool);
            }
        }

        private static Node ParseCmp(P p)
        {
            Node left = ParseAdd(p);
            while (true)
            {
                p.SkipWs();
                int opPos = p.Pos;
                string? op =
                    p.Match(">=") ? "ge" :
                    p.Match("<=") ? "le" :
                    p.Match("==") ? "eq" :
                    p.Match("!=") ? "ne" :
                    p.Match(">")  ? "gt" :
                    p.Match("<")  ? "lt" : null;
                if (op is null) return left;

                Node right = ParseAdd(p);
                bool bothInt   = left.Type == DslValueType.Int   && right.Type == DslValueType.Int;
                bool bothFixed = left.Type == DslValueType.Fixed && right.Type == DslValueType.Fixed;
                bool bothBool  = left.Type == DslValueType.Bool  && right.Type == DslValueType.Bool;
                bool ok = (op is "eq" or "ne") ? (bothInt || bothFixed || bothBool) : (bothInt || bothFixed);
                if (!ok)
                    throw p.Fail(opPos, $"comparison requires matching Int/Fixed{((op is "eq" or "ne") ? "/Bool" : "")} operands (no implicit promotion), got {left.Type} and {right.Type}");
                left = AddBinary(p, opPos, op, left, right, DslValueType.Bool);
            }
        }

        private static Node ParseAdd(P p)
        {
            Node left = ParseMul(p);
            while (true)
            {
                p.SkipWs();
                int opPos = p.Pos;
                string? op = p.Match("+") ? "add" : (p.Match("-") ? "sub" : null);
                if (op is null) return left;
                Node right = ParseMul(p);
                DslValueType t = RequireSameNumeric(p, opPos, op, left, right);
                left = AddBinary(p, opPos, op, left, right, t);
            }
        }

        private static Node ParseMul(P p)
        {
            Node left = ParseUnary(p);
            while (true)
            {
                p.SkipWs();
                int opPos = p.Pos;
                string? op = p.Match("*") ? "mul" : (p.Match("/") ? "div" : (p.Match("%") ? "mod" : null));
                if (op is null) return left;
                Node right = ParseUnary(p);
                DslValueType t = RequireSameNumeric(p, opPos, op, left, right);
                left = AddBinary(p, opPos, op, left, right, t);
            }
        }

        private static DslValueType RequireSameNumeric(P p, int opPos, string op, in Node left, in Node right)
        {
            bool bothInt   = left.Type == DslValueType.Int   && right.Type == DslValueType.Int;
            bool bothFixed = left.Type == DslValueType.Fixed && right.Type == DslValueType.Fixed;
            if (!bothInt && !bothFixed)
                throw p.Fail(opPos, $"operator '{op}' requires both operands Int or both Fixed (no implicit promotion), got {left.Type} and {right.Type}");
            return left.Type;
        }

        private static Node ParseUnary(P p)
        {
            p.SkipWs();
            int opPos = p.Pos;
            // '!=' must not be consumed as a unary '!' — only treat '!' as NOT when not followed by '='.
            if (p.Pos < p.Text.Length && p.Text[p.Pos] == '!'
                && (p.Pos + 1 >= p.Text.Length || p.Text[p.Pos + 1] != '='))
            {
                p.Pos++;
                Node operand = ParseUnary(p);
                if (operand.Type != DslValueType.Bool)
                    throw p.Fail(opPos, $"'!' requires a Bool operand, got {operand.Type}");
                return AddUnary(p, opPos, "not", operand, DslValueType.Bool);
            }
            if (p.Pos < p.Text.Length && p.Text[p.Pos] == '-')
            {
                p.Pos++;
                Node operand = ParseUnary(p);
                if (operand.Type != DslValueType.Int && operand.Type != DslValueType.Fixed)
                    throw p.Fail(opPos, $"unary '-' requires an Int or Fixed operand, got {operand.Type}");
                return AddUnary(p, opPos, "neg", operand, operand.Type);
            }
            return ParsePrimary(p);
        }

        private static Node ParsePrimary(P p)
        {
            p.SkipWs();
            if (p.Pos >= p.Text.Length)
                throw p.Fail(p.Pos, "unexpected end of expression");
            int startPos = p.Pos;
            char c = p.Text[p.Pos];

            if (c == '(')
            {
                p.Pos++;
                Node inner = ParseOr(p);
                if (!p.Match(")"))
                    throw p.Fail(p.Pos, "expected ')'");
                return inner; // grouping adds no node and no depth
            }

            if (IsDigit(c))
                return ParseNumber(p);

            if (IsIdentStart(c))
                return ParseIdentifier(p);

            throw p.Fail(startPos, $"unexpected character '{c}'");
        }

        /// <summary>Int literal <c>123</c> or Fixed literal <c>1.5</c>. The Fixed conversion is pure integer/long
        /// arithmetic (split on '.', round-half-up over ≤5 fraction digits) — never floating-point parsing.
        /// Range note: literals lex as POSITIVE magnitudes and unary '-' applies as a separate node AFTER these
        /// bounds checks, so the most-negative values (int.MinValue = -2147483648; the Fixed minimum -32768.0)
        /// are deliberately unrepresentable as literals — their positive magnitudes reject here first (pinned
        /// by ExprParserTests).</summary>
        private static Node ParseNumber(P p)
        {
            int start = p.Pos;
            long intPart = 0;
            while (p.Pos < p.Text.Length && IsDigit(p.Text[p.Pos]))
            {
                intPart = intPart * 10 + (p.Text[p.Pos] - '0');
                if (intPart > int.MaxValue)
                    throw p.Fail(start, "integer literal is out of range");
                p.Pos++;
            }

            bool isFixed = p.Pos + 1 < p.Text.Length && p.Text[p.Pos] == '.' && IsDigit(p.Text[p.Pos + 1]);
            if (!isFixed)
            {
                if (p.Pos < p.Text.Length && p.Text[p.Pos] == '.')
                    throw p.Fail(p.Pos, "a decimal point must be followed by at least one digit");
                return AddLeaf(p, start, new ExprLiteralNode { ValueType = DslValueType.Int, Raw = (int)intPart }, DslValueType.Int);
            }

            p.Pos++; // consume '.'
            long frac = 0, denom = 1;
            int fracDigits = 0;
            while (p.Pos < p.Text.Length && IsDigit(p.Text[p.Pos]))
            {
                fracDigits++;
                if (fracDigits > 5)
                    throw p.Fail(start, "a Fixed literal supports at most 5 fraction digits");
                frac = frac * 10 + (p.Text[p.Pos] - '0');
                denom *= 10;
                p.Pos++;
            }
            if (p.Pos < p.Text.Length && p.Text[p.Pos] == '.')
                throw p.Fail(p.Pos, "a number may contain only one decimal point");
            if (intPart > 32767)
                throw p.Fail(start, "Fixed literal is out of the 16.16 range [0, 32768)");

            long raw = intPart * Fixed.ONE + (frac * Fixed.ONE + denom / 2) / denom; // round-half-up, pure long math
            if (raw > int.MaxValue)
                throw p.Fail(start, "Fixed literal is out of the 16.16 range");
            return AddLeaf(p, start, new ExprLiteralNode { ValueType = DslValueType.Fixed, Raw = (int)raw }, DslValueType.Fixed);
        }

        private static Node ParseIdentifier(P p)
        {
            int start = p.Pos;
            while (p.Pos < p.Text.Length && IsIdentChar(p.Text[p.Pos]))
                p.Pos++;
            string name = p.Text.Substring(start, p.Pos - start);

            if (name == "true")
                return AddLeaf(p, start, new ExprLiteralNode { ValueType = DslValueType.Bool, Raw = 1 }, DslValueType.Bool);
            if (name == "false")
                return AddLeaf(p, start, new ExprLiteralNode { ValueType = DslValueType.Bool, Raw = 0 }, DslValueType.Bool);

            // Story 7.6 — `length(arr)`: the array-length built-in takes an ARRAY NAME (not an expression).
            // A call named `length` is always the built-in (the grammar is closed; a variable named `length`
            // stays readable bare).
            if (name == "length" && p.Peek() == '(')
                return ParseArrayLen(p, start);

            // Story 7.5 — `event.<name>`: the event-parameter read. Only the `event.` prefix routes here (a
            // declared variable named "event" without a following '.' still reads as a variable, so no legacy
            // text changes meaning); the '.' must follow IMMEDIATELY (no whitespace) so `event . x` stays a
            // located syntax error rather than a surprising member access.
            if (name == "event" && p.Pos < p.Text.Length && p.Text[p.Pos] == '.')
            {
                p.Pos++; // consume '.'
                if (p.Pos >= p.Text.Length || !IsIdentStart(p.Text[p.Pos]))
                    throw p.Fail(p.Pos, "expected an event parameter name after 'event.'");
                int pnStart = p.Pos;
                while (p.Pos < p.Text.Length && IsIdentChar(p.Text[p.Pos]))
                    p.Pos++;
                string paramName = p.Text.Substring(pnStart, p.Pos - pnStart);
                if (p.EventParams is null)
                    throw p.Fail(start, $"'event.{paramName}' is not available here — event parameter reads compile only for a trigger subscribed to exactly one event that declares parameters");
                if (!p.EventParams.TryGetValue(paramName, out var pd))
                    throw p.Fail(start, $"the subscribed event declares no parameter '{paramName}'");
                // Ref-typed payload params (EntityRef/FactionRef) already SURFACE Int in the map — the one
                // sanctioned ref→Int surface (opaque raw handles; no ref algebra).
                return AddLeaf(p, start, new ExprEventParamNode { Name = paramName }, pd.Type);
            }

            if (p.Peek() == '(')
                return ParseCall(p, start, name);

            // Declared-variable read: `name`, per-player `name[k]` (integer-literal slot), or — Story 7.6 —
            // Array-declared `name[expr]` (declared-SCOPE/TYPE-driven disambiguation, per the 7.4 design note).
            if (!p.Vars.TryGetValue(name, out var decl))
                throw p.Fail(start, $"undeclared variable '{name}'");
            if (decl.Type == DslValueType.Array)
                return ParseArrayGet(p, start, name);
            if (decl.Type is DslValueType.EntityRef or DslValueType.FactionRef or DslValueType.TimerRef)
                throw p.Fail(start, $"variable '{name}' is {decl.Type}-typed and cannot be read in an expression");

            int slot = -1;
            p.SkipWs();
            if (p.Pos < p.Text.Length && p.Text[p.Pos] == '[')
            {
                int bracketPos = p.Pos;
                p.Pos++;
                p.SkipWs();
                if (p.Pos >= p.Text.Length || !IsDigit(p.Text[p.Pos]))
                    throw p.Fail(p.Pos, "expected an integer-literal player slot after '['");
                int slotStart = p.Pos;
                long k = 0;
                while (p.Pos < p.Text.Length && IsDigit(p.Text[p.Pos]))
                {
                    // Stop accumulating once k is already out of slot range — the value no longer matters, and the
                    // reject below quotes the AUTHORED digits verbatim (never a saturated stand-in like "8").
                    if (k < DslVarTable.PlayerSlots) k = k * 10 + (p.Text[p.Pos] - '0');
                    p.Pos++;
                }
                string slotText = p.Text.Substring(slotStart, p.Pos - slotStart);
                if (!p.Match("]"))
                    throw p.Fail(p.Pos, "expected ']'");
                if (k >= DslVarTable.PlayerSlots)
                    throw p.Fail(bracketPos, $"player slot {slotText} is out of range [0,{DslVarTable.PlayerSlots})");
                slot = (int)k;
                if (decl.Scope != VarScope.PerPlayer)
                    throw p.Fail(bracketPos, $"variable '{name}' is {decl.Scope}-scoped; '[k]' slot reads are only valid on PerPlayer variables");
            }
            else if (decl.Scope == VarScope.PerPlayer)
            {
                throw p.Fail(start, $"variable '{name}' is PerPlayer-scoped; read it with a slot ('{name}[k]')");
            }

            return AddLeaf(p, start, new ExprVarNode { Name = name, Faction = slot }, decl.Type);
        }

        /// <summary>Story 7.6 — parse an Array-declared name's read: <c>name[expr]</c> (a FULL Int index
        /// expression — unlike PerPlayer's literal-only <c>name[k]</c>, disambiguated by the declared type).
        /// A bare Array read rejects, directing to the two legal forms.</summary>
        private static Node ParseArrayGet(P p, int namePos, string name)
        {
            if (p.ArrayDecls is null || !p.ArrayDecls.TryGetValue(name, out (DslValueType Elem, int Capacity) adecl))
                throw p.Fail(namePos, $"variable '{name}' is Array-typed but no array declaration is available (declare it as an Array-typed Global variable)");
            p.SkipWs();
            if (p.Pos >= p.Text.Length || p.Text[p.Pos] != '[')
                throw p.Fail(namePos, $"variable '{name}' is Array-typed; read it via '{name}[i]' or 'length({name})', never bare");
            p.Pos++; // consume '['
            Node index = ParseOr(p);
            if (!p.Match("]"))
                throw p.Fail(p.Pos, "expected ']'");
            if (index.Type != DslValueType.Int)
                throw p.Fail(namePos, $"array index must be Int, got {index.Type}");

            int depth = index.Depth + 1;
            if (depth > ExprBounds.MaxExprDepth)
                throw p.Fail(namePos, $"expression depth {depth} exceeds ExprBounds.MaxExprDepth={ExprBounds.MaxExprDepth}");
            var node = new ExprArrayGetNode { Id = p.NextId++, Name = name };
            p.Graph.Nodes.Add(node);
            Wire(p, index, node.Id, TriggerGraph.ExprOperandPort0);
            return new Node(node.Id, adecl.Elem, depth);
        }

        /// <summary>Story 7.6 — parse <c>length(arr)</c>: the argument is a declared-Array NAME (an identifier,
        /// never an expression). Emits an <see cref="ExprArrayLenNode"/> leaf (Int).</summary>
        private static Node ParseArrayLen(P p, int namePos)
        {
            if (!p.Match("("))
                throw p.Fail(p.Pos, "expected '('"); // unreachable (caller peeked), kept fail-closed
            p.SkipWs();
            int argStart = p.Pos;
            if (p.Pos >= p.Text.Length || !IsIdentStart(p.Text[p.Pos]))
                throw p.Fail(p.Pos, "length(arr) takes a declared Array variable name");
            while (p.Pos < p.Text.Length && IsIdentChar(p.Text[p.Pos]))
                p.Pos++;
            string arg = p.Text.Substring(argStart, p.Pos - argStart);
            if (!p.Match(")"))
                throw p.Fail(p.Pos, "expected ')' — length takes exactly 1 argument");
            if (p.ArrayDecls is null || !p.ArrayDecls.TryGetValue(arg, out _))
                throw p.Fail(argStart, $"length(arr) requires a declared Array variable, but '{arg}' is not one");
            return AddLeaf(p, namePos, new ExprArrayLenNode { Name = arg }, DslValueType.Int);
        }

        private static Node ParseCall(P p, int namePos, string fn)
        {
            int arity = fn switch
            {
                "count" => 1, "abs" => 1,
                "distance" => 2, "min" => 2, "max" => 2,
                _ => -1,
            };
            if (arity < 0)
                throw p.Fail(namePos, $"unknown function '{fn}' (built-ins: count/distance/min/max/abs)");

            if (!p.Match("("))
                throw p.Fail(p.Pos, "expected '('"); // unreachable (caller peeked), kept fail-closed
            var args = new List<Node>(arity);
            for (int i = 0; i < arity; i++)
            {
                if (i > 0 && !p.Match(","))
                    throw p.Fail(p.Pos, $"'{fn}' takes {arity} argument(s) — expected ','");
                args.Add(ParseOr(p));
            }
            if (!p.Match(")"))
                throw p.Fail(p.Pos, $"expected ')' — '{fn}' takes exactly {arity} argument(s)");

            DslValueType result;
            switch (fn)
            {
                case "count":
                    if (args[0].Type != DslValueType.Int)
                        throw p.Fail(namePos, $"count(faction) requires an Int argument, got {args[0].Type}");
                    result = DslValueType.Int;
                    break;
                case "distance":
                    if (args[0].Type != DslValueType.Point || args[1].Type != DslValueType.Point)
                        throw p.Fail(namePos, $"distance(a,b) requires two Point arguments, got {args[0].Type} and {args[1].Type}");
                    result = DslValueType.Fixed;
                    break;
                case "min": case "max":
                {
                    bool bothInt   = args[0].Type == DslValueType.Int   && args[1].Type == DslValueType.Int;
                    bool bothFixed = args[0].Type == DslValueType.Fixed && args[1].Type == DslValueType.Fixed;
                    if (!bothInt && !bothFixed)
                        throw p.Fail(namePos, $"{fn}(a,b) requires both arguments Int or both Fixed, got {args[0].Type} and {args[1].Type}");
                    result = args[0].Type;
                    break;
                }
                default: // "abs"
                    if (args[0].Type != DslValueType.Int && args[0].Type != DslValueType.Fixed)
                        throw p.Fail(namePos, $"abs(a) requires an Int or Fixed argument, got {args[0].Type}");
                    result = args[0].Type;
                    break;
            }

            int maxChild = 0;
            foreach (Node a in args)
                if (a.Depth > maxChild) maxChild = a.Depth;
            int depth = maxChild + 1;
            if (depth > ExprBounds.MaxExprDepth)
                throw p.Fail(namePos, $"expression depth {depth} exceeds ExprBounds.MaxExprDepth={ExprBounds.MaxExprDepth}");

            var node = new ExprCallNode { Id = p.NextId++, Fn = fn };
            p.Graph.Nodes.Add(node);
            for (int i = 0; i < args.Count; i++)
                Wire(p, args[i], node.Id, i);
            return new Node(node.Id, result, depth);
        }
    }
}
