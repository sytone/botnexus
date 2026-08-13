using System.Net;
using System.Net.Sockets;
using BotNexus.Cli.Commands;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for the CLI port-availability probe (issue #1536).
///
/// <para>
/// The gateway binds a wildcard address by default (http://0.0.0.0:5005, see
/// InitCommand.ListenUrl), so the availability probe must scope to the same
/// interface it will actually bind. A loopback-only probe (127.0.0.1) mis-detects
/// occupants that hold the port on the wildcard address or a non-loopback NIC,
/// producing either a confusing late Kestrel EADDRINUSE or a false "in use".
/// The probe therefore defaults to the wildcard address so it detects an occupant
/// on any interface.
/// </para>
/// <para>
/// That default is now pinned directly and without any socket by
/// <see cref="PortProbeDefaultAddressTests"/> (#2797). The tests that used to cover it
/// here did so by opening a real wildcard socket, which asserts kernel conflict
/// semantics this repository does not implement and writes a permanent inbound Windows
/// firewall rule per worktree. What remains here is the loopback-scoped behaviour,
/// unmodified: a probe explicitly scoped to one interface, exercised for real.
/// </para>
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

        ServeCommand.IsPortAvailable(port, IPAddress.Loopback).ShouldBeTrue();
    }
}
