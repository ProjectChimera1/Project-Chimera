using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ProjectChimera.Analyzers
{
    /// <summary>
    /// Story 1.10b / AR-36 — the custom determinism analyzer over the Godot-free sim source set. Covers the
    /// rules the off-the-shelf <c>Microsoft.CodeAnalysis.BannedApiAnalyzers</c> (RS0030) cannot express because
    /// they require syntax/semantic analysis rather than a banned-symbol table:
    /// <list type="bullet">
    ///   <item>CHM0001 — the true <c>float</c>/<c>double</c> primitive ban (the keyword on declarations, casts,
    ///         fields, parameters and type arguments). It also covers the fully-qualified <c>System.Single</c>/
    ///         <c>System.Double</c> (and bare <c>Single</c>/<c>Double</c>) type references and <c>var</c>-inferred
    ///         float/double locals — the qualified/inferred forms the keyword path cannot see. RS0030's
    ///         <c>T:System.Single</c> only fires on member access, never on declarations (roslyn-analyzers #7371),
    ///         so this is the real coverage.</item>
    ///   <item>CHM0002 — <c>Dictionary</c>/<c>HashSet</c> enumeration driving sim order (S-CORE-1). Iteration
    ///         order over a hashed collection is nondeterministic; sim must iterate ascending id.</item>
    ///   <item>CHM0003 — unstable <c>Array.Sort</c> / <c>List&lt;T&gt;.Sort</c> (S-CORE-2). Equal elements may be
    ///         reordered run-to-run; sim sorts need a total, tie-broken order.</item>
    ///   <item>CHM0004 — a magic "cap" literal used as a relational bound or array size that is not a named
    ///         constant (S-CON-2 / S-FX-5). Heuristic and advisory.</item>
    ///   <item>CHM0005 — <c>Fixed.FromFloat</c>/<c>Fixed.ToFloat</c> outside the single AR-14 quantization
    ///         boundary <c>FixedJsonConverter</c> (S-FX-4). Advisory: the existing load-time static-constant
    ///         sites are D2 "Fixed end-to-end" debt. This rule is the custom-analyzer home for the float↔Fixed
    ///         conversion ban specifically so the off-the-shelf RS0030 set stays a clean zero-baseline set the
    ///         release gate can <c>-warnaserror</c> without tripping on that ~95-site debt.</item>
    /// </list>
    /// All rules default to <see cref="DiagnosticSeverity.Warning"/> (advisory on master); the release branch
    /// escalates the zero-baseline rule set via <c>WarningsAsErrors</c>. See the story's ratchet table.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BannedSimApiAnalyzer : DiagnosticAnalyzer
    {
        /// <summary>Diagnostic category for every rule this analyzer raises.</summary>
        public const string Category = "Determinism";

        /// <summary>The one type permitted to call <c>Fixed.FromFloat</c>/<c>ToFloat</c> — the AR-14 boundary.</summary>
        public const string AllowlistedConverterTypeName = "FixedJsonConverter";

        /// <summary>
        /// The namespace of the one permitted converter. The CHM0005 allow-list is anchored to it so a type that
        /// merely shares the name <see cref="AllowlistedConverterTypeName"/> in another namespace (UGC, a test
        /// double, an accidental dupe) cannot silently exempt itself from the float↔Fixed conversion ban.
        /// </summary>
        public const string AllowlistedConverterNamespace = "ProjectChimera.Core.Definitions";

        /// <summary>Integer literals with magnitude below this are never treated as a "cap" (CHM0004).</summary>
        internal const long CapLiteralThreshold = 8;

        private const string HelpLinkBase =
            "https://github.com/ProjectChimera/determinism#"; // anchors are documentation-only

        internal static readonly DiagnosticDescriptor FloatPrimitiveRule = new(
            id: "CHM0001",
            title: "float/double primitive used in sim code",
            messageFormat: "Sim code uses the '{0}' primitive — gameplay magnitudes must use the deterministic Fixed (16.16) type. float/double arithmetic diverges across machines and silently desyncs lockstep (NFR-4 / S-CORE-3).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The deterministic simulation must not use float/double for gameplay state. Use Fixed. The off-the-shelf banned-API analyzer cannot flag the primitive keyword on declarations; this rule does, and also covers System.Single/System.Double type references and var-inferred float/double.",
            helpLinkUri: HelpLinkBase + "chm0001");

        internal static readonly DiagnosticDescriptor DictionaryEnumerationRule = new(
            id: "CHM0002",
            title: "Dictionary/HashSet enumeration in sim code",
            messageFormat: "Sim code enumerates '{0}', whose iteration order is nondeterministic. Iterate ascending id (or a sorted view) so the tick order is identical on every peer (S-CORE-1).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "foreach over a Dictionary/HashSet (or IDictionary/ISet) yields elements in hash order, which is not stable across runs/peers and corrupts deterministic sim order.",
            helpLinkUri: HelpLinkBase + "chm0002");

        internal static readonly DiagnosticDescriptor UnstableSortRule = new(
            id: "CHM0003",
            title: "unstable sort in sim code",
            messageFormat: "Sim code calls '{0}.Sort', which is unstable: equal elements may be reordered run-to-run. Use a sort with a total tie-break (e.g. ThenBy(id)) so the result is deterministic (S-CORE-2).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Array.Sort and List<T>.Sort are introsort-based and unstable. In sim code an unstable sort over equal keys is a silent desync source.",
            helpLinkUri: HelpLinkBase + "chm0003");

        internal static readonly DiagnosticDescriptor MagicCapLiteralRule = new(
            id: "CHM0004",
            title: "magic cap literal in sim code",
            messageFormat: "The literal '{0}' is used as a bound/size but is not a named constant. Structural caps must be named constants (e.g. in SimConstants) folded into the rulesetHash, not bare literals (S-CON-2 / S-FX-5).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Heuristic, advisory: an integer literal at or above the cap threshold used as a relational bound or array size, outside a const/enum declaration, is likely an un-named structural cap.",
            helpLinkUri: HelpLinkBase + "chm0004");

        internal static readonly DiagnosticDescriptor FromFloatOutsideConverterRule = new(
            id: "CHM0005",
            title: "Fixed.FromFloat/ToFloat outside the converter",
            messageFormat: "'Fixed.{0}' is called outside FixedJsonConverter — the single AR-14 float<->Fixed quantization boundary. A float<->Fixed conversion in tick-reachable code reintroduces nondeterminism (S-FX-4).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Fixed.FromFloat/ToFloat may only run in FixedJsonConverter (load/save). Anywhere else is a determinism hazard. Existing load-time static-constant sites are advisory D2 debt.",
            helpLinkUri: HelpLinkBase + "chm0005");

        internal static readonly DiagnosticDescriptor FloatStringConversionRule = new(
            id: "CHM0006",
            title: "float/double Parse/ToString in sim code",
            messageFormat: "Sim code calls '{0}' — float/double<->string parse/format is culture- and rounding-nondeterministic (A17) and differs across machines/peers. Author thresholds as Fixed; never System.Single/Double.Parse or .ToString in sim (S-CORE-3).",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "float.Parse/double.Parse and float.ToString/double.ToString depend on the current culture and rounding mode, so their results diverge across peers. The off-the-shelf banned-API bare-name doc-IDs (M:System.Single.Parse/.ToString) resolve unreliably for these overloaded methods, so this rule detects them semantically instead. Advisory: the one existing site (the Fixed.ToString debug formatter) is D2 'Fixed end-to-end' debt; it cannot be release-gated zero-baseline, hence advisory like the sibling CHM0005.",
            helpLinkUri: HelpLinkBase + "chm0006");

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                FloatPrimitiveRule,
                DictionaryEnumerationRule,
                UnstableSortRule,
                MagicCapLiteralRule,
                FromFloatOutsideConverterRule,
                FloatStringConversionRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzePredefinedType, SyntaxKind.PredefinedType);
            context.RegisterSyntaxNodeAction(AnalyzeIdentifierName, SyntaxKind.IdentifierName);
            context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement);
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
            context.RegisterSyntaxNodeAction(AnalyzeNumericLiteral, SyntaxKind.NumericLiteralExpression);
        }

        // ── CHM0001 — float/double primitive keyword ──────────────────────────────────────────
        private static void AnalyzePredefinedType(SyntaxNodeAnalysisContext ctx)
        {
            var node = (PredefinedTypeSyntax)ctx.Node;
            SyntaxKind kw = node.Keyword.Kind();
            if (kw != SyntaxKind.FloatKeyword && kw != SyntaxKind.DoubleKeyword)
                return;

            // Skip `float.X` / `double.X` member-access receivers (e.g. float.Parse, double.MaxValue) — RS0030's
            // T:System.Single / T:System.Double already owns member access; reporting here would double up.
            if (node.Parent is MemberAccessExpressionSyntax ma && ma.Expression == node)
                return;

            ctx.ReportDiagnostic(Diagnostic.Create(FloatPrimitiveRule, node.GetLocation(), node.Keyword.ValueText));
        }

        // ── CHM0001 — System.Single/Double type references and var-inferred float/double ──────
        // The PredefinedType path above owns the `float`/`double` keyword; this closes the qualified
        // (`System.Single`) and inferred (`var x = 1f`) forms it cannot see. Cheap text prefilter first
        // ("var"/"Single"/"Double") so the semantic model is only consulted for the handful of candidates.
        private static void AnalyzeIdentifierName(SyntaxNodeAnalysisContext ctx)
        {
            var node = (IdentifierNameSyntax)ctx.Node;
            string text = node.Identifier.ValueText;

            if (text == "var")
            {
                if (!node.IsVar)
                    return;
                ITypeSymbol? inferred = ctx.SemanticModel.GetTypeInfo(node, ctx.CancellationToken).Type;
                if (inferred is null || inferred.TypeKind == TypeKind.Error)
                    return; // inference failure / error type — skip
                string? primitive = FloatOrDoubleName(inferred);
                if (primitive is not null)
                    ctx.ReportDiagnostic(Diagnostic.Create(FloatPrimitiveRule, node.GetLocation(), primitive));
                return;
            }

            if (text != "Single" && text != "Double")
                return;

            // Skip member access (`System.Single.Parse`, `Single.MaxValue`) — RS0030/CHM0006 own member access.
            if (node.Parent is MemberAccessExpressionSyntax)
                return;

            // Skip `nameof(Single)`/`nameof(Double)` — the type only produces a string; no float value is computed.
            if (IsInsideNameOf(node))
                return;

            if (ctx.SemanticModel.GetSymbolInfo(node, ctx.CancellationToken).Symbol is not INamedTypeSymbol nt)
                return;
            string? primitiveName = FloatOrDoubleName(nt);
            string ns = nt.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (primitiveName is not null && ns == "System")
                ctx.ReportDiagnostic(Diagnostic.Create(FloatPrimitiveRule, node.GetLocation(), primitiveName));
        }

        /// <summary>
        /// True when <paramref name="node"/> is the argument of a <c>nameof(...)</c> expression. <c>nameof</c> never
        /// evaluates its operand, so a <c>Single</c>/<c>Double</c> type name inside it is a string, not a float value.
        /// </summary>
        private static bool IsInsideNameOf(SyntaxNode node)
        {
            for (SyntaxNode? a = node.Parent; a is not null; a = a.Parent)
            {
                if (a is InvocationExpressionSyntax inv
                    && inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
                    return true;
                // A statement/member boundary means we are past any enclosing nameof — stop climbing.
                if (a is StatementSyntax || a is MemberDeclarationSyntax)
                    return false;
            }
            return false;
        }

        /// <summary>Returns "float"/"double" when <paramref name="t"/> is System.Single/System.Double, else null.</summary>
        private static string? FloatOrDoubleName(ITypeSymbol t)
        {
            if (t.SpecialType == SpecialType.System_Single)
                return "float";
            if (t.SpecialType == SpecialType.System_Double)
                return "double";
            return null;
        }

        // ── CHM0002 — Dictionary/HashSet enumeration ──────────────────────────────────────────
        private static void AnalyzeForEach(SyntaxNodeAnalysisContext ctx)
        {
            var node = (ForEachStatementSyntax)ctx.Node;
            ITypeSymbol? type = ctx.SemanticModel.GetTypeInfo(node.Expression, ctx.CancellationToken).Type;
            if (type is null || type.TypeKind == TypeKind.Error)
                return;
            if (!IsUnorderedCollection(type))
                return;

            ctx.ReportDiagnostic(Diagnostic.Create(DictionaryEnumerationRule, node.Expression.GetLocation(), type.Name));
        }

        private static bool IsUnorderedCollection(ITypeSymbol type)
        {
            if (IsDictionaryKeyOrValueCollection(type))
                return true;
            if (IsUnorderedInterface(type))
                return true;
            foreach (INamedTypeSymbol i in type.AllInterfaces)
                if (IsUnorderedInterface(i))
                    return true;
            return false;
        }

        /// <summary>
        /// True for <c>Dictionary&lt;,&gt;.KeyCollection</c>/<c>ValueCollection</c> — the projection you get from
        /// <c>dict.Keys</c>/<c>dict.Values</c>. Enumerating it yields hash order, so it is as unordered as the
        /// dictionary itself. Detected structurally: metadata name is <c>KeyCollection</c>/<c>ValueCollection</c>
        /// nested in the BCL <c>System.Collections.Generic.Dictionary`2</c> specifically — the namespace is verified
        /// so a user type named <c>Dictionary`2</c> (or the sorted/immutable dictionaries, which are deterministic)
        /// cannot be mistaken for it. <c>SortedDictionary`2</c> already differs by metadata name, so it is excluded too.
        /// </summary>
        private static bool IsDictionaryKeyOrValueCollection(ITypeSymbol type)
        {
            ITypeSymbol def = type.OriginalDefinition;
            string name = def.MetadataName;
            if (name != "KeyCollection" && name != "ValueCollection")
                return false;
            INamedTypeSymbol? container = def.ContainingType;
            if (container is null || container.MetadataName != "Dictionary`2")
                return false;
            return (container.ContainingNamespace?.ToDisplayString() ?? string.Empty) == "System.Collections.Generic";
        }

        private static bool IsUnorderedInterface(ITypeSymbol t)
        {
            ISymbol def = t.OriginalDefinition;
            string ns = def.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            string name = def.MetadataName;
            if (ns == "System.Collections.Generic")
                return name == "IDictionary`2" || name == "IReadOnlyDictionary`2" || name == "ISet`1";
            if (ns == "System.Collections")
                return name == "IDictionary";
            return false;
        }

        // ── CHM0003 (unstable sort) + CHM0005 (FromFloat/ToFloat) share invocation resolution ──
        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
        {
            var node = (InvocationExpressionSyntax)ctx.Node;
            if (ctx.SemanticModel.GetSymbolInfo(node, ctx.CancellationToken).Symbol is not IMethodSymbol method)
                return;

            INamedTypeSymbol? owner = method.ContainingType?.OriginalDefinition;
            if (owner is null)
                return;
            string ownerNs = owner.ContainingNamespace?.ToDisplayString() ?? string.Empty;

            // CHM0002 (beyond foreach) — GetEnumerator() or a non-ordering LINQ operator invoked on an unordered
            // receiver (Dictionary/HashSet/KeyCollection/ValueCollection). Cheap kind prefilter: the invocation must
            // be a member access so a receiver node exists; ordering operators (OrderBy/…) impose a deterministic
            // order and are exempt. Reports on the receiver so the location points at the collection (matches foreach).
            if (node.Expression is MemberAccessExpressionSyntax recvAccess)
            {
                bool isEnumeration =
                    method.Name == "GetEnumerator"
                    || (ownerNs == "System.Linq" && owner.MetadataName == "Enumerable"
                        && !IsOrderingOperator(method.Name)
                        && !IsOrderInsensitiveReducer(method.Name));
                if (isEnumeration)
                {
                    ITypeSymbol? recvType =
                        ctx.SemanticModel.GetTypeInfo(recvAccess.Expression, ctx.CancellationToken).Type;
                    if (recvType is not null && recvType.TypeKind != TypeKind.Error && IsUnorderedCollection(recvType))
                    {
                        ctx.ReportDiagnostic(Diagnostic.Create(
                            DictionaryEnumerationRule, recvAccess.Expression.GetLocation(), recvType.Name));
                        return;
                    }
                }
            }

            // CHM0003 — unstable Array.Sort / List<T>.Sort / Span<T>.Sort (MemoryExtensions). A Sort that carries an
            // IComparer/IComparer<T>/Comparison<T> argument is a developer-controlled total order and is NOT flagged —
            // this is how the two real total-order List.Sort(Comparison) sites clear without a suppression.
            if (method.Name == "Sort"
                && ((ownerNs == "System" && owner.MetadataName == "Array")
                    || (ownerNs == "System" && owner.MetadataName == "MemoryExtensions")
                    || (ownerNs == "System.Collections.Generic" && owner.MetadataName == "List`1")))
            {
                if (!HasComparerParameter(method))
                    ctx.ReportDiagnostic(Diagnostic.Create(UnstableSortRule, node.GetLocation(), owner.Name));
                return;
            }

            // CHM0005 — Fixed.FromFloat / Fixed.ToFloat outside the FixedJsonConverter allow-list
            if ((method.Name == "FromFloat" || method.Name == "ToFloat")
                && owner.Name == "Fixed" && ownerNs == "ProjectChimera.Core"
                && !IsInsideAllowlistedConverter(ctx, node))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(FromFloatOutsideConverterRule, node.GetLocation(), method.Name));
                return;
            }

            // CHM0006 — float/double .Parse / .ToString (culture/rounding-nondeterministic, A17). Detected
            // semantically because the off-the-shelf bare-name doc-ID bans (M:System.Single.Parse/.ToString)
            // resolve unreliably across compilations and cannot be release-gated zero-baseline (the Fixed.ToString
            // debug formatter is one legitimate site). Advisory — mirrors the float<->Fixed CHM0005 cadence.
            if ((method.Name == "Parse" || method.Name == "ToString")
                && ownerNs == "System"
                && (owner.MetadataName == "Single" || owner.MetadataName == "Double"))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    FloatStringConversionRule, node.GetLocation(), owner.Name + "." + method.Name));
            }
        }

        /// <summary>LINQ operators that impose a deterministic order and so are exempt from CHM0002.</summary>
        private static bool IsOrderingOperator(string name)
            => name == "OrderBy" || name == "OrderByDescending" || name == "Order" || name == "OrderDescending";

        /// <summary>
        /// LINQ reducers whose <em>result</em> does not depend on enumeration order (the sim layer is int/Fixed —
        /// float is banned by CHM0001 — so integer sum/min/max/average are order-invariant too). Flagging these on a
        /// Dictionary/HashSet is a false positive: the value is already deterministic. Order-exposing operators
        /// (<c>First</c>/<c>Last</c>/<c>ElementAt</c>/<c>Select</c>/<c>Aggregate</c>/<c>ToList</c>/…) are NOT listed and still fire.
        /// </summary>
        private static bool IsOrderInsensitiveReducer(string name)
            => name == "Count" || name == "LongCount" || name == "Any" || name == "All" || name == "Contains"
               || name == "Sum" || name == "Min" || name == "Max" || name == "Average"
               || name == "ToDictionary" || name == "ToHashSet";

        /// <summary>
        /// True when <paramref name="method"/> takes an <c>IComparer</c>/<c>IComparer&lt;T&gt;</c>/<c>Comparison&lt;T&gt;</c>
        /// parameter — a developer-controlled total order, which clears the CHM0003 unstable-sort ban.
        /// </summary>
        private static bool HasComparerParameter(IMethodSymbol method)
        {
            foreach (IParameterSymbol p in method.Parameters)
            {
                ITypeSymbol def = p.Type.OriginalDefinition;
                string ns = def.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                string name = def.MetadataName;
                if (ns == "System.Collections.Generic" && name == "IComparer`1")
                    return true;
                if (ns == "System.Collections" && name == "IComparer")
                    return true;
                if (ns == "System" && name == "Comparison`1")
                    return true;
            }
            return false;
        }

        // ── CHM0004 — magic cap literal ───────────────────────────────────────────────────────
        private static void AnalyzeNumericLiteral(SyntaxNodeAnalysisContext ctx)
        {
            var node = (LiteralExpressionSyntax)ctx.Node;

            // Integer literals only (skip float/double/decimal/uint/ulong — caps are int/long).
            long value;
            switch (node.Token.Value)
            {
                case int i: value = i; break;
                case long l: value = l; break;
                default: return;
            }

            // A leading unary minus is part of the bound value: `x < -64` is a cap of -64, not 64. Treat the unary
            // expression as the node whose context/parent decides cap-ness so the negated relational bound fires.
            SyntaxNode boundNode = node;
            if (node.Parent is PrefixUnaryExpressionSyntax neg
                && neg.IsKind(SyntaxKind.UnaryMinusExpression)
                && neg.Operand == node)
            {
                value = -value;
                boundNode = neg;
            }

            if (value > -CapLiteralThreshold && value < CapLiteralThreshold)
                return;
            if (IsInsideConstOrEnum(node))
                return;
            // A loop-condition relational bound (`for (…; i < 100; …)` / `while (i < 100)`) is control flow, not a
            // structural cap — the DW-6 false-positive class. Skip it.
            if (IsLoopConditionBound(boundNode))
                return;
            if (!IsCapContext(boundNode) && !IsStaticReadonlyFieldCap(boundNode))
                return;

            // Report at boundNode so a negated cap (`-64`) underlines the sign too, matching the reported value.
            ctx.ReportDiagnostic(Diagnostic.Create(MagicCapLiteralRule, boundNode.GetLocation(), value));
        }

        /// <summary>
        /// True when <paramref name="node"/> is lexically enclosed by the real AR-14 boundary type
        /// <see cref="AllowlistedConverterTypeName"/> in namespace <see cref="AllowlistedConverterNamespace"/>.
        /// The namespace is verified via the semantic model so an unrelated / UGC / test-double type that merely
        /// shares the name <c>FixedJsonConverter</c> in another namespace cannot silently exempt itself from CHM0005.
        /// </summary>
        private static bool IsInsideAllowlistedConverter(SyntaxNodeAnalysisContext ctx, SyntaxNode node)
        {
            for (SyntaxNode? a = node.Parent; a is not null; a = a.Parent)
            {
                if (a is TypeDeclarationSyntax t && t.Identifier.ValueText == AllowlistedConverterTypeName)
                {
                    INamedTypeSymbol? sym = ctx.SemanticModel.GetDeclaredSymbol(t, ctx.CancellationToken);
                    string ns = sym?.ContainingNamespace?.ToDisplayString() ?? string.Empty;
                    return ns == AllowlistedConverterNamespace;
                }
            }
            return false;
        }

        /// <summary>True when the literal is part of a <c>const</c> field/local or an enum member (a named constant).</summary>
        private static bool IsInsideConstOrEnum(SyntaxNode node)
        {
            for (SyntaxNode? a = node.Parent; a is not null; a = a.Parent)
            {
                switch (a)
                {
                    case EnumMemberDeclarationSyntax:
                        return true;
                    case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword):
                        return true;
                    case LocalDeclarationStatementSyntax ld when ld.Modifiers.Any(SyntaxKind.ConstKeyword):
                        return true;
                    // Reached an executable/member boundary without seeing const — stop climbing.
                    case BaseMethodDeclarationSyntax:
                    case AccessorDeclarationSyntax:
                    case BasePropertyDeclarationSyntax:
                        return false;
                }
            }
            return false;
        }

        /// <summary>True when the literal is used as a relational bound (&lt; &lt;= &gt; &gt;=) or an array size — a "cap".</summary>
        private static bool IsCapContext(SyntaxNode node)
        {
            SyntaxNode? parent = node.Parent;
            while (parent is ParenthesizedExpressionSyntax p)
                parent = p.Parent;

            if (parent is ArrayRankSpecifierSyntax)
                return true;

            if (parent is BinaryExpressionSyntax bin)
                return IsRelational(bin);
            return false;
        }

        /// <summary>True for the four relational operators (&lt; &lt;= &gt; &gt;=).</summary>
        private static bool IsRelational(BinaryExpressionSyntax bin)
        {
            switch (bin.OperatorToken.Kind())
            {
                case SyntaxKind.LessThanToken:
                case SyntaxKind.LessThanEqualsToken:
                case SyntaxKind.GreaterThanToken:
                case SyntaxKind.GreaterThanEqualsToken:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the literal's enclosing relational comparison is the controlling condition of a <c>for</c>/
        /// <c>while</c>/<c>do…while</c> loop (climbing through parentheses and <c>&amp;&amp;</c>/<c>||</c> compound
        /// conditions). Such a bound is loop control flow, not a structural cap, so CHM0004 must not flag it (DW-6).
        /// </summary>
        private static bool IsLoopConditionBound(SyntaxNode node)
        {
            SyntaxNode? p = node.Parent;
            while (p is ParenthesizedExpressionSyntax paren)
                p = paren.Parent;
            if (p is not BinaryExpressionSyntax bin || !IsRelational(bin))
                return false;

            SyntaxNode current = bin;
            SyntaxNode? parent = bin.Parent;
            while (parent is ParenthesizedExpressionSyntax
                   || (parent is BinaryExpressionSyntax pb
                       && (pb.IsKind(SyntaxKind.LogicalAndExpression) || pb.IsKind(SyntaxKind.LogicalOrExpression))))
            {
                current = parent;
                parent = parent.Parent;
            }

            return (parent is ForStatementSyntax f && f.Condition == current)
                || (parent is WhileStatementSyntax w && w.Condition == current)
                || (parent is DoStatementSyntax d && d.Condition == current);
        }

        /// <summary>
        /// True when the literal is the initializer of a <c>static readonly</c> field — a structural cap that escaped
        /// the named-constant channel (<c>const</c>/enum are already exempt via <see cref="IsInsideConstOrEnum"/>).
        /// </summary>
        private static bool IsStaticReadonlyFieldCap(SyntaxNode node)
        {
            if (node.Parent is not EqualsValueClauseSyntax eq || eq.Value != node)
                return false;
            if (eq.Parent is not VariableDeclaratorSyntax vd)
                return false;
            if (vd.Parent is not VariableDeclarationSyntax decl)
                return false;
            if (decl.Parent is not FieldDeclarationSyntax field)
                return false;
            return field.Modifiers.Any(SyntaxKind.StaticKeyword)
                && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
        }
    }
}
