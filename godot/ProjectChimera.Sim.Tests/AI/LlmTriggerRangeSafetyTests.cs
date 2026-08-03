#nullable enable
using ProjectChimera.AI;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-334 — first coverage of <see cref="LLMService.Validate"/>'s Pass 6 range/safety guard (the surface Story
    /// 7.1 adapted to <see cref="Fixed"/> that had no test at all): the out-of-map-bounds <c>spawn_unit</c> reject
    /// (Fixed-vs-Fixed against the quantized <see cref="ScenarioContext.MapBounds"/>), the <c>count</c> auto-clamp
    /// to 1..50, the <c>display_message</c> <c>duration &lt;= 0</c> auto-fix to 4s, and the <c>create_timer</c>
    /// non-positive-duration reject. Inverting the bounds comparison or breaking either auto-fix/reject fails here.
    /// </summary>
    public class LlmTriggerRangeSafetyTests
    {
        private static ScenarioContext Ctx() => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>One-action trigger JSON around <paramref name="actionJson"/> (valid event, no conditions).</summary>
        private static string Trigger(string actionJson) =>
            "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
            $"\"actions\":[{actionJson}]}}";

        // ── spawn_unit map-bounds reject (Fixed-vs-Fixed) ─────────────────────

        [Fact]
        public void Validate_AcceptsSpawnInsideBounds()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":-30,\"z\":10,\"count\":5}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
        }

        [Fact]
        public void Validate_AcceptsSpawnExactlyOnBoundary()
        {
            // The guard rejects strictly OUTSIDE ±bounds — x/z exactly at ±120 (quantized exactly in Fixed) pass.
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":120,\"z\":-120,\"count\":1}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
        }

        [Fact]
        public void Validate_RejectsSpawnBeyondPositiveX()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":121,\"z\":0,\"count\":1}"),
                Ctx());
            Assert.Null(trigger);
            Assert.Contains("outside map bounds", error);
        }

        [Fact]
        public void Validate_RejectsSpawnBeyondNegativeX()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":-121,\"z\":0,\"count\":1}"),
                Ctx());
            Assert.Null(trigger);
            Assert.Contains("outside map bounds", error);
        }

        [Fact]
        public void Validate_RejectsSpawnBeyondZBounds()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":0,\"z\":999,\"count\":1}"),
                Ctx());
            Assert.Null(trigger);
            Assert.Contains("outside map bounds", error);
        }

        [Fact]
        public void Validate_BoundsFollowTheContext_NotAHardcoded120()
        {
            // A tighter context rejects a position the default would accept — the guard reads ScenarioContext.MapBounds.
            var tight = new ScenarioContext { UnitIds = new[] { "melee" }, MapBounds = 50f };
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":60,\"z\":0,\"count\":1}"),
                tight);
            Assert.Null(trigger);
            Assert.Contains("outside map bounds", error);
        }

        // ── spawn_unit count auto-clamp (1..50) ───────────────────────────────

        [Fact]
        public void Validate_ClampsNonPositiveSpawnCountUpToOne()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":0,\"z\":0,\"count\":0}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
            Assert.Equal(1, trigger!.Actions[0].Count);
        }

        [Fact]
        public void Validate_ClampsOversizedSpawnCountDownToFifty()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"spawn_unit\",\"unit_id\":\"melee\",\"faction\":0,\"x\":0,\"z\":0,\"count\":999}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
            Assert.Equal(50, trigger!.Actions[0].Count);
        }

        // ── display_message duration auto-fix ─────────────────────────────────

        [Fact]
        public void Validate_AutoFixesZeroMessageDurationToFourSeconds()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"display_message\",\"text\":\"hi\",\"duration\":0}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
            Assert.Equal(Fixed.FromInt(4), trigger!.Actions[0].Duration);
        }

        [Fact]
        public void Validate_AutoFixesNegativeMessageDurationToFourSeconds()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"display_message\",\"text\":\"hi\",\"duration\":-2}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
            Assert.Equal(Fixed.FromInt(4), trigger!.Actions[0].Duration);
        }

        [Fact]
        public void Validate_PreservesPositiveMessageDuration()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"display_message\",\"text\":\"hi\",\"duration\":7}"),
                Ctx());
            Assert.NotNull(trigger);
            Assert.Null(error);
            Assert.Equal(Fixed.FromInt(7), trigger!.Actions[0].Duration);
        }

        // ── create_timer non-positive duration reject ─────────────────────────

        [Fact]
        public void Validate_RejectsNonPositiveTimerDuration()
        {
            var (trigger, error) = LLMService.Validate(
                Trigger("{\"type\":\"create_timer\",\"timer_name\":\"t1\",\"timer_seconds\":0}"),
                Ctx());
            Assert.Null(trigger);
            Assert.Contains("invalid duration", error);
        }
    }
}
