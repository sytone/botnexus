using System.Text.Json;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.JSInterop;
using NSubstitute;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed class FocusedSidebarPreferenceTests
{
    [Fact]
    public async Task FocusedSidebar_DefaultsOff_PersistsAndReloadsWithoutChangingShell()
    {
        var property = typeof(PortalPreferences).GetProperty("FocusedSidebar");
        property.ShouldNotBeNull();
        var setter = typeof(PortalPreferencesService).GetMethod("SetFocusedSidebarAsync");
        setter.ShouldNotBeNull();
        var js = Substitute.For<IJSRuntime>();
        string? saved = null;
        js.InvokeAsync<object>("portalPrefs.save", Arg.Do<object[]>(args => saved = args[1]?.ToString()))
            .Returns(new ValueTask<object>(new object()));
        var service = new PortalPreferencesService(js);
        property.GetValue(service.Current).ShouldBe(false);
        service.Current.Shell = PortalShell.Simplified;
        service.Current.Theme = PortalTheme.Light;
        var changed = 0;
        service.OnChanged += () => changed++;
        var operation = setter.Invoke(service, [true]);
        if (operation is not Task task)
            throw new InvalidOperationException("Preference setter must return Task.");
        await task;
        changed.ShouldBe(1);
        saved.ShouldNotBeNull();
        using var json = JsonDocument.Parse(saved);
        json.RootElement.GetProperty("FocusedSidebar").GetBoolean().ShouldBeTrue();
        js.InvokeAsync<string>("portalPrefs.load", Arg.Any<object[]>()).Returns(new ValueTask<string>(saved));
        var reloaded = new PortalPreferencesService(js);
        await reloaded.LoadAsync();
        property.GetValue(reloaded.Current).ShouldBe(true);
        reloaded.Current.Shell.ShouldBe(PortalShell.Simplified);
        reloaded.Current.Theme.ShouldBe(PortalTheme.Light);
        if (setter.Invoke(service, [false]) is not Task disableTask)
            throw new InvalidOperationException("Preference setter must return Task.");
        await disableTask;
        property.GetValue(service.Current).ShouldBe(false);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public async Task FocusedSidebar_MissingOrMalformedStorage_RemainsOff(string stored)
    {
        var property = typeof(PortalPreferences).GetProperty("FocusedSidebar");
        property.ShouldNotBeNull();
        var js = Substitute.For<IJSRuntime>();
        js.InvokeAsync<string>("portalPrefs.load", Arg.Any<object[]>()).Returns(new ValueTask<string>(stored));
        var service = new PortalPreferencesService(js);
        await service.LoadAsync();
        property.GetValue(service.Current).ShouldBe(false);
    }
}
