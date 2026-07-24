#nullable enable
using System;
using System.IO;
using System.Text;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.UGC
{
    /// <summary>
    /// Story 9.8 — per-scenario token persistence round-trip for <see cref="ProofOfPlayStore"/> (Godot-free half over
    /// an injected temp dir). Covers save→load, the absent/corrupt fail-soft rows, and the file-safe id sanitization.
    /// </summary>
    public class ProofOfPlayStoreTests
    {
        private static readonly byte[] Key = Encoding.UTF8.GetBytes("store-test-key");

        private static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "chimera_pop_" + Guid.NewGuid().ToString("N"));
            return d; // created lazily by the store on first Save
        }

        [Fact]
        public void SaveThenTryLoad_RoundTripsAllFields()
        {
            string dir = NewTempDir();
            try
            {
                var store = new ProofOfPlayStore(dir);
                var token = ProofOfPlaySigner.Create(0xFEEDFACE, "win", "2026-07-24T12:00:00Z", "my-map", Key);
                store.Save("my-map", token);

                Assert.True(store.TryLoad("my-map", out var loaded));
                Assert.NotNull(loaded);
                Assert.Equal(token.ScenarioHash, loaded!.ScenarioHash);
                Assert.Equal(token.Outcome,      loaded.Outcome);
                Assert.Equal(token.MintedAt,     loaded.MintedAt);
                Assert.Equal(token.Signature,    loaded.Signature);
                Assert.Equal(token.ScenarioId,   loaded.ScenarioId);
                // The loaded token still verifies (bytes survived serialization).
                Assert.True(ProofOfPlaySigner.Verify(loaded, Key));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TryLoad_AbsentToken_ReturnsFalseNoThrow()
        {
            var store = new ProofOfPlayStore(NewTempDir()); // dir never created
            Assert.False(store.TryLoad("nope", out var loaded));
            Assert.Null(loaded);
        }

        [Fact]
        public void TryLoad_CorruptFile_FailsSoft()
        {
            string dir = NewTempDir();
            try
            {
                // Save a valid token (creates the file with whatever the store's naming scheme is), then corrupt that
                // exact file — robust to the P8 filename disambiguation scheme.
                var store = new ProofOfPlayStore(dir);
                store.Save("bad-map", ProofOfPlaySigner.Create(1, "win", "t", "bad-map", Key));
                File.WriteAllText(Directory.GetFiles(dir)[0], "{ this is not json");

                Assert.False(store.TryLoad("bad-map", out var loaded));
                Assert.Null(loaded);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Save_DistinctIdsSharingSanitizedStem_MapToDistinctFiles()
        {
            string dir = NewTempDir();
            try
            {
                // "My-Map" and "My Map" both sanitize to the stem "my-map"; the raw-id hash suffix must keep them apart
                // so one scenario's token can never overwrite or cross-read another's (review P8).
                var store = new ProofOfPlayStore(dir);
                store.Save("My-Map", ProofOfPlaySigner.Create(0xA, "win", "t", "My-Map", Key));
                store.Save("My Map", ProofOfPlaySigner.Create(0xB, "win", "t", "My Map", Key));

                Assert.Equal(2, Directory.GetFiles(dir).Length);

                Assert.True(store.TryLoad("My-Map", out var a));
                Assert.True(store.TryLoad("My Map", out var b));
                Assert.Equal(ProofOfPlaySigner.HashToHex(0xA), a!.ScenarioHash);
                Assert.Equal(ProofOfPlaySigner.HashToHex(0xB), b!.ScenarioHash);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Save_SanitizesIdToFileSafeName_AndReloadsBySameId()
        {
            string dir = NewTempDir();
            try
            {
                var store = new ProofOfPlayStore(dir);
                var token = ProofOfPlaySigner.Create(1, "win", "t", "My Map/../Evil", Key);
                store.Save("My Map/../Evil", token);

                // A path-traversal / spaced id can never escape the directory: exactly one file inside, no parent write.
                string[] files = Directory.GetFiles(dir);
                Assert.Single(files);
                Assert.StartsWith(dir, Path.GetFullPath(files[0]));
                Assert.EndsWith(".json", files[0]);

                // The same raw id reloads it (sanitization is deterministic).
                Assert.True(store.TryLoad("My Map/../Evil", out var loaded));
                Assert.NotNull(loaded);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Save_Overwrites_SameId()
        {
            string dir = NewTempDir();
            try
            {
                var store = new ProofOfPlayStore(dir);
                store.Save("m", ProofOfPlaySigner.Create(0x1, "win", "t1", "m", Key));
                store.Save("m", ProofOfPlaySigner.Create(0x2, "win", "t2", "m", Key));

                Assert.Single(Directory.GetFiles(dir));
                Assert.True(store.TryLoad("m", out var loaded));
                Assert.Equal(ProofOfPlaySigner.HashToHex(0x2), loaded!.ScenarioHash);
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        }
    }
}
