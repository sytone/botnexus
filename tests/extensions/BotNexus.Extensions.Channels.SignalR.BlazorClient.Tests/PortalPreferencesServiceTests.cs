using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using System.Text.Json;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public class PortalPreferencesServiceTests
{
    private readonly IJSRuntime _js;
    private readonly PortalPreferencesService _sut;

    public PortalPreferencesServiceTests()
    {
        _js = Substitute.For<IJSRuntime>();
        _sut = new PortalPreferencesService(_js);
    }

    [Fact]
    public void Current_BeforeLoad_ReturnsDefaults()
    {
        var prefs = _sut.Current;
        Assert.True(prefs.ExpandingInput);
        Assert.Equal(8, prefs.ExpandingInputMaxLines);
    }

    [Fact]
    public async Task LoadAsync_WhenNoStoredData_UsesDefaults()
    {
        _js.InvokeAsync<string>("portalPrefs.load", Arg.Any<object[]>())
           .Returns(new ValueTask<string>((string?)null!));

        await _sut.LoadAsync();

        Assert.True(_sut.Current.ExpandingInput);
    }

    [Fact]
    public async Task LoadAsync_WithStoredData_RestoresPreferences()
    {
        var stored = JsonSerializer.Serialize(new PortalPreferences { ExpandingInput = false, ExpandingInputMaxLines = 4 });
        _js.InvokeAsync<string>("portalPrefs.load", Arg.Any<object[]>())
           .Returns(new ValueTask<string>(stored));

        await _sut.LoadAsync();

        Assert.False(_sut.Current.ExpandingInput);
        Assert.Equal(4, _sut.Current.ExpandingInputMaxLines);
    }

    [Fact]
    public async Task SetExpandingInputAsync_UpdatesPreferenceAndSaves()
    {
        await _sut.SetExpandingInputAsync(false);

        Assert.False(_sut.Current.ExpandingInput);
        await _js.Received(1).InvokeAsync<object>("portalPrefs.save", Arg.Any<object[]>());
    }

    [Fact]
    public async Task SetExpandingInputAsync_RaisesOnChanged()
    {
        var raised = false;
        _sut.OnChanged += () => raised = true;

        await _sut.SetExpandingInputAsync(false);

        Assert.True(raised);
    }

    [Fact]
    public async Task SaveAsync_WritesToLocalStorage()
    {
        await _sut.SaveAsync();

        await _js.Received(1).InvokeAsync<object>("portalPrefs.save", Arg.Any<object[]>());
    }

    [Fact]
    public async Task LoadAsync_WithInvalidJson_UsesDefaults()
    {
        _js.InvokeAsync<string>("portalPrefs.load", Arg.Any<object[]>())
           .Returns(new ValueTask<string>("not-valid-json"));

        await _sut.LoadAsync(); // should not throw

        Assert.True(_sut.Current.ExpandingInput);
    }
}
