using BotNexus.Cron;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Api.Models;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2671: the create and update seams must reject an unresolvable
/// <see cref="CronJob.FailureAlertConversationId"/>, via the SAME shared helper, so a job that
/// could never deliver its alert is never storable in that state.
/// </summary>
public sealed partial class CronControllerTests
{
    // Clause 1: rejected AND nothing persisted. Asserting only the 400 would pass while the row landed.
    [Fact]
    public async Task Create_WithUnresolvableFailureAlertConversationId_ReturnsBadRequest_NamingId_AndPersistsNothing()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var result = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-alert",
                Name = "Alerting job",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi",
                FailureAlertsEnabled = true,
                FailureAlertConversationId = "conv-typo"
            },
            CancellationToken.None);

        var bad = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        bad.Value!.ToString()!.ShouldContain("conv-typo");
        (await store.GetAsync(JobId.From("job-alert"))).ShouldBeNull();
        (await store.ListAsync()).ShouldBeEmpty();
    }

    // Clause 3: alerting stays opt-in - an absent target still creates successfully.
    [Fact]
    public async Task Create_WithoutFailureAlertConversationId_StillSucceeds()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var result = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-noalert",
                Name = "No alert",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi"
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
        (await store.GetAsync(JobId.From("job-noalert"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_WithResolvableFailureAlertConversationId_Succeeds_AndRoundTripsTheTarget()
    {
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-real"]));

        var result = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-goodalert",
                Name = "Good alert",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi",
                FailureAlertsEnabled = true,
                FailureAlertConversationId = "conv-real"
            },
            CancellationToken.None);

        result.Result.ShouldBeOfType<CreatedAtActionResult>();
        var stored = await store.GetAsync(JobId.From("job-goodalert"));
        stored.ShouldNotBeNull();
        stored!.FailureAlertConversationId!.Value.Value.ShouldBe("conv-real");
    }

    // Clause 2: the update seam is closed on the same terms and leaves the stored value intact.
    [Fact]
    public async Task Update_ToUnresolvableFailureAlertConversationId_ReturnsBadRequest_AndLeavesStoredValueIntact()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("conv-real")
        });
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver(["conv-real"]));

        var result = await controller.Update(
            "job-1",
            CronJobUpdateRequest.FromCronJob(CreateJob("job-1") with
            {
                FailureAlertsEnabled = true,
                FailureAlertConversationId = ConversationId.From("conv-gone")
            }),
            CancellationToken.None);

        var bad = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        bad.Value!.ToString()!.ShouldContain("conv-gone");
        (await store.GetAsync(JobId.From("job-1")))!.FailureAlertConversationId!.Value.Value.ShouldBe("conv-real");
    }

    [Fact]
    public async Task Update_ClearingFailureAlertConversationId_IsAllowed()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1") with
        {
            FailureAlertsEnabled = true,
            FailureAlertConversationId = ConversationId.From("conv-real")
        });
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var result = await controller.Update(
            "job-1",
            CronJobUpdateRequest.FromCronJob(CreateJob("job-1") with { FailureAlertConversationId = null }),
            CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        (await store.GetAsync(JobId.From("job-1")))!.FailureAlertConversationId.ShouldBeNull();
    }

    // Clause 2's anti-drift assertion: BOTH seams must produce the SAME rejection text, which is
    // only possible if they route through the one shared helper. Two independently written
    // checks would drift here first.
    [Fact]
    public async Task CreateAndUpdate_RejectTheSameUnresolvableTarget_WithByteIdenticalMessage_ProvingOneSharedHelper()
    {
        var store = new FakeCronStore();
        await store.CreateAsync(CreateJob("job-1"));
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), new StubAlertResolver([]));

        var createResult = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-2",
                Name = "Alerting job",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi",
                FailureAlertConversationId = "conv-shared"
            },
            CancellationToken.None);

        var updateResult = await controller.Update(
            "job-1",
            CronJobUpdateRequest.FromCronJob(CreateJob("job-1") with { FailureAlertConversationId = ConversationId.From("conv-shared") }),
            CancellationToken.None);

        var createMessage = createResult.Result.ShouldBeOfType<BadRequestObjectResult>().Value!.ToString();
        var updateMessage = updateResult.Result.ShouldBeOfType<BadRequestObjectResult>().Value!.ToString();

        createMessage.ShouldBe(updateMessage);
        createMessage.ShouldBe(CronAlertTarget.UnresolvableMessage("conv-shared"));
    }

    // #3168 AC3: the controller must accept a real target when wired to the PRODUCTION resolver,
    // not merely to a test stub. The suite was green over a feature that could not run precisely
    // because every alert-target test supplied the stub the product did not have.
    [Fact]
    public async Task Create_WithProductionResolver_AcceptsAnExistingConversation_AndRejectsAMissingOne()
    {
        var conversations = new BotNexus.Gateway.Conversations.InMemoryConversationStore();
        var conversationId = ConversationId.From("c_live");
        await conversations.CreateAsync(new BotNexus.Gateway.Abstractions.Models.Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a")
        });
        var resolver = new BotNexus.Gateway.Cron.ConversationCronAlertTargetResolver(
            conversations,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BotNexus.Gateway.Cron.ConversationCronAlertTargetResolver>.Instance);
        var store = new FakeCronStore();
        var controller = CreateController(store, new RecordingAction(), new CronOptions(), resolver);

        var accepted = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-live",
                Name = "Live alert",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi",
                FailureAlertsEnabled = true,
                FailureAlertConversationId = "c_live"
            },
            CancellationToken.None);

        accepted.Result.ShouldBeOfType<CreatedAtActionResult>();
        (await store.GetAsync(JobId.From("job-live")))!.FailureAlertConversationId!.Value.Value.ShouldBe("c_live");

        var rejected = await controller.Create(
            new CronJobCreateRequest
            {
                Id = "job-dead",
                Name = "Dead alert",
                Schedule = "0 * * * *",
                ActionType = "agent-prompt",
                Message = "hi",
                FailureAlertsEnabled = true,
                FailureAlertConversationId = "c_nope"
            },
            CancellationToken.None);

        rejected.Result.ShouldBeOfType<BadRequestObjectResult>()
            .Value!.ToString().ShouldBe(CronAlertTarget.UnresolvableMessage("c_nope"));
        (await store.GetAsync(JobId.From("job-dead"))).ShouldBeNull();
    }

    private sealed class StubAlertResolver(IReadOnlyCollection<string> known) : ICronAlertTargetResolver
    {
        public Task<bool> ExistsAsync(ConversationId conversationId, CancellationToken ct = default)
            => Task.FromResult(known.Contains(conversationId.Value));
    }
}
