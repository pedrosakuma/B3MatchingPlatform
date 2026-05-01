using System.Text;
using B3.Exchange.Host;

namespace B3.Exchange.Host.Tests;

public class HostConfigFirmRegistryTests
{
    private static string WriteTempJson(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"host-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Load_parses_firms_sessions_and_auth()
    {
        var json = """
        {
          "tcp": { "listen": "127.0.0.1:0" },
          "auth": { "devMode": true },
          "firms": [
            { "id": "FIRM01", "name": "Alpha Corretora", "enteringFirmCode": 100 },
            { "id": "FIRM02", "name": "Beta Corretora", "enteringFirmCode": 200 }
          ],
          "sessions": [
            { "sessionId": "SESS-A", "firmId": "FIRM01", "accessKey": "k1" },
            { "sessionId": "SESS-B", "firmId": "FIRM02", "accessKey": "k2",
              "policy": { "throttleMessagesPerSecond": 50, "keepAliveIntervalMs": 15000,
                          "idleTimeoutMs": 20000, "testRequestGraceMs": 3000,
                          "retransmitBufferSize": 5000 } }
          ],
          "channels": [
            { "channelNumber": 1, "instrumentsFile": "instruments.json",
              "umdf": { "group": "239.0.0.1", "port": 30001 } }
          ]
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var cfg = HostConfigLoader.Load(path);
            Assert.True(cfg.Auth.DevMode);
            Assert.Equal(2, cfg.Firms.Count);
            Assert.Equal(100u, cfg.Firms[0].EnteringFirmCode);
            Assert.Equal(2, cfg.Sessions.Count);
            Assert.Equal("FIRM02", cfg.Sessions[1].FirmId);
            Assert.NotNull(cfg.Sessions[1].Policy);
            Assert.Equal(50, cfg.Sessions[1].Policy!.ThrottleMessagesPerSecond);
            Assert.Equal(15000, cfg.Sessions[1].Policy!.KeepAliveIntervalMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_back_compat_uses_legacy_enteringFirm_when_no_firms()
    {
        var json = """
        {
          "tcp": { "listen": "127.0.0.1:0", "enteringFirm": 7 },
          "channels": [
            { "channelNumber": 1, "instrumentsFile": "instruments.json",
              "umdf": { "group": "239.0.0.1", "port": 30001 } }
          ]
        }
        """;
        var path = WriteTempJson(json);
        try
        {
            var cfg = HostConfigLoader.Load(path);
            Assert.Empty(cfg.Firms);
            Assert.Empty(cfg.Sessions);
            Assert.Equal(7u, cfg.Tcp.EnteringFirm);
            Assert.False(cfg.Auth.DevMode);
        }
        finally { File.Delete(path); }
    }
}
