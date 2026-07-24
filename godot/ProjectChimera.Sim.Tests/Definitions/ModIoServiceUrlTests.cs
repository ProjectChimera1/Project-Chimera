#nullable enable
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ProjectChimera.UGC;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 9.10 — Tier-1 proof that the content browser's six discovery verbs (browse/search/tag/sort) become
    /// mod.io-native query params, never a client-side index. Covers every <see cref="ModIoService.BuildModsUrl"/>
    /// I/O-Matrix row: default sort, sort override, escaped <c>_q</c>, one <c>tags=</c> per tag, and omission when
    /// search/tags are empty.
    /// </summary>
    public class ModIoServiceUrlTests
    {
        private const string Base = "https://api.mod.io/v1";

        private static int Count(string haystack, string needle) =>
            Regex.Matches(haystack, Regex.Escape(needle)).Count;

        [Fact]
        public void PlainBrowse_DefaultSort_NoQueryNoTags()
        {
            string url = ModIoService.BuildModsUrl(
                Base, gameId: 42, apiKey: "KEY", limit: 20, offset: 0,
                searchQuery: null, sort: null, tags: null);

            Assert.Contains("/games/42/mods", url);
            Assert.Contains("_limit=20", url);
            Assert.Contains("_offset=0", url);
            Assert.Contains("_sort=-popular", url);
            Assert.DoesNotContain("_q=", url);
            Assert.DoesNotContain("tags=", url);
        }

        [Fact]
        public void FullBrowse_SortOverride_EscapedQuery_OneTagsParamPerTag()
        {
            string url = ModIoService.BuildModsUrl(
                Base, gameId: 7, apiKey: "KEY", limit: 10, offset: 30,
                searchQuery: "desert storm", sort: "-downloads",
                tags: new List<string> { "1v1", "Melee" });

            Assert.Contains("_sort=-downloads", url);
            Assert.DoesNotContain("_sort=-popular", url);
            Assert.Contains("_q=desert%20storm", url); // space escaped
            Assert.Contains("tags=1v1", url);
            Assert.Contains("tags=Melee", url);
            Assert.Equal(2, Count(url, "tags="));      // exactly one param per tag
        }

        [Fact]
        public void EmptyOrWhitespaceSort_FallsBackToPopular()
        {
            string blank = ModIoService.BuildModsUrl(Base, 1, "K", 20, 0, null, "   ", null);
            string empty = ModIoService.BuildModsUrl(Base, 1, "K", 20, 0, null, "",    null);

            Assert.Contains("_sort=-popular", blank);
            Assert.Contains("_sort=-popular", empty);
        }

        [Fact]
        public void EmptyTagList_OmitsTagsParam()
        {
            string url = ModIoService.BuildModsUrl(
                Base, 1, "K", 20, 0, null, null, new List<string>());

            Assert.DoesNotContain("tags=", url);
        }

        [Fact]
        public void BlankTagsAreSkipped()
        {
            string url = ModIoService.BuildModsUrl(
                Base, 1, "K", 20, 0, null, null, new List<string> { "Melee", "  ", "" });

            Assert.Contains("tags=Melee", url);
            Assert.Equal(1, Count(url, "tags=")); // the blank/whitespace tags contribute no param
        }

        [Fact]
        public void TagValuesAreEscaped()
        {
            string url = ModIoService.BuildModsUrl(
                Base, 1, "K", 20, 0, null, null, new List<string> { "Free For All" });

            Assert.Contains("tags=Free%20For%20All", url);
        }

        [Fact]
        public void SearchQueryOmittedWhenBlank()
        {
            string url = ModIoService.BuildModsUrl(Base, 1, "K", 20, 0, "   ", null, null);
            Assert.DoesNotContain("_q=", url);
        }

        [Fact]
        public void OffsetAndLimitThreadedThrough()
        {
            string url = ModIoService.BuildModsUrl(Base, 3, "K", limit: 55, offset: 110,
                searchQuery: null, sort: null, tags: null);
            Assert.Contains("_limit=55", url);
            Assert.Contains("_offset=110", url);
        }

        // ── FlattenTagNames: the "no local tag index" flatten seam ─────────────

        [Fact]
        public void FlattenTagNames_NullGroups_ReturnsEmpty()
        {
            Assert.Empty(ModIoService.FlattenTagNames(null));
        }

        [Fact]
        public void FlattenTagNames_FlattensAllGroupsInOrder()
        {
            var groups = new List<ModIoTagOption>
            {
                new() { Name = "Mode",  Tags = new List<string> { "1v1", "Melee" } },
                new() { Name = "Theme", Tags = new List<string> { "Desert" } },
            };

            Assert.Equal(new[] { "1v1", "Melee", "Desert" }, ModIoService.FlattenTagNames(groups));
        }

        [Fact]
        public void FlattenTagNames_MalformedGroupDoesNotDropAllTags()
        {
            // A group with a null Tags list ("tags":null) must be skipped, not abort the whole flatten.
            var groups = new List<ModIoTagOption>
            {
                new() { Name = "Bad",  Tags = null! },
                new() { Name = "Good", Tags = new List<string> { "Melee" } },
            };

            Assert.Equal(new[] { "Melee" }, ModIoService.FlattenTagNames(groups));
        }

        [Fact]
        public void FlattenTagNames_SkipsBlankAndWhitespaceTags()
        {
            var groups = new List<ModIoTagOption>
            {
                new() { Name = "Mode", Tags = new List<string> { "Melee", "", "  ", "1v1" } },
            };

            Assert.Equal(new[] { "Melee", "1v1" }, ModIoService.FlattenTagNames(groups));
        }
    }
}
