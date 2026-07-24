#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The OFFLINE hero-persistence disk rail (Story 3.9, AR-12 M2 / AR-5). A Godot-free source over an INJECTED
    /// directory — the Godot layer globalizes <c>user://hero_profiles</c> (from <see cref="SettingsData.HeroProfileFolder"/>)
    /// and hands it in — mirroring <c>SettingsManager</c>'s <c>System.Text.Json</c> + <c>System.IO</c> pattern. Stores all
    /// saved heroes as ONE JSON list file (<c>profiles.json</c>) written in a byte-stable order (sorted by
    /// <see cref="PlayerProfile.ProfileId"/>), so the on-disk bytes are stable regardless of save order.
    ///
    /// <para>Fail-soft like <c>SettingsManager.Load</c>: a missing directory / missing / unparseable file makes
    /// <see cref="LoadAll"/> return an empty list — never a throw. Deterministic: <see cref="NextProfileId"/> derives the
    /// next id from the current store state (no wall-clock, no RNG, no <c>Guid</c>).</para>
    /// </summary>
    public sealed class LocalProfileSource : IProfileSource
    {
        /// <summary>The single list file inside the injected directory holding every saved profile.</summary>
        public const string ProfilesFileName = "profiles.json";

        private readonly string _directory;
        private readonly string _filePath;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented       = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>Construct the rail over <paramref name="directory"/> (an OS-absolute path; the Godot layer resolves
        /// <c>user://…</c> via <c>ProjectSettings.GlobalizePath</c> before passing it). The directory need not exist yet
        /// — it is created lazily on the first <see cref="Save"/>.</summary>
        public LocalProfileSource(string directory)
        {
            _directory = directory ?? "";
            _filePath  = Path.Combine(_directory, ProfilesFileName);
        }

        /// <summary>
        /// Load every saved profile. Fail-soft: a missing directory / file, or unparseable JSON, returns an EMPTY list
        /// (no throw) — the corrupt/absent-store row of the I/O matrix. The result is byte-stable-ordered on disk; the
        /// in-memory list preserves that file order.
        /// </summary>
        public IReadOnlyList<PlayerProfile> LoadAll()
        {
            if (!File.Exists(_filePath)) return Array.Empty<PlayerProfile>();
            try
            {
                string json = File.ReadAllText(_filePath);
                var list = JsonSerializer.Deserialize<List<PlayerProfile>>(json, JsonOptions);
                return list ?? new List<PlayerProfile>();
            }
            catch
            {
                // Fail-soft (mirrors SettingsManager.Load): a corrupt store must not crash the picker — treat as empty.
                return Array.Empty<PlayerProfile>();
            }
        }

        /// <summary>
        /// Save <paramref name="profile"/> — insert a new entry, or REPLACE the existing one with the same
        /// <see cref="PlayerProfile.ProfileId"/> (Overwrite). The directory is created if missing; the list is rewritten
        /// sorted by <see cref="PlayerProfile.ProfileId"/> (ordinal) so the file bytes are stable regardless of save order.
        /// </summary>
        public void Save(PlayerProfile profile)
        {
            if (profile == null) return;

            var list = new List<PlayerProfile>(LoadAll());
            int idx = IndexOf(list, profile.ProfileId);
            if (idx >= 0) list[idx] = profile; // replace-by-id (Overwrite)
            else list.Add(profile);            // new entry

            WriteAll(list);
        }

        /// <summary>Delete the profile with <paramref name="profileId"/> (no-op if absent). Rewrites the byte-stable list.</summary>
        public void Delete(string profileId)
        {
            var list = new List<PlayerProfile>(LoadAll());
            int idx = IndexOf(list, profileId);
            if (idx < 0) return;
            list.RemoveAt(idx);
            WriteAll(list);
        }

        /// <summary>
        /// The next stable profile id for <paramref name="heroDefId"/>, derived DETERMINISTICALLY from the current store
        /// state: <c>"{heroDefId}#{n}"</c> where <c>n</c> = one past the highest existing <c>#</c> suffix for that hero
        /// (starting at 1). No wall-clock / RNG / Guid — the same store state always yields the same next id.
        /// </summary>
        public string NextProfileId(string heroDefId)
        {
            string prefix = (heroDefId ?? "") + "#";
            int max = 0;
            foreach (PlayerProfile p in LoadAll())
            {
                if (p.ProfileId == null || !p.ProfileId.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string suffix = p.ProfileId.Substring(prefix.Length);
                if (int.TryParse(suffix, out int n) && n > max) max = n;
            }
            return prefix + (max + 1);
        }

        // Linear scan for a profile by id (no Dictionary — determinism rule + tiny N).
        private static int IndexOf(List<PlayerProfile> list, string profileId)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].ProfileId == profileId) return i;
            return -1;
        }

        // Write the whole list to disk, sorted by ProfileId (ordinal) for a byte-stable file.
        private void WriteAll(List<PlayerProfile> list)
        {
            list.Sort((a, b) => string.CompareOrdinal(a.ProfileId, b.ProfileId));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(list, JsonOptions));
        }
    }
}
