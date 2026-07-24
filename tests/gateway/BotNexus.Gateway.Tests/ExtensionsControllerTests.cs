using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Diagnostics;
using Microsoft.AspNetCore.Http;
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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

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

        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

        var result = controller.List();

        result.Result.ShouldBeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result.Result!;
        ok.Value.ShouldBeAssignableTo<IReadOnlyList<ExtensionResponse>>();
        ((IReadOnlyList<ExtensionResponse>)ok.Value!).ShouldAllBe(item => item is ExtensionResponse);
    }

    [Fact]
    public void Health_WithNoRecordedResults_ReturnsOkWithZeroCounts()
    {
        var loader = new Mock<IExtensionLoader>();
        var controller = new ExtensionsController(loader.Object, new ExtensionBootReport());

        var result = controller.Health();

        var ok = result.Result as OkObjectResult;
        ok.ShouldNotBeNull();
        var payload = ok!.Value as ExtensionHealthResponse;
        payload.ShouldNotBeNull();
        payload!.Status.ShouldBe("ok");
        payload.LoadedCount.ShouldBe(0);
        payload.FailedCount.ShouldBe(0);
        payload.Failed.ShouldBeEmpty();
    }

    [Fact]
    public void Health_WithAllExtensionsLoaded_ReturnsOk()
    {
        var loader = new Mock<IExtensionLoader>();
        var report = new ExtensionBootReport();
        report.Record(
        [
            new ExtensionLoadResult { ExtensionId = "ext-a", Success = true },
            new ExtensionLoadResult { ExtensionId = "ext-b", Success = true }
        ]);
        var controller = new ExtensionsController(loader.Object, report);

        var result = controller.Health();

        var ok = result.Result as OkObjectResult;
        ok.ShouldNotBeNull();
        var payload = (ExtensionHealthResponse)ok!.Value!;
        payload.Status.ShouldBe("ok");
        payload.LoadedCount.ShouldBe(2);
        payload.FailedCount.ShouldBe(0);
    }

    [Fact]
    public void Health_WithFailedExtension_Returns503AndNamesOffendingAssembly()
    {
        var loader = new Mock<IExtensionLoader>();
        var report = new ExtensionBootReport();
        report.Record(
        [
            new ExtensionLoadResult { ExtensionId = "ext-a", Success = true },
            new ExtensionLoadResult
            {
                ExtensionId = "botnexus-servicebus",
                Success = false,
                Error = "Could not load file or assembly 'Azure.Messaging.ServiceBus'."
            }
        ]);
        var controller = new ExtensionsController(loader.Object, report);

        var result = controller.Health();

        // ActionResult<T> surfaces a non-2xx status via ObjectResult in Result.
        var objectResult = result.Result as ObjectResult;
        objectResult.ShouldNotBeNull();
        objectResult!.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);

        var payload = (ExtensionHealthResponse)objectResult.Value!;
        payload.Status.ShouldBe("failed");
        payload.LoadedCount.ShouldBe(1);
        payload.FailedCount.ShouldBe(1);
        var failure = payload.Failed.ShouldHaveSingleItem();
        failure.Id.ShouldBe("botnexus-servicebus");
        // The real load error must be surfaced verbatim so the smoke gate names the
        // diverged/missing assembly instead of reporting a generic timeout.
        failure.Error.ShouldContain("Azure.Messaging.ServiceBus");
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
