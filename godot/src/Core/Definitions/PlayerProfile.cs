#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
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

    /// <summary>One persisted inventory item in a <see cref="PlayerProfile"/> (Story 3.16): the item-def STRING id (never a
    /// volatile packed <c>ItemStore</c> ref) + its remaining <c>Charges</c>. The <c>(key,int-raw)</c> <see cref="ProfileAttributeValue"/>
    /// shape cannot express an ordered list of (id, charges) pairs, so <c>hero.inventory</c> rides its own serialized list;
    /// re-mint resolves the id back to a registry index and creates a fresh <c>ItemStore</c> instance (D-4).</summary>
    /// <para>Story 3.16 (review): <c>Slot</c> is the 0-based inventory grid index the item occupied at Save, so re-mint is
    /// SLOT-FAITHFUL (an item in slot 2 comes back in slot 2, not repacked to slot 1). Defaults to <c>-1</c> for a legacy
    /// profile that predates slot capture — re-mint then falls back to the first free slot (the old contiguous behaviour).</para>
    /// <para>DW-48: the type-level <see cref="ProfileInventoryItemJsonConverter"/> deserializes a MISSING <c>"slot"</c> key
    /// to <c>-1</c> (the legacy sentinel), not System.Text.Json's <c>default(int)=0</c> — a positional record's default
    /// parameter value is NOT honoured for an absent JSON property, so without the converter a slot-less legacy loadout
    /// would collapse every item onto slot 0. The serialized form is unchanged (<c>item_id</c>, <c>charges</c>, <c>slot</c>,
    /// in that order, <c>slot</c> always written), so no golden re-baseline is required.</para>
    [JsonConverter(typeof(ProfileInventoryItemJsonConverter))]
    public readonly record struct ProfileInventoryItem(
        [property: JsonPropertyName("item_id")] string ItemId,
        [property: JsonPropertyName("charges")] int Charges,
        [property: JsonPropertyName("slot")] int Slot = -1);

    /// <summary>DW-48: deserialize a <see cref="ProfileInventoryItem"/> so an ABSENT <c>"slot"</c> key becomes <c>-1</c>
    /// (backed by a nullable that stays null → <c>-1</c>), never <c>default(int)=0</c>. Serialization stays byte-identical
    /// to the default positional-record shape: <c>item_id</c>, <c>charges</c>, <c>slot</c> in that order, <c>slot</c> always
    /// written — so a round-trip of an explicit slot is faithful and no existing on-disk profile changes bytes.</summary>
    internal sealed class ProfileInventoryItemJsonConverter : JsonConverter<ProfileInventoryItem>
    {
        public override ProfileInventoryItem Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException();
            string itemId = "";
            int charges = 0;
            int? slot = null; // absent key stays null → -1 (never default(int)=0)
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
                string name = reader.GetString()!;
                reader.Read();
                switch (name)
                {
                    case "item_id": itemId = reader.GetString() ?? ""; break;
                    case "charges": charges = reader.GetInt32(); break;
                    case "slot":    slot = reader.TokenType == JsonTokenType.Null ? (int?)null : reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }
            return new ProfileInventoryItem(itemId, charges, slot ?? -1);
        }

        public override void Write(Utf8JsonWriter writer, ProfileInventoryItem value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("item_id", value.ItemId);
            writer.WriteNumber("charges", value.Charges);
            writer.WriteNumber("slot", value.Slot);
            writer.WriteEndObject();
        }
    }

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

        /// <summary>Story 3.16: the persisted per-hero inventory as ordered (item-def id + charges) pairs — never volatile
        /// packed refs (D-4). Captured on Save when the manifest shape carries <c>hero.inventory</c>; re-minted into
        /// <c>ItemStore</c> + <c>HeroStore.Inventory[]</c> at init-time before <see cref="StartStateHash.Compute"/>. The
        /// setter coerces a hand-edited JSON <c>null</c> to an empty list. Omitted when empty (no faction/profile churn).</summary>
        [JsonPropertyName("inventory")]
        public List<ProfileInventoryItem> Inventory
        {
            get => _inventory;
            set => _inventory = value ?? new List<ProfileInventoryItem>();
        }
        private List<ProfileInventoryItem> _inventory = new();

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
            Inventory       = new List<ProfileInventoryItem>(_inventory),
        };
    }
}
