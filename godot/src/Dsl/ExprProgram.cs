#nullable enable
using ProjectChimera.Core; // Fixed, FixedVec3

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.4 — the small Dsl-owned seam through which a compiled expression reaches the live world. Implemented
    /// by <c>ScenarioDirector</c> (ascending-id scan, the existing CountAlive semantics). Kept minimal so the Dsl
    /// layer never references Core stores directly.
    /// </summary>
    public interface IExprWorld
    {
        /// <summary>Number of alive entities owned by the given faction slot (slot 0 = Player1). Deterministic
        /// ascending-id scan; an empty/unknown faction counts 0.</summary>
        int CountAlive(int factionSlot);
    }

    /// <summary>
    /// Story 7.4 — a compiled, pre-checked expression: a flat postfix op array evaluated over a PREALLOCATED value
    /// stack (zero per-tick heap allocation). Produced only by <see cref="ExprCompiler"/>, which has already
    /// type-checked, cost-capped, and rejected the statically-knowable failure modes — so <see cref="Eval"/> is
    /// TOTAL: it can never throw in the tick.
    ///
    /// Total runtime semantics (deterministic, WC3-lineage):
    ///   • a runtime divisor/mod-by-zero evaluates to 0 (Int and Fixed);
    ///   • <c>abs(int.MinValue)</c> → <c>int.MaxValue</c>;
    ///   • Int overflow wraps unchecked;
    ///   • expressions are pure (no side effects), so both branches of &amp;&amp;/|| may evaluate — no
    ///     short-circuit semantics are needed or provided.
    ///
    /// Values live on two parallel raw-int stacks: scalars use slot 0 only (Int = the value; Fixed = Fixed.Raw;
    /// Bool = 0/1); a Point occupies (raw X, raw Z) across both — consumed only by <c>distance</c>.
    /// </summary>
    public sealed class ExprProgram
    {
        /// <summary>The closed postfix opcode set. Ops that are raw-identical across Int and Fixed (add/sub/mod/
        /// neg/compares/min/max/abs — 16.16 raws order and add like ints) are shared; mul/div differ and are split.</summary>
        internal enum OpCode : byte
        {
            PushLit,   // push (A, B)
            PushVar,   // push DslVarTable.GetRaw(Name, A)
            Neg,       // unary minus (raw negate, unchecked)
            Not,       // boolean not
            Add,       // raw add (Int value / Fixed raw), unchecked wrap
            Sub,       // raw subtract, unchecked wrap
            Mod,       // raw remainder; divisor 0 (or the MinValue/-1 hardware trap) → 0
            MulInt,    // int multiply, unchecked wrap
            MulFix,    // 16.16 multiply (long intermediate, matches Fixed.operator*)
            DivInt,    // int divide; divisor 0 → 0; MinValue/-1 wraps to MinValue
            DivFix,    // 16.16 divide (long intermediate, matches Fixed.operator/); divisor 0 → 0
            Gt, Lt, Ge, Le, Eq, Ne, // raw compares → 0/1 (exact raws — no epsilon, per the 7.4 contract)
            And, Or,   // boolean and/or → 0/1
            Min, Max,  // raw min/max (valid for Int values and Fixed raws alike)
            Abs,       // raw abs; int.MinValue → int.MaxValue
            Count,     // pop faction slot, push IExprWorld.CountAlive (null world → 0)
            Distance,  // pop Point b, pop Point a, push FixedVec3.Distance((aX,0,aZ),(bX,0,bZ)).Raw
        }

        /// <summary>One postfix op. <see cref="Name"/> is only used by <see cref="OpCode.PushVar"/> (the variable
        /// name — a load-time string, never allocated in the tick); A/B are the literal raws or the faction slot.</summary>
        internal readonly struct Op
        {
            internal readonly OpCode Code;
            internal readonly int A;
            internal readonly int B;
            internal readonly string? Name;

            internal Op(OpCode code, int a = 0, int b = 0, string? name = null)
            {
                Code = code; A = a; B = b; Name = name;
            }
        }

        private readonly Op[]  _ops;
        private readonly int[] _stack0; // scalar raw / Point X raw
        private readonly int[] _stack1; // Point Z raw (0 for scalars)

        /// <summary>The expression's inferred result type (Int / Fixed / Bool — a root can never be Point).</summary>
        public DslValueType ResultType { get; }

        /// <summary>Number of postfix ops in the compiled program (≤ <see cref="ExprBounds.MaxExprOps"/>).</summary>
        public int OpCount => _ops.Length;

        internal ExprProgram(Op[] ops, int maxStack, DslValueType resultType)
        {
            _ops       = ops;
            _stack0    = new int[maxStack < 1 ? 1 : maxStack];
            _stack1    = new int[maxStack < 1 ? 1 : maxStack];
            ResultType = resultType;
        }

        /// <summary>
        /// Evaluate the program against the live variable store (and, for <c>count</c>, the world seam). Returns
        /// the result's raw int (Int value / Fixed.Raw / Bool 0-1). Zero heap allocation; never throws (see class
        /// remarks for the total runtime semantics).
        ///
        /// NON-REENTRANT and not thread-safe by construction: evaluation reuses this instance's preallocated
        /// value stacks (<c>_stack0</c>/<c>_stack1</c>), so a nested or concurrent Eval of the SAME program would
        /// corrupt them. Safe today because programs are evaluated only on the single-threaded sim tick and the
        /// one external seam (<see cref="IExprWorld.CountAlive"/>, a pure world scan) never re-enters Eval.
        /// </summary>
        public int Eval(DslVarTable vars, IExprWorld? world)
        {
            int[] s0 = _stack0;
            int[] s1 = _stack1;
            int sp = 0;

            for (int i = 0; i < _ops.Length; i++)
            {
                ref readonly Op op = ref _ops[i];
                switch (op.Code)
                {
                    case OpCode.PushLit:
                        s0[sp] = op.A; s1[sp] = op.B; sp++;
                        break;

                    case OpCode.PushVar:
                        vars.GetRaw(op.Name!, op.A, out int r0, out int r1);
                        s0[sp] = r0; s1[sp] = r1; sp++;
                        break;

                    case OpCode.Neg:
                        s0[sp - 1] = unchecked(-s0[sp - 1]);
                        break;

                    case OpCode.Not:
                        s0[sp - 1] = s0[sp - 1] == 0 ? 1 : 0;
                        break;

                    case OpCode.Add:
                        sp--; s0[sp - 1] = unchecked(s0[sp - 1] + s0[sp]);
                        break;

                    case OpCode.Sub:
                        sp--; s0[sp - 1] = unchecked(s0[sp - 1] - s0[sp]);
                        break;

                    case OpCode.Mod:
                    {
                        sp--;
                        int b = s0[sp], a = s0[sp - 1];
                        // b == -1 short-circuits to the mathematically-correct 0 AND sidesteps the
                        // int.MinValue % -1 hardware trap (OverflowException) — Eval must be total.
                        s0[sp - 1] = (b == 0 || b == -1) ? 0 : a % b;
                        break;
                    }

                    case OpCode.MulInt:
                        sp--; s0[sp - 1] = unchecked(s0[sp - 1] * s0[sp]);
                        break;

                    case OpCode.MulFix:
                        sp--; s0[sp - 1] = unchecked((int)(((long)s0[sp - 1] * s0[sp]) >> Fixed.FRACTIONAL_BITS));
                        break;

                    case OpCode.DivInt:
                    {
                        sp--;
                        int b = s0[sp], a = s0[sp - 1];
                        // MinValue / -1 overflows in hardware; the unchecked-wrap contract makes it MinValue.
                        s0[sp - 1] = b == 0 ? 0 : (a == int.MinValue && b == -1 ? int.MinValue : a / b);
                        break;
                    }

                    case OpCode.DivFix:
                    {
                        sp--;
                        int b = s0[sp], a = s0[sp - 1];
                        s0[sp - 1] = b == 0 ? 0 : unchecked((int)(((long)a << Fixed.FRACTIONAL_BITS) / b));
                        break;
                    }

                    case OpCode.Gt: sp--; s0[sp - 1] = s0[sp - 1] >  s0[sp] ? 1 : 0; break;
                    case OpCode.Lt: sp--; s0[sp - 1] = s0[sp - 1] <  s0[sp] ? 1 : 0; break;
                    case OpCode.Ge: sp--; s0[sp - 1] = s0[sp - 1] >= s0[sp] ? 1 : 0; break;
                    case OpCode.Le: sp--; s0[sp - 1] = s0[sp - 1] <= s0[sp] ? 1 : 0; break;
                    case OpCode.Eq: sp--; s0[sp - 1] = s0[sp - 1] == s0[sp] ? 1 : 0; break;
                    case OpCode.Ne: sp--; s0[sp - 1] = s0[sp - 1] != s0[sp] ? 1 : 0; break;

                    case OpCode.And: sp--; s0[sp - 1] = (s0[sp - 1] != 0 && s0[sp] != 0) ? 1 : 0; break;
                    case OpCode.Or:  sp--; s0[sp - 1] = (s0[sp - 1] != 0 || s0[sp] != 0) ? 1 : 0; break;

                    case OpCode.Min: sp--; if (s0[sp] < s0[sp - 1]) s0[sp - 1] = s0[sp]; break;
                    case OpCode.Max: sp--; if (s0[sp] > s0[sp - 1]) s0[sp - 1] = s0[sp]; break;

                    case OpCode.Abs:
                    {
                        int a = s0[sp - 1];
                        s0[sp - 1] = a == int.MinValue ? int.MaxValue : (a < 0 ? -a : a);
                        break;
                    }

                    case OpCode.Count:
                        s0[sp - 1] = world?.CountAlive(s0[sp - 1]) ?? 0;
                        s1[sp - 1] = 0;
                        break;

                    case OpCode.Distance:
                    {
                        sp--;
                        var a = new FixedVec3(Fixed.FromRaw(s0[sp - 1]), Fixed.Zero, Fixed.FromRaw(s1[sp - 1]));
                        var b = new FixedVec3(Fixed.FromRaw(s0[sp]),     Fixed.Zero, Fixed.FromRaw(s1[sp]));
                        s0[sp - 1] = FixedVec3.Distance(a, b).Raw; // exactly the FixedVec3 semantics over (X, 0, Z)
                        s1[sp - 1] = 0;
                        break;
                    }
                }
            }

            return s0[0];
        }
    }
}
