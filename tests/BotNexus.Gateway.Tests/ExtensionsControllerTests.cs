using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ExtensionsControllerTests
{
    [Fact]
    public void List_WithNoLoadedExtensions_ReturnsEmptyList()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns([]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.Should().NotBeNull();
        payload.Should().BeEmpty();
    }

    [Fact]
    public void List_WithLoadedExtension_ReturnsExpectedMetadata()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns(
        [
            CreateLoadedExtension(
                extensionId: "ext-a",
                name: "Extension A",
                version: "1.2.3",
                assemblyPath: "Q:\\extensions\\ext-a\\ExtensionA.dll",
                extensionTypes: ["channel"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.Should().NotBeNull();
        payload!.Should().ContainSingle();
        payload[0].Should().BeEquivalentTo(
            new ExtensionResponse("Extension A", "1.2.3", "channel", "ExtensionA.dll"));
    }

    [Fact]
    public void List_WithLoadedExtensions_ReturnsFlattenedTypeRows()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns(
        [
            CreateLoadedExtension(
                extensionId: "ext-a",
                name: "Extension A",
                version: "1.2.3",
                assemblyPath: "Q:\\extensions\\ext-a\\ExtensionA.dll",
                extensionTypes: ["channel", "router"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.Should().NotBeNull();
        payload!.Should().HaveCount(2);
        payload.Should().BeEquivalentTo(
        [
            new ExtensionResponse("Extension A", "1.2.3", "channel", "ExtensionA.dll"),
            new ExtensionResponse("Extension A", "1.2.3", "router", "ExtensionA.dll")
        ]);
    }

    [Fact]
    public void List_WithMultipleExtensions_ReturnsAllExtensions()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns(
        [
            CreateLoadedExtension(
                extensionId: "ext-a",
                name: "Extension A",
                version: "1.2.3",
                assemblyPath: "Q:\\extensions\\ext-a\\ExtensionA.dll",
                extensionTypes: ["channel"]),
            CreateLoadedExtension(
                extensionId: "ext-b",
                name: "Extension B",
                version: "2.0.0",
                assemblyPath: "Q:\\extensions\\ext-b\\ExtensionB.dll",
                extensionTypes: ["transport"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.Should().NotBeNull();
        payload!.Should().HaveCount(2);
        payload.Select(item => item.Name).Should().ContainInOrder("Extension A", "Extension B");
    }

    [Fact]
    public void List_WithNoDeclaredExtensionTypes_UsesUnknownType()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns(
        [
            CreateLoadedExtension(
                extensionId: "ext-a",
                name: "Extension A",
                version: "1.2.3",
                assemblyPath: "Q:\\extensions\\ext-a\\ExtensionA.dll",
                extensionTypes: [])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.Should().NotBeNull();
        payload!.Should().ContainSingle();
        payload[0].Type.Should().Be("unknown");
    }

    [Fact]
    public void List_ReturnsOkResultWithExtensionResponses()
    {
        var loader = new Mock<IExtensionLoader>();
        loader.Setup(value => value.GetLoaded()).Returns(
        [
            CreateLoadedExtension(
                extensionId: "ext-a",
                name: "Extension A",
                version: "1.2.3",
                assemblyPath: "Q:\\extensions\\ext-a\\ExtensionA.dll",
                extensionTypes: ["channel"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        result.Result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        ok.Value.Should().BeAssignableTo<IReadOnlyList<ExtensionResponse>>();
        ((IReadOnlyList<ExtensionResponse>)ok.Value!).Should().OnlyContain(item => item is ExtensionResponse);
    }

    private static LoadedExtension CreateLoadedExtension(
        string extensionId,
        string name,
        string version,
        string assemblyPath,
        IReadOnlyList<string> extensionTypes)
    {
        var directoryPath = Path.GetDirectoryName(assemblyPath) ?? "Q:\\extensions";
        return new LoadedExtension
        {
            ExtensionId = extensionId,
            Name = name,
            Version = version,
            DirectoryPath = directoryPath,
            EntryAssemblyPath = assemblyPath,
            ExtensionTypes = [.. extensionTypes],
            LoadedAtUtc = DateTimeOffset.UtcNow,
            RegisteredServices = []
        };
    }
}
