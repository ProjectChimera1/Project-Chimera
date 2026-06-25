; Unshipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CHM0001 | Determinism | Warning | BannedSimApiAnalyzer: float/double primitive used in sim code
CHM0002 | Determinism | Warning | BannedSimApiAnalyzer: Dictionary/HashSet enumeration driving sim order
CHM0003 | Determinism | Warning | BannedSimApiAnalyzer: unstable Array.Sort / List<T>.Sort in sim code
CHM0004 | Determinism | Warning | BannedSimApiAnalyzer: magic cap literal that is not a named constant
CHM0005 | Determinism | Warning | BannedSimApiAnalyzer: Fixed.FromFloat/ToFloat outside the FixedJsonConverter allow-list
CHM0006 | Determinism | Warning | BannedSimApiAnalyzer: float/double Parse/ToString in sim code (culture-nondeterministic)
