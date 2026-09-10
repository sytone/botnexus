using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Pages;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Pins the note explaining what the Skills page does and does not list.
/// </summary>
/// <remarks>
/// The page is a file browser over <c>~/.botnexus/skills</c> but is titled "Skills", so it reads
/// as the complete inventory. It is not: skill discovery also covers installed plugins and an
/// agent's own folder and workspace. On the machine this was found, an agent's own
/// <c>skills_list</c> returned four skills while the page showed two - the two from a plugin were
/// working and invisible. The note is the cheap fix for a view being mistaken for a total.
/// </remarks>
public sealed class SkillsPageScopeNoteTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public SkillsPageScopeNoteTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddSingleton(Substitute.For<IGatewayRestClient>());
        _ctx.Services.AddSingleton(Substitute.For<IPortalLoadService>());
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void The_page_says_which_folder_it_is_showing()
    {
        var cut = _ctx.Render<Skills>();

        var note = cut.Find("[data-testid='skills-scope-note']").TextContent;
        Assert.Contains("~/.botnexus/skills", note);
    }

    // The load-bearing half: without this, a reader concludes the two listed skills are all there
    // are, which is how working plugin skills get reported as missing.
    [Fact]
    public void The_page_says_agents_also_use_skills_it_does_not_list()
    {
        var cut = _ctx.Render<Skills>();

        var note = cut.Find("[data-testid='skills-scope-note']").TextContent;
        Assert.Contains("plugins", note);
        Assert.Contains("workspace", note);
        Assert.Contains("without appearing in this list", note);
    }
}
