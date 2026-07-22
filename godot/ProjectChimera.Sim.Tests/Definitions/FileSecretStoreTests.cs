#nullable enable
using System;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 8.1 — the file-backed <see cref="FileSecretStore"/>, covering every row of the story I/O matrix:
    /// fresh read (absent dir ⇒ "", nothing written), Set-then-read round-trip, restart (a fresh store over the same
    /// dir), corrupt/unreadable file (fail-soft ⇒ ""), invalid keyId (path-traversal guard ⇒ throws), and the
    /// no-write-until-Set invariant. Godot-free / Tier-1, mirroring <c>HeroProfilePersistenceTests</c>'s temp-dir rail.
    /// </summary>
    public class FileSecretStoreTests
    {
        // ── Fresh read: absent secrets/ dir ⇒ "", dir NOT created, nothing written ─────────

        [Fact]
        public void Get_AbsentDir_ReturnsEmpty_CreatesNothing()
        {
            string dir = Path.Combine(Path.GetTempPath(), "chimera_secrets_absent_" + Guid.NewGuid().ToString("N"));
            var store = new FileSecretStore(dir);

            Assert.Equal("", store.Get("llm"));
            Assert.False(Directory.Exists(dir)); // Get must NOT lazily create the directory
        }

        [Fact]
        public void Get_ExistingEmptyDir_NoKeyFile_ReturnsEmpty_WritesNothing()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            Assert.Equal("", store.Get("llm"));
            Assert.Empty(Directory.GetFiles(dir.Path)); // no llm.key created on a read miss
        }

        // ── Set then read: value round-trips; secrets/<id>.key written ─────────────────────

        [Fact]
        public void SetThenGet_RoundTrips_WritesIdDotKeyFile()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            store.Set("llm", "sk-X");

            Assert.Equal("sk-X", store.Get("llm"));
            Assert.True(File.Exists(Path.Combine(dir.Path, "llm.key"))); // AC: user://secrets/llm.key is literal
        }

        [Fact]
        public void Set_TrimsValue_AndHasReflectsPresence()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            Assert.False(store.Has("llm"));      // absent
            store.Set("llm", "  sk-trim  \n");
            Assert.Equal("sk-trim", store.Get("llm")); // surrounding whitespace/newline trimmed
            Assert.True(store.Has("llm"));
        }

        // ── Restart: a NEW store over the same dir returns the persisted value ─────────────

        [Fact]
        public void Restart_NewStoreOverSameDir_ReturnsPersistedValue()
        {
            using var dir = new TempDir();
            new FileSecretStore(dir.Path).Set("llm", "sk-persist");

            var reopened = new FileSecretStore(dir.Path);
            Assert.Equal("sk-persist", reopened.Get("llm"));
        }

        // ── Corrupt/unreadable file: fail-soft ⇒ "" ────────────────────────────────────────

        [Fact]
        public void Get_EmptyFile_ReturnsEmpty_AndHasFalse()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "llm.key"), "   \n"); // whitespace-only ⇒ trims to ""
            var store = new FileSecretStore(dir.Path);

            Assert.Equal("", store.Get("llm"));
            Assert.False(store.Has("llm"));
        }

        // ── Invalid keyId: path-traversal guard ⇒ ArgumentException, touches nothing ───────

        [Theory]
        [InlineData("../evil")]
        [InlineData("a/b")]
        [InlineData("a.b")]
        [InlineData("UPPER")]
        [InlineData("")]
        [InlineData("llm\n")] // trailing newline must be rejected (.NET '$' would have accepted it; \z does not)
        public void InvalidKeyId_GetAndSet_Throw(string badId)
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            Assert.Throws<ArgumentException>(() => store.Get(badId));
            Assert.Throws<ArgumentException>(() => store.Set(badId, "x"));
            Assert.Empty(Directory.GetFiles(dir.Path)); // nothing escaped the directory / was written
        }

        // ── Clear: removes the secret (no-op if absent) ────────────────────────────────────

        [Fact]
        public void Clear_RemovesSecret_NoopWhenAbsent()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            store.Clear("llm"); // no-op, no throw
            store.Set("llm", "sk-X");
            Assert.True(store.Has("llm"));

            store.Clear("llm");
            Assert.False(store.Has("llm"));
            Assert.Equal("", store.Get("llm"));
        }

        // ── Per-id isolation: distinct ids don't collide ───────────────────────────────────

        [Fact]
        public void DistinctIds_AreIndependentFiles()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            store.Set("llm", "sk-llm");
            store.Set("modio", "mod-key");

            Assert.Equal("sk-llm", store.Get("llm"));
            Assert.Equal("mod-key", store.Get("modio"));
            Assert.True(File.Exists(Path.Combine(dir.Path, "llm.key")));
            Assert.True(File.Exists(Path.Combine(dir.Path, "modio.key")));
        }

        // ── SecretIds: the canonical ids the bootstrap wiring seeds/reads must be stable AND valid store ids ──

        [Fact]
        public void SecretIds_MatchExpectedLiterals()
        {
            // Pin the values so the Godot-coupled (unit-untestable) seed/read sites in SettingsPhase /
            // TriggerEditorPhase / ContentBrowserPhase can never drift from the on-disk file names or each other.
            Assert.Equal("llm", SecretIds.Llm);
            Assert.Equal("modio", SecretIds.ModIo);
        }

        [Fact]
        public void SecretIds_AreValidStoreKeyIds()
        {
            // Every canonical id must satisfy the store's ^[a-z0-9_-]+$ rule — i.e. round-trip without throwing.
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);
            foreach (string id in new[] { SecretIds.Llm, SecretIds.ModIo })
            {
                store.Set(id, "v-" + id);
                Assert.Equal("v-" + id, store.Get(id));
            }
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_secrets_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
