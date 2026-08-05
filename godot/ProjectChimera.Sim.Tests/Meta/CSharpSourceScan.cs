#nullable enable
using System.Text;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Shared plumbing for the SOURCE-scanning meta guards (the ones that assert a house rule the compiler cannot
    /// see — DW-213's nullable-context guard, DW-501's reflection-probe adoption guard).
    ///
    /// <para>Every such guard needs the same two things: a way to blank out comments and string literals so a rule
    /// written ABOUT the banned idiom (in a doc comment, in a diagnostic message) is not reported as an instance OF
    /// it, and a way to turn a character index back into a line number for the failure message.</para>
    ///
    /// <para>Test-only infrastructure in the Tier-1 (Godot-free) assembly: it reads source text and touches no
    /// simulation code, so it can never move a checksum or a golden.</para>
    /// </summary>
    internal static class CSharpSourceScan
    {
        /// <summary>
        /// Blank out comments, char literals and (regular, verbatim, interpolated, raw) string bodies, replacing each
        /// removed character with a space and PRESERVING every newline — so the result is index-for-index aligned with
        /// the input and <see cref="LineOf"/> still reports accurate line numbers.
        /// </summary>
        public static string StripCommentsAndLiterals(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                // line comment
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                // block comment
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    {
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    if (i < text.Length) { sb.Append("  "); i += 2; }
                    continue;
                }
                // raw string literal ("""…""") — the closing run must be at least as long as the opening run
                if (c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
                {
                    int run = 0;
                    while (i + run < text.Length && text[i + run] == '"') run++;
                    for (int k = 0; k < run; k++) { sb.Append(' '); i++; }
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            int close = 0;
                            while (i + close < text.Length && text[i + close] == '"') close++;
                            for (int k = 0; k < close; k++) { sb.Append(' '); i++; }
                            if (close >= run) break;
                            continue;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    continue;
                }
                // verbatim string (@"…" / $@"…" / @$"…") — "" is an escaped quote, backslash is literal
                if (c == '@' && i + 1 < text.Length && (text[i + 1] == '"' || (text[i + 1] == '$' && i + 2 < text.Length && text[i + 2] == '"')))
                {
                    sb.Append(' '); i++;                                  // '@'
                    if (text[i] == '$') { sb.Append(' '); i++; }          // '$'
                    sb.Append(' '); i++;                                  // opening quote
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append("  "); i += 2; continue; }
                            sb.Append(' '); i++; break;
                        }
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    continue;
                }
                // regular / interpolated string
                if (c == '"' || (c == '$' && i + 1 < text.Length && text[i + 1] == '"'))
                {
                    if (c == '$') { sb.Append(' '); i++; }
                    sb.Append(' '); i++;                                  // opening quote
                    while (i < text.Length && text[i] != '"' && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append("  "); i += 2; continue; }
                        sb.Append(' '); i++;
                    }
                    if (i < text.Length && text[i] == '"') { sb.Append(' '); i++; }
                    continue;
                }
                // char literal
                if (c == '\'')
                {
                    sb.Append(' '); i++;
                    while (i < text.Length && text[i] != '\'' && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append("  "); i += 2; continue; }
                        sb.Append(' '); i++;
                    }
                    if (i < text.Length && text[i] == '\'') { sb.Append(' '); i++; }
                    continue;
                }

                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        /// <summary>The 1-based line number containing character <paramref name="index"/> of <paramref name="text"/>.</summary>
        public static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') line++;
            return line;
        }
    }
}
