using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
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
        payload.ShouldNotBeNull();
        payload.ShouldBeEmpty();
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
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-a", "ExtensionA.dll"),
                extensionTypes: ["channel"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.ShouldNotBeNull();
        payload!.ShouldHaveSingleItem();
        payload[0].ShouldBe(
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
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-a", "ExtensionA.dll"),
                extensionTypes: ["channel", "router"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.ShouldNotBeNull();
        payload!.Count().ShouldBe(2);
        payload.ShouldBe(new[]
        {
            new ExtensionResponse("Extension A", "1.2.3", "channel", "ExtensionA.dll"),
            new ExtensionResponse("Extension A", "1.2.3", "router", "ExtensionA.dll")
        });
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
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-a", "ExtensionA.dll"),
                extensionTypes: ["channel"]),
            CreateLoadedExtension(
                extensionId: "ext-b",
                name: "Extension B",
                version: "2.0.0",
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-b", "ExtensionB.dll"),
                extensionTypes: ["transport"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.ShouldNotBeNull();
        payload!.Count().ShouldBe(2);
        payload.Select(item => item.Name).ToList().ShouldBe(new[] { "Extension A", "Extension B" });
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
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-a", "ExtensionA.dll"),
                extensionTypes: [])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        var payload = (result.Result as OkObjectResult)?.Value as IReadOnlyList<ExtensionResponse>;
        payload.ShouldNotBeNull();
        payload!.ShouldHaveSingleItem();
        payload[0].Type.ShouldBe("unknown");
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
                assemblyPath: Path.Combine(Path.GetTempPath(), "extensions", "ext-a", "ExtensionA.dll"),
                extensionTypes: ["channel"])
        ]);

        var controller = new ExtensionsController(loader.Object);

        var result = controller.List();

        result.Result.ShouldBeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        ok.Value.ShouldBeAssignableTo<IReadOnlyList<ExtensionResponse>>();
        ((IReadOnlyList<ExtensionResponse>)ok.Value!).ShouldAllBe(item => item is ExtensionResponse);
    }

    private static LoadedExtension CreateLoadedExtension(
        string extensionId,
        string name,
        string version,
        string assemblyPath,
        IReadOnlyList<string> extensionTypes)
    {
        var directoryPath = Path.GetDirectoryName(assemblyPath) ?? Path.Combine(Path.GetTempPath(), "extensions");
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
