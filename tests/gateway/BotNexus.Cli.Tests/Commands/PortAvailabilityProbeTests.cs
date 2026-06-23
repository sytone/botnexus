using System.Net;
using System.Net.Sockets;
using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for the CLI port-availability probe (issue #1536).
///
/// The gateway binds a wildcard address by default (http://0.0.0.0:5005, see
/// InitCommand.ListenUrl), so the availability probe must scope to the same
/// interface it will actually bind. A loopback-only probe (127.0.0.1) mis-detects
/// occupants that hold the port on the wildcard address or a non-loopback NIC,
/// producing either a confusing late Kestrel EADDRINUSE or a false "in use".
/// The probe therefore defaults to the wildcard address (IPAddress.Any) so it
/// detects an occupant on any interface.
/// </summary>
public sealed class PortAvailabilityProbeTests
{
    /// <summary>
    /// Reserve a free TCP port by binding to port 0, capture the assigned port,
    /// then release it so the probe can be exercised against a known-free port.
    /// </summary>
    private static int ReserveFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void IsPortAvailable_ReturnsTrue_WhenPortIsFree()
    {
        var port = ReserveFreePort();

        ServeCommand.IsPortAvailable(port).ShouldBeTrue();
    }

    [Fact]
    public void IsPortAvailable_ReturnsFalse_WhenPortHeldOnWildcardAddress()
    {
        var port = ReserveFreePort();
        using var occupant = new TcpListener(IPAddress.Any, port);
        occupant.Start();

        try
        {
            ServeCommand.IsPortAvailable(port).ShouldBeFalse();
        }
        finally
        {
            occupant.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_ReturnsFalse_WhenPortHeldOnLoopbackAddress()
    {
        // Regression for #1536: an occupant on 127.0.0.1 must be detected by the
        // default wildcard probe. A wildcard bind (IPAddress.Any) cannot succeed
        // while any interface (including loopback) holds the port.
        var port = ReserveFreePort();
        using var occupant = new TcpListener(IPAddress.Loopback, port);
        occupant.Server.ExclusiveAddressUse = true;
        occupant.Start();

        try
        {
            ServeCommand.IsPortAvailable(port).ShouldBeFalse();
        }
        finally
        {
            occupant.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_WithExplicitLoopback_ScopesProbeToThatInterface()
    {
        // A caller may scope the probe to a specific interface. Probing loopback
        // while the port is held only on loopback must report "in use".
        var port = ReserveFreePort();
        using var occupant = new TcpListener(IPAddress.Loopback, port);
        occupant.Server.ExclusiveAddressUse = true;
        occupant.Start();

        try
        {
            ServeCommand.IsPortAvailable(port, IPAddress.Loopback).ShouldBeFalse();
        }
        finally
        {
            occupant.Stop();
        }
    }

    [Fact]
    public void IsPortAvailable_WithExplicitBindAddress_ReturnsTrue_WhenPortIsFree()
    {
        var port = ReserveFreePort();

        ServeCommand.IsPortAvailable(port, IPAddress.Any).ShouldBeTrue();
        ServeCommand.IsPortAvailable(port, IPAddress.Loopback).ShouldBeTrue();
    }

    [Fact]
    public void UpdateCommand_IsPortAvailable_DelegatesToAlignedWildcardProbe()
    {
        // UpdateCommand previously carried a duplicate loopback-only probe.
        // It must now share the same wildcard-aligned probe so all three call
        // sites (ServeCommand, GatewayCommand, UpdateCommand) agree.
        var port = ReserveFreePort();
        using var occupant = new TcpListener(IPAddress.Any, port);
        occupant.Start();

        try
        {
            UpdateCommand.IsPortAvailable(port).ShouldBeFalse();
        }
        finally
        {
            occupant.Stop();
        }
    }

    [Fact]
    public void UpdateCommand_IsPortAvailable_ReturnsTrue_WhenPortIsFree()
    {
        var port = ReserveFreePort();

        UpdateCommand.IsPortAvailable(port).ShouldBeTrue();
    }
}
