#nullable enable
using System;
using System.IO;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 8.1 — <see cref="SecretMigration.MigrateLegacyKey"/> covers the migrate branch (store empty + legacy
    /// non-empty ⇒ copied, returns true) and every no-op branch (store already set, legacy empty/whitespace/null ⇒
    /// false, store untouched). Godot-free / Tier-1.
    /// </summary>
    public class SecretMigrationTests
    {
        [Fact]
        public void Migrate_EmptyStore_NonEmptyLegacy_CopiesAndReturnsTrue()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            bool migrated = SecretMigration.MigrateLegacyKey(store, "llm", "sk-legacy");

            Assert.True(migrated);
            Assert.Equal("sk-legacy", store.Get("llm"));
        }

        [Fact]
        public void Migrate_StoreAlreadySet_IsNoop_ReturnsFalse_PreservesExisting()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);
            store.Set("llm", "sk-existing");

            bool migrated = SecretMigration.MigrateLegacyKey(store, "llm", "sk-legacy");

            Assert.False(migrated);
            Assert.Equal("sk-existing", store.Get("llm")); // legacy did NOT overwrite the store
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Migrate_EmptyOrNullLegacy_IsNoop_ReturnsFalse(string? legacy)
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            bool migrated = SecretMigration.MigrateLegacyKey(store, "llm", legacy);

            Assert.False(migrated);
            Assert.False(store.Has("llm"));
            Assert.Empty(Directory.GetFiles(dir.Path)); // no key file written on a no-op
        }

        [Fact]
        public void Migrate_IsIdempotent_SecondCallReturnsFalse()
        {
            using var dir = new TempDir();
            var store = new FileSecretStore(dir.Path);

            Assert.True(SecretMigration.MigrateLegacyKey(store, "llm", "sk-once"));
            Assert.False(SecretMigration.MigrateLegacyKey(store, "llm", "sk-once")); // store now owns it
        }

        private sealed class TempDir : IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_secretmig_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
