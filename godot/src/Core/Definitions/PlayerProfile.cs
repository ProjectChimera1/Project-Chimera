#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProjectChimera.Core; // Fixed

namespace ProjectChimera.Core.Definitions
{
    /// <summary>One persisted attribute value in a <see cref="PlayerProfile"/> (Story 3.9): a stable dotted key and its
    /// value stored as an INTEGER raw — never a float. <c>hero.level</c> stores its plain <c>int</c>; <c>hero.xp</c>
    /// stores the <see cref="Fixed.Raw"/> 16.16 integer. Storing raw ints (not floats) is what lets the same saved
    /// profile reproduce a byte-identical <see cref="StartStateHash"/> across machines (D-1).</summary>
    public readonly record struct ProfileAttributeValue(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("raw")] int Raw);

    /// <summary>
    /// A saved hero, offline (Story 3.9, AR-12 M2 / FR-7b). The VALUE-bearing companion to Story 3.8's shape-only
    /// <see cref="PlayerProfileShape"/>: a Godot-free, deterministic value class the offline <see cref="LocalProfileSource"/>
    /// disk rail persists and the <see cref="HeroProfileLoader"/> mints into <see cref="HeroStore"/> as deterministic
    /// initial state before the match hash is computed.
    ///
    /// <para><b>Identity.</b> <see cref="ProfileId"/> is a stable string assigned by <see cref="LocalProfileSource"/> at
    /// save time and persisted forever; <see cref="HeroProfileLoader.MintId"/> hashes it (FNV-64) into the
    /// <see cref="HeroId"/> minted into the store, so re-loading the same profile always reproduces the SAME id → a
    /// byte-identical <see cref="StartStateHash"/> (MP-safe). <see cref="HeroDefId"/> is the <see cref="UnitDefinition.Id"/>
    /// this hero embodies — the compatibility key against a scenario's placed hero units.</para>
    ///
    /// <para><b>No floats — ever.</b> The persisted attribute values live in <see cref="Values"/> as integer raws
    /// (<see cref="ProfileAttributeValue"/>): level as its <c>int</c>, xp as its <see cref="Fixed.Raw"/>. The
    /// <see cref="Level"/> / <see cref="Xp"/> accessors reconstruct the typed values by key — no float field is ever
    /// declared or serialized (determinism, S-CORE-3).</para>
    /// </summary>
    public sealed class PlayerProfile
    {
        /// <summary>The stable, persisted identity assigned by <see cref="LocalProfileSource"/> at first save (e.g.
        /// <c>"grommash#1"</c>). FNV-hashed into the minted <see cref="HeroId"/>, so it must never change for a saved
        /// hero, and it is unique across saved profiles → unique across live <see cref="HeroStore"/> rows.</summary>
        [JsonPropertyName("profile_id")]
        public string ProfileId { get; set; } = "";

        /// <summary>The <see cref="UnitDefinition.Id"/> of the hero this profile embodies — the compatibility key: a
        /// profile is deployable into a scenario only when it places a hero unit with this id.</summary>
        [JsonPropertyName("hero_def_id")]
        public string HeroDefId { get; set; } = "";

        /// <summary>Owning faction id (<see cref="FactionDefinition.Id"/>) — CARD METADATA only (display), never a hard
        /// apply gate (D-3).</summary>
        [JsonPropertyName("faction_id")]
        public string FactionId { get; set; } = "";

        /// <summary>Human display name for the slot card.</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>The hero's signature ability id (card metadata). Null/empty = none authored.</summary>
        [JsonPropertyName("signature_ability")]
        public string? SignatureAbility { get; set; }

        /// <summary>The persisted attribute values (integer raws), one per manifest-shape slot the save captured. The
        /// setter coerces a hand-edited JSON <c>null</c> to an empty list so accessors never NRE.</summary>
        [JsonPropertyName("values")]
        public List<ProfileAttributeValue> Values
        {
            get => _values;
            set => _values = value ?? new List<ProfileAttributeValue>();
        }
        private List<ProfileAttributeValue> _values = new();

        /// <summary>The persisted <c>hero.level</c> (plain int), or 0 if the profile did not capture it.</summary>
        [JsonIgnore]
        public int Level => RawOf("hero.level");

        /// <summary>The persisted <c>hero.xp</c> reconstructed from its <see cref="Fixed.Raw"/> integer (never a float),
        /// or <see cref="Fixed.Zero"/> if the profile did not capture it.</summary>
        [JsonIgnore]
        public Fixed Xp => Fixed.FromRaw(RawOf("hero.xp"));

        /// <summary>Linear lookup of a persisted raw by key (no <c>Dictionary</c> enumeration — sim-layer rule). Returns
        /// 0 when the key was not captured.</summary>
        private int RawOf(string key)
        {
            for (int i = 0; i < _values.Count; i++)
                if (_values[i].Key == key) return _values[i].Raw;
            return 0;
        }

        /// <summary>A deep copy (the values list is re-materialised; records + strings are immutable) so a clone and its
        /// source can be edited independently — mirrors <see cref="PersistenceManifest.Clone"/>.</summary>
        public PlayerProfile Clone() => new PlayerProfile
        {
            ProfileId       = ProfileId,
            HeroDefId       = HeroDefId,
            FactionId       = FactionId,
            DisplayName     = DisplayName,
            SignatureAbility = SignatureAbility,
            Values          = new List<ProfileAttributeValue>(_values),
        };
    }
}
