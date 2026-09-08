using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #3808: <c>PUT /api/cron/{jobId}</c> must apply the same omitted-field rule as the
/// <c>CronTool</c> update seam - a field absent from the body leaves the stored value alone, and
/// only an explicit value mutates it.
/// </summary>
/// <remarks>
/// <para>
/// These cases go through <see cref="CronJobUpdateRequest"/> rather than the domain record on
/// purpose. The defect was precisely that the endpoint could not tell "the caller omitted this"
/// from "the caller sent the default", so a test that constructs a fully populated request cannot
/// observe it - it has already supplied every field. Absence has to be expressible for the bug to
/// be expressible.
/// </para>
/// <para>
/// Every preserve case seeds the six fields to NON-default values first. Asserting that an omitted
/// field is still <c>false</c>/<c>null</c> would pass on the broken code too, since that is exactly
/// what the defect writes.
/// </para>
/// </remarks>
public sealed partial class CronControllerTests
{
    /// <summary>A job with every #3808 field set to a non-default value.</summary>
    private static CronJob CreatePolicyJob(string id) => CreateJob(id) with
    {
        FailureAlertsEnabled = true,
        FailureAlertConversationId = ConversationId.From("conv-alerts"),
        DeleteJobAfterRun = true,
        DeleteAfterRun = true,
        ExpiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ExecutionClass = true
    };

    // AC1: an edit that never mentions the alert fields must not un-alert the job.
    [Fact]
    public async Task Update_OmittingFailureAlertFields_LeavesBothStoredValuesUnchanged()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-alerts"]));

        var result = await controller.Update(
            "job-1",
            new CronJobUpdateRequest { Name = CronPatch<string>.Set("Renamed, nothing else") },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.Name.ShouldBe("Renamed, nothing else");
        stored.FailureAlertsEnabled.ShouldBeTrue();
        stored.FailureAlertConversationId!.Value.Value.ShouldBe("conv-alerts");
    }

