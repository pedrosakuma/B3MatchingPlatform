using System.Text.Json;
using B3.Exchange.Core;
using B3.Exchange.Gateway;
using B3.Exchange.Host;

namespace B3.Exchange.Host.Tests;

public sealed class HostFixpSessionOptionsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void BuildSessionOptions_binds_suspendedTimeoutMs_from_tcp_config()
    {
        var cfg = ParseConfig("""
        { "suspendedTimeoutMs": 1500 }
        """);

        var options = BuildOptions(cfg.Tcp);

        Assert.Equal(1500, options.SuspendedTimeoutMs);
    }

    [Fact]
    public void BuildSessionOptions_uses_default_suspended_timeout_when_absent()
    {
        var cfg = ParseConfig("""
        { "heartbeatIntervalMs": 30000 }
        """);

        var options = BuildOptions(cfg.Tcp);

        Assert.Equal(FixpSessionOptions.Default.SuspendedTimeoutMs, options.SuspendedTimeoutMs);
    }

    private static HostConfig ParseConfig(string tcpJson)
    {
        var json = $$"""
        {
          "tcp": {{tcpJson}}
        }
        """;

        return JsonSerializer.Deserialize<HostConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("empty host config");
    }

    private static FixpSessionOptions BuildOptions(TcpConfig tcp)
    {
        return ExchangeHost.BuildSessionOptions(
            tcp,
            new MetricsRegistry(),
            static _ => null,
            static () => null);
    }
}
