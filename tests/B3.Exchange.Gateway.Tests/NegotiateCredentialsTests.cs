using B3.EntryPoint.Wire;
using System.Text;
using B3.Exchange.Gateway;

namespace B3.Exchange.Gateway.Tests;

public class NegotiateCredentialsTests
{
    private static ReadOnlySpan<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Parses_valid_basic_credentials()
    {
        var json = Utf8("{\"auth_type\":\"basic\",\"username\":\"10101\",\"access_key\":\"secret\"}");
        Assert.True(NegotiateCredentials.TryParse(json, out var c, out var err));
        Assert.Null(err);
        Assert.Equal("basic", c.AuthType);
        Assert.Equal("10101", c.Username);
        Assert.Equal("secret", c.AccessKey);
    }

    [Fact]
    public void Empty_input_rejected()
    {
        Assert.False(NegotiateCredentials.TryParse(default, out _, out var err));
        Assert.Contains("empty", err);
    }

    [Fact]
    public void Oversize_rejected()
    {
        var json = Utf8("{\"auth_type\":\"basic\",\"username\":\"u\",\"access_key\":\"" + new string('x', 200) + "\"}");
        Assert.False(NegotiateCredentials.TryParse(json, out _, out var err));
        Assert.Contains("max", err);
    }

    [Fact]
    public void Non_object_root_rejected()
    {
        Assert.False(NegotiateCredentials.TryParse(Utf8("\"basic\""), out _, out var err));
        Assert.Contains("not a JSON object", err);
    }

    [Fact]
    public void Missing_field_rejected()
    {
        var json = Utf8("{\"auth_type\":\"basic\",\"username\":\"u\"}");
        Assert.False(NegotiateCredentials.TryParse(json, out _, out var err));
        Assert.Contains("missing required field", err);
    }

    [Fact]
    public void Non_ascii_rejected()
    {
        var json = Utf8("{\"auth_type\":\"basic\",\"username\":\"héllo\",\"access_key\":\"k\"}");
        Assert.False(NegotiateCredentials.TryParse(json, out _, out var err));
        Assert.Contains("non-printable-ASCII", err);
    }

    [Fact]
    public void Malformed_json_rejected()
    {
        Assert.False(NegotiateCredentials.TryParse(Utf8("{\"auth_type\":}"), out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void Empty_username_rejected_as_missing_required()
    {
        var json = Utf8("{\"auth_type\":\"basic\",\"username\":\"\",\"access_key\":\"k\"}");
        Assert.False(NegotiateCredentials.TryParse(json, out _, out var err));
        Assert.Contains("missing required field", err);
    }
}
