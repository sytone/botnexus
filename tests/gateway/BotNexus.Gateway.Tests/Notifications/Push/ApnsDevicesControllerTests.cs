using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Notifications.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace BotNexus.Gateway.Tests.Notifications.Push;

/// <summary>
/// Pins the endpoints a native iOS app calls to register itself.
/// </summary>
/// <remarks>
/// Validation matters more here than it looks. A token accepted now but malformed is refused by
/// Apple on every future notification, while the app that sent it goes on believing it is
/// registered - the same silent failure the web push endpoint had to be fixed for.
/// </remarks>
public sealed class ApnsDevicesControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "botnexus-apns-api", Guid.NewGuid().ToString("N"));

    private const string ValidToken = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private string DbPath => Path.Combine(_dir, "apns.sqlite");

    public ApnsDevicesControllerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(DbPath);
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory is not worth failing a test over.
        }
    }

    private IApnsDeviceStore Store() => new SqliteApnsDeviceStore(DbPath);

    private ApnsDevicesController Controller(ApnsOptions? options = null, IApnsDeviceStore? store = null) =>
        new(store ?? Store(), options ?? new ApnsOptions
        {
            TeamId = "TEAM123456",
            KeyId = "KEY1234567",
            BundleId = "com.example.botnexus",
            PrivateKeyPath = "/tmp/AuthKey.p8",
        })
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static int StatusOf(IActionResult result) =>
        Assert.IsAssignableFrom<IStatusCodeActionResult>(result).StatusCode ?? 0;

    // An app should be able to find out there is no point registering, rather than registering
    // into a void and waiting for notifications that can never arrive.
    [Fact]
    public void Status_says_when_the_gateway_cannot_push_to_ios()
    {
        var body = Assert.IsType<ApnsStatusResponse>(
            Assert.IsType<OkObjectResult>(Controller(new ApnsOptions()).Status().Result).Value);

        Assert.False(body.Configured);
        Assert.Null(body.BundleId);
    }

    [Fact]
    public void Status_reports_the_bundle_id_so_an_app_can_check_it_matches()
    {
        var body = Assert.IsType<ApnsStatusResponse>(
            Assert.IsType<OkObjectResult>(Controller().Status().Result).Value);

        Assert.True(body.Configured);
        Assert.Equal("com.example.botnexus", body.BundleId);
    }

    [Fact]
    public async Task Registers_a_well_formed_token()
    {
        var store = Store();

        var result = await Controller(store: store).Register(new ApnsRegisterRequest
        {
            DeviceToken = ValidToken,
            Environment = "sandbox",
            DeviceName = "Brad's iPhone",
        });

        Assert.Equal(204, StatusOf(result));

        var device = Assert.Single(await store.ListAsync());
        Assert.Equal(ValidToken, device.DeviceToken);
        Assert.Equal("sandbox", device.Environment);
        Assert.Equal("Brad's iPhone", device.DeviceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("zzzz2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    public async Task Refuses_a_token_that_is_not_a_device_token(string? token)
    {
        var result = await Controller().Register(new ApnsRegisterRequest
        {
            DeviceToken = token,
            Environment = "production",
        });

        Assert.Equal(400, StatusOf(result));
    }

    // The environment decides which Apple host the token is valid against, and guessing wrong is
    // refused as BadDeviceToken - which reads like a bad token rather than a wrong address.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("staging")]
    public async Task Refuses_an_environment_it_cannot_route(string? environment)
    {
        var result = await Controller().Register(new ApnsRegisterRequest
        {
            DeviceToken = ValidToken,
            Environment = environment,
        });

        Assert.Equal(400, StatusOf(result));
    }

    [Fact]
    public async Task Registering_twice_updates_rather_than_duplicating()
    {
        var store = Store();
        var controller = Controller(store: store);

        await controller.Register(new ApnsRegisterRequest
        {
            DeviceToken = ValidToken,
            Environment = "sandbox",
        });
        await controller.Register(new ApnsRegisterRequest
        {
            DeviceToken = ValidToken,
            Environment = "production",
        });

        var device = Assert.Single(await store.ListAsync());
        Assert.Equal("production", device.Environment);
    }

    [Fact]
    public async Task Unregistering_removes_the_device()
    {
        var store = Store();
        var controller = Controller(store: store);
        await controller.Register(new ApnsRegisterRequest
        {
            DeviceToken = ValidToken,
            Environment = "production",
        });

        await controller.Unregister(new ApnsUnregisterRequest { DeviceToken = ValidToken });

        Assert.Empty(await store.ListAsync());
    }

    // An app that unregisters twice, or that never registered, should not have to care which call
    // was the real one.
    [Fact]
    public async Task Unregistering_something_unknown_still_succeeds()
    {
        var result = await Controller().Unregister(new ApnsUnregisterRequest { DeviceToken = ValidToken });

        Assert.Equal(204, StatusOf(result));
    }
}
