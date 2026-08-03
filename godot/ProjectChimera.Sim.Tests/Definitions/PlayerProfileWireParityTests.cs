#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using ProjectChimera.Core;               // Fixed
using ProjectChimera.Core.Definitions;   // PlayerProfile, HeroProfileValidator
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-438 — the C# half of the WIRE-level C#&lt;-&gt;TS round trip. The shared parity oracle
    /// (<c>validation-cases.json</c>) proves the two validators agree on hand-authored JSON literals, but the live
    /// client-&gt;server boundary ships whatever <c>JsonSerializer.Serialize(profile)</c> ACTUALLY emits
    /// (<c>NakamaService.WriteHeroProfileViaRpcAsync</c>). This suite pins that genuine output
    /// BYTE-FOR-BYTE to the shared fixture <c>csharp-serialized-profile.json</c> (embedded; the TS vitest suite
    /// <c>csharp-roundtrip.test.ts</c> feeds the SAME bytes to <c>validateHeroProfile</c> + both RPC handlers). A
    /// <c>JsonPropertyName</c> rename, a <c>Fixed</c> raw-encoding change, or a converter change breaks the byte
    /// assertion here, forcing a fixture regeneration that the TS side must then re-accept — so a wire-format drift
    /// can no longer ship with every rule-parity test green. Godot-free (Tier-1).
    /// </summary>
    public class PlayerProfileWireParityTests
    {
        /// <summary>The canonical wire profile — exercises every serialized feature: all five scalar fields (signature
        /// ability PRESENT so the TS sanitize whitelist must carry it), an int attribute, a <see cref="Fixed"/> 16.16
        /// raw (12.5 → 819200, integer-built — no floats), and a slot-faithful multi-item inventory through the
        /// <c>ProfileInventoryItemJsonConverter</c>.</summary>
        private static PlayerProfile CanonicalProfile() => new PlayerProfile
        {
            ProfileId        = "grommash#wire-1",
            HeroDefId        = "grommash",
            FactionId        = "rebels",
            DisplayName      = "Grommash the Undying",
            SignatureAbility = "war-cry",
            Values = new List<ProfileAttributeValue>
            {
                new("hero.level", 7),
                new("hero.xp", (Fixed.FromInt(12) + Fixed.Half).Raw), // 12.5 in 16.16 = 819200
                new("hero.strength", 42),
            },
            Inventory = new List<ProfileInventoryItem>
            {
                new("healing-potion", 3, 0),
                new("ring-of-haste", 0, 2),
            },
        };

        /// <summary>Read the shared wire fixture (raw compact JSON, one line). Trim only neutralizes a trailing
        /// newline an editor/git could append — never the JSON text itself.</summary>
        private static string FixtureWire()
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("csharp-serialized-profile.json")
                ?? throw new InvalidOperationException("Embedded shared fixture 'csharp-serialized-profile.json' not found.");
            using var reader = new StreamReader(s);
            return reader.ReadToEnd().Trim();
        }

        [Fact]
        public void ProductionSerialization_MatchesTheSharedWireFixture_ByteForByte()
        {
            // EXACTLY the production call shape: NakamaService.WriteHeroProfileViaRpcAsync does
            // JsonSerializer.Serialize(profile) with default options — no custom options here either.
            string wire = JsonSerializer.Serialize(CanonicalProfile());
            Assert.Equal(FixtureWire(), wire);
        }

        [Fact]
        public void CanonicalWireProfile_IsValid_SoTheTsAcceptanceIsMeaningful()
        {
            // The TS suite asserts these exact bytes are ACCEPTED — that only proves parity if the profile is valid
            // on the C# side too (an invalid canonical profile would make the TS acceptance a parity BREAK).
            ProfileValidation v = HeroProfileValidator.Validate(CanonicalProfile());
            Assert.True(v.IsValid);
            Assert.Equal(ProfileInvalidReason.None, v.Reason);
        }

        [Fact]
        public void WireFixture_DeserializesBackToTheCanonicalSemantics()
        {
            // The stored object comes BACK over the wire too (owner-read ReadStorageObjectsAsync → Deserialize), so
            // the fixture must round-trip into the same semantics — level/xp reconstructed by key, slots faithful.
            PlayerProfile p = JsonSerializer.Deserialize<PlayerProfile>(FixtureWire())!;
            Assert.Equal("grommash#wire-1", p.ProfileId);
            Assert.Equal("grommash", p.HeroDefId);
            Assert.Equal(7, p.Level);
            Assert.Equal((Fixed.FromInt(12) + Fixed.Half).Raw, p.Xp.Raw);
            Assert.Equal(2, p.Inventory.Count);
            Assert.Equal(new ProfileInventoryItem("healing-potion", 3, 0), p.Inventory[0]);
            Assert.Equal(new ProfileInventoryItem("ring-of-haste", 0, 2), p.Inventory[1]);
            // And re-serializing the deserialized profile reproduces the identical bytes (converter symmetry).
            Assert.Equal(FixtureWire(), JsonSerializer.Serialize(p));
        }
    }
}
