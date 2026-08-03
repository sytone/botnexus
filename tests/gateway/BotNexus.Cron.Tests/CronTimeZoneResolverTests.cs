using BotNexus.Cron;

namespace BotNexus.Cron.Tests;

/// <summary>
/// Regression corpus for issue #2748.
/// <para>
/// The cron seam used to define timezone resolution three times: the scheduler's private
/// <c>ResolveTimeZone(CronJob)</c>, the cron tool's private <c>ResolveTimeZone(string)</c>,
/// and <c>TimeZoneHelper.Resolve</c>. Only the last one translated between Windows and IANA
/// ids, so on a host that stored only one family a resolvable id silently became UTC in the
/// next-run computation while the action that ran the job used the correct zone. These tests
/// pin the single canonical resolver: both conversion directions must resolve, and only a
/// genuinely unresolvable id may degrade to UTC.
/// </para>
/// <para>
/// The host database is injected because no single machine can exhibit both failure modes -
/// a Windows box knows Windows ids, a Linux box knows IANA ids. The fakes below are built
/// from custom zones so the assertions do not depend on the running host's tz database.
/// </para>
/// </summary>
public sealed class CronTimeZoneResolverTests
{
    private const string PacificWindowsId = "Pacific Standard Time";
    private const string PacificIanaId = "America/Los_Angeles";

    private static TimeZoneInfo FakeZone(string id)
        => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-8), id, id);

    // Models Linux: only IANA spellings exist in the host database.
    private static Func<string, TimeZoneInfo> IanaOnlyHost()
        => id => id == PacificIanaId
            ? FakeZone(PacificIanaId)
            : throw new TimeZoneNotFoundException(id);

    // Models Windows without ICU: only Windows spellings exist.
    private static Func<string, TimeZoneInfo> WindowsOnlyHost()
        => id => id == PacificWindowsId
            ? FakeZone(PacificWindowsId)
            : throw new TimeZoneNotFoundException(id);

    [Fact]
    public void Resolve_WindowsIdOnIanaOnlyHost_ResolvesViaConversion()
    {
        var resolved = CronTimeZoneResolver.Resolve(PacificWindowsId, IanaOnlyHost());

        resolved.Id.ShouldBe(PacificIanaId);
        resolved.ShouldNotBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Resolve_IanaIdOnWindowsOnlyHost_ResolvesViaConversion()
    {
        var resolved = CronTimeZoneResolver.Resolve(PacificIanaId, WindowsOnlyHost());

        resolved.Id.ShouldBe(PacificWindowsId);
        resolved.ShouldNotBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Resolve_NativeId_ResolvesWithoutConversion()
    {
        CronTimeZoneResolver.Resolve(PacificIanaId, IanaOnlyHost()).Id.ShouldBe(PacificIanaId);
        CronTimeZoneResolver.Resolve(PacificWindowsId, WindowsOnlyHost()).Id.ShouldBe(PacificWindowsId);
    }

    [Fact]
    public void Resolve_UnresolvableId_DegradesToUtcInsteadOfThrowing()
    {
        // Fail-safe: this runs inside the scheduler loop, so a throw would stop scheduling.
        CronTimeZoneResolver.Resolve("Invalid/Timezone", IanaOnlyHost()).ShouldBe(TimeZoneInfo.Utc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UTC")]
    [InlineData("utc")]
    public void Resolve_BlankOrUtc_ReturnsUtc(string? timezoneId)
    {
        CronTimeZoneResolver.Resolve(timezoneId, IanaOnlyHost()).ShouldBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Resolve_InvalidTimeZoneException_IsAlsoTreatedAsUnresolvable()
    {
        // A corrupt tz entry throws InvalidTimeZoneException, not TimeZoneNotFoundException.
        // The pre-#2748 scheduler caught only the latter and would have crashed the loop.
        Func<string, TimeZoneInfo> corruptHost = _ => throw new InvalidTimeZoneException("corrupt");

        CronTimeZoneResolver.Resolve(PacificIanaId, corruptHost).ShouldBe(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Resolve_DefaultOverload_UsesHostDatabaseAndResolvesBothFamilies()
    {
        // Guards the production entry point actually wired to the host lookup.
        CronTimeZoneResolver.Resolve(PacificIanaId).ShouldNotBe(TimeZoneInfo.Utc);
        CronTimeZoneResolver.Resolve(PacificWindowsId).ShouldNotBe(TimeZoneInfo.Utc);
        CronTimeZoneResolver.Resolve(PacificIanaId).BaseUtcOffset.ShouldBe(TimeSpan.FromHours(-8));
        CronTimeZoneResolver.Resolve(PacificWindowsId).BaseUtcOffset.ShouldBe(TimeSpan.FromHours(-8));
    }
}
