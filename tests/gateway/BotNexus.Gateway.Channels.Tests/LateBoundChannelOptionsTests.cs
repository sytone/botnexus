using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using Shouldly;

namespace BotNexus.Gateway.Channels.Tests;

/// <summary>
/// Tests for <see cref="LateBoundChannelOptions{TOptions}"/> - the shared runtime-reload holder for
/// late-loaded channel extension options (issue #2010). Verifies the two behaviours the issue calls
/// out: options (a) bind correctly after dynamic load, and (b) pick up a subsequent config change
/// without a restart - while the cold-boot one-shot fallback (null IConfiguration) is preserved.
/// </summary>
public sealed class LateBoundChannelOptionsTests
{
    private sealed class SampleOptions
    {
        public string? Token { get; set; }
        public string Queue { get; set; } = "default-queue";
    }

    // Mirrors the adapters' static ResolveOptions: prefer the injected value when it already carries
    // auth material, otherwise bind from the config section. Local to the test so it exercises the
    // holder's re-resolution behaviour rather than one specific adapter.
    private static Func<SampleOptions> ResolverFrom(IConfiguration? configuration, SampleOptions injected)
        => () =>
        {
            if (!string.IsNullOrWhiteSpace(injected.Token))
                return injected;
            if (configuration is null)
                return injected;
            var bound = new SampleOptions();
            configuration.GetSection("channels:sample").Bind(bound);
            return bound;
        };

    // ── Happy path: binds after late load ─────────────────────────────────────

    [Fact]
    public void Current_BindsFromConfiguration_WhenInjectedOptionsEmpty()
    {
        // Simulates the live gateway: IOptions<T> is empty (extension loaded after the DI binding
        // pass) so the resolver must self-bind from IConfiguration under channels:sample.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:sample:token"] = "bound-from-config",
                ["channels:sample:queue"] = "inbound-q",
            })
            .Build();
        var injected = new SampleOptions();

        var holder = new LateBoundChannelOptions<SampleOptions>(ResolverFrom(config, injected), config);

        holder.Current.Token.ShouldBe("bound-from-config");
        holder.Current.Queue.ShouldBe("inbound-q");
    }

    // ── Reload: picks up a config change without restart ──────────────────────

    [Fact]
    public void Current_ReflectsConfigChange_WithoutRestart()
    {
        // A reloadable source stands in for config.json with reloadOnChange:true. Triggering its
        // reload token models a runtime config.json edit (portal edit / dynamic reload). The holder
        // must re-run the resolver so Current reflects the new value without constructing a new
        // adapter (i.e. without a gateway restart).
        var source = new ReloadableConfigurationSource(new Dictionary<string, string?>
        {
            ["channels:sample:token"] = "v1",
            ["channels:sample:queue"] = "q1",
        });
        var config = new ConfigurationBuilder().Add(source).Build();
        var injected = new SampleOptions();
        var holder = new LateBoundChannelOptions<SampleOptions>(ResolverFrom(config, injected), config);

        holder.Current.Token.ShouldBe("v1");
        holder.Current.Queue.ShouldBe("q1");

        source.Update(new Dictionary<string, string?>
        {
            ["channels:sample:token"] = "v2",
            ["channels:sample:queue"] = "q2",
        });

        holder.Current.Token.ShouldBe("v2");
        holder.Current.Queue.ShouldBe("q2");
    }

    // ── Sad path / cold-boot fallback preserved ───────────────────────────────

    [Fact]
    public void Current_ReturnsInjectedOptions_WhenConfigurationNull()
    {
        // Cold-boot / unit-test path: no IConfiguration. The one-shot injected value is used and
        // never reloads, exactly as before the reload mechanism was added.
        var injected = new SampleOptions { Token = "injected", Queue = "injected-q" };

        var holder = new LateBoundChannelOptions<SampleOptions>(
            ResolverFrom(configuration: null, injected), configuration: null);

        holder.Current.ShouldBeSameAs(injected);
        holder.Current.Token.ShouldBe("injected");
    }

    [Fact]
    public void Current_PrefersInjectedOptions_OverConfiguration_WhenAuthPresent()
    {
        // When IOptions already carries auth material, it wins and configuration is ignored - the
        // resolver contract every adapter relies on for cold boot.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["channels:sample:token"] = "should-be-ignored",
            })
            .Build();
        var injected = new SampleOptions { Token = "injected-token", Queue = "injected-q" };

        var holder = new LateBoundChannelOptions<SampleOptions>(ResolverFrom(config, injected), config);

        holder.Current.Token.ShouldBe("injected-token");
        holder.Current.Queue.ShouldBe("injected-q");
    }

    [Fact]
    public void Constructor_Throws_WhenResolverNull()
        => Should.Throw<ArgumentNullException>(() =>
            new LateBoundChannelOptions<SampleOptions>(resolve: null!, configuration: null));

    // ── Test double: a configuration source whose reload token can be fired on demand ──

    private sealed class ReloadableConfigurationSource(IReadOnlyDictionary<string, string?> initial)
        : IConfigurationSource
    {
        private readonly ReloadableConfigurationProvider _provider = new(initial);

        public void Update(IReadOnlyDictionary<string, string?> next) => _provider.Update(next);

        public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
    }

    private sealed class ReloadableConfigurationProvider(IReadOnlyDictionary<string, string?> initial)
        : ConfigurationProvider
    {
        private readonly IReadOnlyDictionary<string, string?> _initial = initial;
        private bool _loaded;

        public override void Load()
        {
            if (_loaded)
                return;
            Data = new Dictionary<string, string?>(_initial, StringComparer.OrdinalIgnoreCase);
            _loaded = true;
        }

        // Replaces the backing data and raises the change token, exactly as a file-watcher-backed
        // provider does when config.json is edited at runtime.
        public void Update(IReadOnlyDictionary<string, string?> next)
        {
            Data = new Dictionary<string, string?>(next, StringComparer.OrdinalIgnoreCase);
            OnReload();
        }
    }
}
