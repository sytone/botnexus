using System.Text.RegularExpressions;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

public sealed partial class IconComponentTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void RepeatedGradientIconsHaveUniqueLocallyResolvedIds()
    {
        var gradientNames = GradientIconNames();
        gradientNames.ShouldNotBeEmpty();

        var rendered = gradientNames
            .SelectMany(name => Enumerable.Range(0, 2).Select(_ => Render(name)))
            .ToArray();
        var ids = rendered.SelectMany(DeclaredIds).ToArray();

        ids.Length.ShouldBeGreaterThanOrEqualTo(gradientNames.Count * 2);
        ids.ShouldBeUnique();
        rendered.ShouldAllBe(markup => ReferencesResolveLocally(markup));
    }

    [Fact]
    public void GradientReferencesRemainLocalAfterRerenderAndNameChange()
    {
        var gradientNames = GradientIconNames();
        gradientNames.Count.ShouldBeGreaterThan(1);
        using var cut = _ctx.Render<Icon>(parameters => parameters
            .Add(component => component.Name, gradientNames[0])
            .Add(component => component.Title, "First icon"));

        var initialId = DeclaredIds(cut.Markup).ShouldHaveSingleItem();
        ReferencesResolveLocally(cut.Markup).ShouldBeTrue();

        cut.Render(parameters => parameters
            .Add(component => component.Name, gradientNames[0])
            .Add(component => component.Size, 32)
            .Add(component => component.Title, "Resized icon"));
        DeclaredIds(cut.Markup).ShouldHaveSingleItem().ShouldBe(initialId);
        ReferencesResolveLocally(cut.Markup).ShouldBeTrue();

        cut.Render(parameters => parameters
            .Add(component => component.Name, gradientNames[1])
            .Add(component => component.Title, "Changed icon"));
        var changedId = DeclaredIds(cut.Markup).ShouldHaveSingleItem();
        changedId.ShouldNotBe(initialId);
        changedId.ShouldStartWith($"bn-{gradientNames[1]}-g-i");
        ReferencesResolveLocally(cut.Markup).ShouldBeTrue();
    }

    private string Render(string name) =>
        _ctx.Render<Icon>(parameters => parameters.Add(component => component.Name, name)).Markup;

    private static IReadOnlyList<string> GradientIconNames() =>
        IconLibrary.Icons
            .Where(pair => UrlReferenceRegex().IsMatch(pair.Value.Stroke + " " + pair.Value.Body))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] DeclaredIds(string markup) =>
        IdRegex().Matches(markup).Select(match => match.Groups[1].Value).ToArray();

    private static bool ReferencesResolveLocally(string markup)
    {
        var ids = DeclaredIds(markup).ToHashSet(StringComparer.Ordinal);
        var references = UrlReferenceRegex().Matches(markup)
            .Select(match => match.Groups[1].Value)
            .ToArray();
        return references.Length > 0 && references.All(ids.Contains);
    }

    [GeneratedRegex("\\bid=\"([^\"]+)\"")]
    private static partial Regex IdRegex();

    [GeneratedRegex("url\\(#([^)]+)\\)")]
    private static partial Regex UrlReferenceRegex();
}