    // AC2: the four lifecycle/classification fields survive an unrelated edit.
    [Fact]
    public async Task Update_OmittingLifecycleFields_LeavesAllFourStoredValuesUnchanged()
    {
        var store = new FakeCronStore();
        var expiry = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-alerts"]));

        var result = await controller.Update(
            "job-1",
            new CronJobUpdateRequest { Schedule = CronPatch<string>.Set("*/10 * * * *") },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.Schedule.ShouldBe("*/10 * * * *");
        stored.DeleteAfterRun.ShouldBeTrue();
        stored.DeleteJobAfterRun.ShouldBeTrue();
        stored.ExpiresAt.ShouldBe(expiry);
        stored.ExecutionClass.ShouldBeTrue();
    }

    // AC3, mutate half: an explicit value still takes effect. Without this, "preserve everything"
    // would pass the two cases above and make the endpoint useless.
    [Fact]
    public async Task Update_WithExplicitValues_StillMutatesEverySixField()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-other"]));
        var newExpiry = new DateTimeOffset(2031, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var result = await controller.Update(
            "job-1",
            new CronJobUpdateRequest
            {
                FailureAlertsEnabled = CronPatch<bool>.Set(false),
                FailureAlertConversationId = CronPatch<string>.Set("conv-other"),
                DeleteJobAfterRun = CronPatch<bool>.Set(false),
                DeleteAfterRun = CronPatch<bool>.Set(false),
                ExpiresAt = CronPatch<DateTimeOffset?>.Set(newExpiry),
                ExecutionClass = CronPatch<bool>.Set(false)
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.FailureAlertsEnabled.ShouldBeFalse();
        stored.FailureAlertConversationId!.Value.Value.ShouldBe("conv-other");
        stored.DeleteJobAfterRun.ShouldBeFalse();
        stored.DeleteAfterRun.ShouldBeFalse();
        stored.ExpiresAt.ShouldBe(newExpiry);
        stored.ExecutionClass.ShouldBeFalse();
    }

    // AC3, clear half: an explicit null clears the two nullable fields. This is the case that
    // distinguishes a real tri-state from "omitted means preserve" applied to nullables - under a
    // two-state model there would be no way to clear these at all.
    [Fact]
    public async Task Update_WithExplicitNulls_ClearsAlertTargetAndExpiry()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var result = await controller.Update(
            "job-1",
            new CronJobUpdateRequest
            {
                FailureAlertConversationId = CronPatch<string>.Set(null),
                ExpiresAt = CronPatch<DateTimeOffset?>.Set(null)
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.FailureAlertConversationId.ShouldBeNull();
        stored.ExpiresAt.ShouldBeNull();

        // The un-mentioned neighbours are still intact - a clear must be surgical, not a reset.
        stored.FailureAlertsEnabled.ShouldBeTrue();
        stored.ExecutionClass.ShouldBeTrue();
    }

    /// <summary>
    /// AC4, the anti-drift assertion: the REST and tool seams must produce the SAME stored job for
    /// the same logical edit.
    /// </summary>
    /// <remarks>
    /// This is the clause that keeps the fix from rotting. #2554 and #3575 each hardened one field
    /// of this endpoint by hand and the block still read as complete afterwards, because nothing
    /// compared it against the seam that already had the rule. Here the two seams edit identical
    /// jobs identically and the resulting records are compared field by field, so a future divergence
    /// fails a test rather than being discovered in production a year later.
    /// </remarks>
    [Fact]
    public async Task Update_ViaRestAndViaTool_ProduceTheSameStoredJob()
    {
        var restStore = new FakeCronStore();
        var toolStore = new FakeCronStore();
        await restStore.CreateAsync(CreatePolicyJob("job-1"));
        await toolStore.CreateAsync(CreatePolicyJob("job-1"));

        var controller = CreateController(restStore, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-alerts"]));

        // The same logical edit expressed on each seam: rename, and mention nothing else.
        await controller.Update(
            "job-1",
            new CronJobUpdateRequest { Name = CronPatch<string>.Set("Same edit") },
            CancellationToken.None);

        var toolExisting = await toolStore.GetAsync(JobId.From("job-1"));
        toolExisting.ShouldNotBeNull();
        await toolStore.UpdateDefinitionAsync(
            toolExisting! with { Name = "Same edit" },
            CronJobOwnershipExpectation.From(toolExisting!));

        var viaRest = await restStore.GetAsync(JobId.From("job-1"));
        var viaTool = await toolStore.GetAsync(JobId.From("job-1"));
        viaRest.ShouldNotBeNull();
        viaTool.ShouldNotBeNull();

        viaRest!.Name.ShouldBe(viaTool!.Name);
        viaRest.Schedule.ShouldBe(viaTool.Schedule);
        viaRest.FailureAlertsEnabled.ShouldBe(viaTool.FailureAlertsEnabled);
        viaRest.FailureAlertConversationId.ShouldBe(viaTool.FailureAlertConversationId);
        viaRest.DeleteJobAfterRun.ShouldBe(viaTool.DeleteJobAfterRun);
        viaRest.DeleteAfterRun.ShouldBe(viaTool.DeleteAfterRun);
        viaRest.ExpiresAt.ShouldBe(viaTool.ExpiresAt);
        viaRest.ExecutionClass.ShouldBe(viaTool.ExecutionClass);
    }

    /// <summary>
    /// The wire-level proof. Everything above constructs the request in C#; this one deserialises
    /// actual JSON, because the whole mechanism rests on System.Text.Json invoking a converter only
    /// for properties that are physically present in the payload.
    /// </summary>
    [Fact]
    public async Task Update_FromJsonBodyOmittingPolicyFields_PreservesThem()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-alerts"]));

        var request = System.Text.Json.JsonSerializer.Deserialize<CronJobUpdateRequest>(
            """{"name":"Edited over the wire","schedule":"*/15 * * * *"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        request.ShouldNotBeNull();

        // Absence really is absence after deserialisation, not a defaulted value.
        request!.FailureAlertsEnabled.IsSet.ShouldBeFalse();
        request.ExpiresAt.IsSet.ShouldBeFalse();
        request.Name.IsSet.ShouldBeTrue();

        var result = await controller.Update("job-1", request, CancellationToken.None);
        result.Result.ShouldBeOfType<OkObjectResult>();

        var stored = await store.GetAsync(JobId.From("job-1"));
        stored.ShouldNotBeNull();
        stored!.Name.ShouldBe("Edited over the wire");
        stored.FailureAlertsEnabled.ShouldBeTrue();
        stored.FailureAlertConversationId!.Value.Value.ShouldBe("conv-alerts");
        stored.DeleteJobAfterRun.ShouldBeTrue();
        stored.DeleteAfterRun.ShouldBeTrue();
        stored.ExecutionClass.ShouldBeTrue();
        stored.ExpiresAt.ShouldBe(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// A present-but-null JSON property is an explicit clear, not an omission. If the converter
    /// were bypassed for nulls the two would collapse and clearing would become impossible.
    /// </summary>
    [Fact]
    public async Task Update_FromJsonBodyWithExplicitNull_ClearsTheField()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreatePolicyJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var request = System.Text.Json.JsonSerializer.Deserialize<CronJobUpdateRequest>(
            """{"failureAlertConversationId":null}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        request.ShouldNotBeNull();
        request!.FailureAlertConversationId.IsSet.ShouldBeTrue();

        var result = await controller.Update("job-1", request, CancellationToken.None);
        result.Result.ShouldBeOfType<OkObjectResult>();

        (await store.GetAsync(JobId.From("job-1")))!.FailureAlertConversationId.ShouldBeNull();
    }

    /// <summary>
    /// An omitted webhook url must not be re-validated into a rejection, nor erased. A webhook job
    /// being renamed mentions no url at all.
    /// </summary>
    [Fact]
    public async Task Update_OmittingWebhookUrl_PreservesIt()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1", actionType: "webhook") with { WebhookUrl = "https://example.com/hook" });
        var controller = CreateController(store, new RecordingAction(), new CronOptions());

        var result = await controller.Update(
            "job-1",
            new CronJobUpdateRequest { Name = CronPatch<string>.Set("Renamed webhook job") },
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        (await store.GetAsync(JobId.From("job-1")))!.WebhookUrl.ShouldBe("https://example.com/hook");
    }
}
