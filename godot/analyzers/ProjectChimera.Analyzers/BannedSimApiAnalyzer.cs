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
    ///         fields, parameters and type arguments). RS0030's <c>T:System.Single</c> only fires on member
    ///         access, never on declarations (roslyn-analyzers #7371), so this is the real coverage.</item>
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
            description: "The deterministic simulation must not use float/double for gameplay state. Use Fixed. The off-the-shelf banned-API analyzer cannot flag the primitive keyword on declarations; this rule does.",
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

        /// <inheritdoc />
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(
                FloatPrimitiveRule,
                DictionaryEnumerationRule,
                UnstableSortRule,
                MagicCapLiteralRule,
                FromFloatOutsideConverterRule);

        /// <inheritdoc />
        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzePredefinedType, SyntaxKind.PredefinedType);
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
            if (IsUnorderedInterface(type))
                return true;
            foreach (INamedTypeSymbol i in type.AllInterfaces)
                if (IsUnorderedInterface(i))
                    return true;
            return false;
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

            // CHM0003 — unstable Array.Sort / List<T>.Sort
            if (method.Name == "Sort"
                && ((ownerNs == "System" && owner.MetadataName == "Array")
                    || (ownerNs == "System.Collections.Generic" && owner.MetadataName == "List`1")))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(UnstableSortRule, node.GetLocation(), owner.Name));
                return;
            }

            // CHM0005 — Fixed.FromFloat / Fixed.ToFloat outside the FixedJsonConverter allow-list
            if ((method.Name == "FromFloat" || method.Name == "ToFloat")
                && owner.Name == "Fixed" && ownerNs == "ProjectChimera.Core"
                && !IsInsideAllowlistedConverter(node))
            {
                ctx.ReportDiagnostic(Diagnostic.Create(FromFloatOutsideConverterRule, node.GetLocation(), method.Name));
            }
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
            if (value > -CapLiteralThreshold && value < CapLiteralThreshold)
                return;
            if (IsInsideConstOrEnum(node))
                return;
            if (!IsCapContext(node))
                return;

            ctx.ReportDiagnostic(Diagnostic.Create(MagicCapLiteralRule, node.GetLocation(), value));
        }

        /// <summary>True when <paramref name="node"/> is enclosed by a type named <see cref="AllowlistedConverterTypeName"/>.</summary>
        private static bool IsInsideAllowlistedConverter(SyntaxNode node)
        {
            for (SyntaxNode? a = node.Parent; a is not null; a = a.Parent)
                if (a is TypeDeclarationSyntax t && t.Identifier.ValueText == AllowlistedConverterTypeName)
                    return true;
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
        private static bool IsCapContext(LiteralExpressionSyntax node)
        {
            SyntaxNode? parent = node.Parent;
            while (parent is ParenthesizedExpressionSyntax p)
                parent = p.Parent;

            if (parent is ArrayRankSpecifierSyntax)
                return true;

            if (parent is BinaryExpressionSyntax bin)
            {
                switch (bin.OperatorToken.Kind())
                {
                    case SyntaxKind.LessThanToken:
                    case SyntaxKind.LessThanEqualsToken:
                    case SyntaxKind.GreaterThanToken:
                    case SyntaxKind.GreaterThanEqualsToken:
                        return true;
                }
            }
            return false;
        }
    }
}
