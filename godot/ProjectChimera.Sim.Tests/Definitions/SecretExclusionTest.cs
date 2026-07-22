#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 8.1 — the structural "no key ships in a build" guard. Godot packs only <c>res://</c> into the PCK, and
    /// secrets are <c>user://secrets</c>-rooted, so a stored key is structurally UNPACKABLE. This test enforces the
    /// strongest deterministic surface the headless xUnit harness can reach:
    /// <list type="number">
    ///   <item>the secret store is rooted under <c>user://secrets</c> in the Godot layer, never <c>res://</c>; and</item>
    ///   <item>the committed <c>res://</c> tree that Godot packs (<c>godot/src/**/*.cs</c> + <c>godot/scenes/*.tscn</c>
    ///         + text under <c>godot/resources/</c>) contains NO plaintext <c>[Export]</c> API-key field and NO
    ///         key-shaped plaintext literal — fail loudly if one is (re)introduced.</item>
    /// </list>
    /// A live PCK-export scan is outside this deterministic harness (a known surface boundary, not an intent gap); the
    /// committed-tree scan below is a superset of the specific leak vector this story closes (the removed
    /// <c>[Export]</c> key fields) and the rooting discipline covers everything else.
    /// </summary>
    public class SecretExclusionTest
    {
        // A key-shaped plaintext literal (Anthropic sk-ant-…, OpenRouter sk-or-…, generic sk-… of real length).
        // NOTE: mod.io keys are un-prefixed hex, indistinguishable from a hash/GUID by shape, so a value-scan for
        // them would false-positive heavily; the mod.io reintroduction vector is instead covered by the [Export]
        // field-name guard below plus the res:// rooting discipline. Documented boundary, not a silent gap.
        private static readonly Regex KeyLiteralPattern =
            new(@"sk-[A-Za-z0-9_-]{18,}", RegexOptions.Compiled);

        // An [Export] *declaration* of an API-key field. Spans the attribute→field gap across newlines (matches the
        // idiomatic multi-line `[Export]\n public string …` form as well as single-line), stopping before any
        // `;{}[` so it never runs past the member. Captures the field NAME; a secret-shaped name is decided in
        // IsSecretFieldName so a renamed reintroduction (…ApiKey/…Secret/…Token) is caught too — not only the two
        // literal names this story removed. Deliberately NOT broadened to bare "Key" (would flag the legitimate
        // non-secret [Export] NakamaKey with its committed default).
        private static readonly Regex ExportStringFieldPattern =
            new(@"\[Export\][^;{}\[]*?\bstring\s+(\w+)", RegexOptions.Compiled);

        private static readonly Regex SecretFieldNamePattern =
            new(@"(ApiKey|Secret|Token)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsSecretFieldName(string name) =>
            name is "AnthropicApiKey" or "ModIoApiKey" || SecretFieldNamePattern.IsMatch(name);

        [Fact]
        public void SecretStore_IsUserRooted_NeverResRooted()
        {
            string godot = LocateGodotRoot();

            // The Godot layer (SettingsPhase) is the single site that roots the store — it must globalize
            // user://secrets and must NEVER root secrets under res:// (which Godot packs into the PCK).
            string settingsPhase = File.ReadAllText(
                Path.Combine(godot, "src", "Core", "Bootstrap", "Phases", "SettingsPhase.cs"));

            // Bind the assertion to executable CODE, not prose: SettingsPhase's doc comment also contains the string
            // "user://secrets", so a bare Contains would stay green even if the real GlobalizePath call were typo'd
            // (e.g. "user://secret") — the exact "silently-ignored key" regression class. Strip full-line comments
            // first, then require the actual rooting call, so a folder-name typo in code fails this test.
            string code = StripLineComments(settingsPhase);
            Assert.Contains("GlobalizePath(\"user://secrets\")", code);

            // Belt-and-suspenders: no file in the committed source tree may root a secret path under res://secrets.
            foreach (string cs in EnumerateSource(godot))
            {
                string text = File.ReadAllText(cs);
                Assert.False(text.Contains("res://secrets"),
                    $"Secret path rooted under res:// (structurally packable into the PCK) in {cs}");
            }
        }

        [Fact]
        public void CommittedSourceTree_HasNoExportKeyField_AndNoKeyLiteral()
        {
            string godot = LocateGodotRoot();
            var offenders = new List<string>();

            // [Export] key-field scan: C# source only (that's where [Export] lives).
            foreach (string cs in EnumerateSource(godot))
            {
                string text = File.ReadAllText(cs);
                foreach (Match m in ExportStringFieldPattern.Matches(text))
                {
                    string name = m.Groups[1].Value;
                    if (IsSecretFieldName(name))
                        offenders.Add($"[Export] API-key field '{name}' in {cs}");
                }
            }

            // Key-shaped literal scan: every packable text file (src *.cs + scenes *.tscn + resources text), since
            // the PCK packs all of res://, not just src/scenes.
            foreach (string file in EnumeratePackableText(godot))
            {
                Match lit = KeyLiteralPattern.Match(File.ReadAllText(file));
                if (lit.Success)
                    offenders.Add($"key-shaped plaintext literal '{lit.Value}' in {file}");
            }

            Assert.True(offenders.Count == 0,
                "Committed res:// tree must contain no plaintext API-key [Export] field or key-shaped literal:\n  " +
                string.Join("\n  ", offenders));
        }

        // ── Guard self-test: the reintroduction detector must actually fire on the forms it claims to catch, and
        //    must NOT fire on the legitimate non-secret [Export] fields already in the tree. ──────────────────────

        [Theory]
        [InlineData("[Export] public string AnthropicApiKey { get; set; } = \"\";", true)]   // single-line, literal name
        [InlineData("[Export]\n        public string ModIoApiKey { get; set; } = \"\";", true)] // idiomatic multi-line
        [InlineData("[Export]\n    public string MyProviderApiKey { get; set; }", true)]       // renamed …ApiKey
        [InlineData("[Export] public string SessionToken { get; set; }", true)]                // …Token
        [InlineData("[Export] public string ClientSecret { get; set; }", true)]               // …Secret
        [InlineData("[Export] public string NakamaKey { get; set; } = \"defaultkey\";", false)] // legit non-secret
        [InlineData("[Export] public int ModIoGameId { get; set; } = 0;", false)]              // not a string
        [InlineData("// mentions AnthropicApiKey in a comment", false)]                        // prose, no [Export]
        public void ExportKeyFieldDetector_MatchesSecretDeclarations_NotBenignOnes(string source, bool expectFlagged)
        {
            bool flagged = false;
            foreach (Match m in ExportStringFieldPattern.Matches(source))
                if (IsSecretFieldName(m.Groups[1].Value))
                    flagged = true;

            Assert.Equal(expectFlagged, flagged);
        }

        [Theory]
        [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", true)]
        [InlineData("var x = \"sk-or-v1-0123456789abcdefghij\";", true)]
        [InlineData("sk-short", false)]        // too short
        [InlineData("no key here", false)]
        public void KeyLiteralDetector_MatchesRealisticKeys(string source, bool expectMatch)
        {
            Assert.Equal(expectMatch, KeyLiteralPattern.IsMatch(source));
        }

        [Fact]
        public void SecretStore_UsesKeyExtension_NotResPacked()
        {
            // The key files use the *.key extension that both .gitignore files exclude — confirms the sink is the
            // gitignored user:// rail, not a committed res:// resource.
            Assert.Equal(".key", FileSecretStore.KeyFileExtension);
        }

        // ── Repo-tree location + enumeration ───────────────────────────────────────────────

        /// <summary>Walk up from the test-assembly directory to the <c>godot/</c> dir (the one containing both
        /// <c>src</c> and <c>scenes</c>), mirroring <c>FactionValidatorTests.ResolveDataPath</c>'s walk-up.</summary>
        private static string LocateGodotRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "scenes")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate the godot/ root (a dir with both src/ and scenes/) above {AppContext.BaseDirectory}");
        }

        private static IEnumerable<string> EnumerateSource(string godot) =>
            Directory.EnumerateFiles(Path.Combine(godot, "src"), "*.cs", SearchOption.AllDirectories);

        // Text extensions that a hand-pasted key could realistically land in and that Godot packs from res://. Binary
        // resources (.ogg/.zip/.import) are skipped — reading them as text is pointless and slow.
        private static readonly HashSet<string> PackableTextExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".cs", ".tscn", ".json", ".tres", ".cfg", ".txt" };

        private static IEnumerable<string> EnumeratePackableText(string godot)
        {
            foreach (string cs in EnumerateSource(godot)) yield return cs;

            foreach (string sub in new[] { "scenes", "resources" })
            {
                string dir = Path.Combine(godot, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    if (PackableTextExtensions.Contains(Path.GetExtension(f)))
                        yield return f;
            }
        }

        // Drop full-line // comments (incl. /// doc comments) so a code assertion binds to executable code, not prose.
        // Deliberately line-based, NOT a mid-line // strip: the rooting call contains "user://secrets", whose "//"
        // would be mangled by a naive strip. A doc-comment line ("// … GlobalizePath(\"user://secrets\") …") starts
        // with // and is removed; the real code line ("var s = new FileSecretStore(ProjectSettings.Global…")) does not.
        private static string StripLineComments(string source)
        {
            var kept = new List<string>();
            foreach (string line in source.Split('\n'))
                if (!line.TrimStart().StartsWith("//"))
                    kept.Add(line);
            return string.Join("\n", kept);
        }
    }
}
