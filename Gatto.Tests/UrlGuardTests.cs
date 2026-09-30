using Gatto.Core.Web;
using System.Net;

namespace Gatto.Tests;

public class UrlGuardTests
{
    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ftp://example.com/x")]
    [InlineData("gopher://example.com/x")]
    public void Validate_BlocksNonHttpSchemes(string url) =>
        Assert.Contains("http", Assert.Throws<GattoFetchException>(() => UrlGuard.Validate(url)).Message);

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://2130706433/")]        //decimal 127.0.0.1, the parser normalizes it
    [InlineData("http://0x7f.0.0.1/")]        //hex first octet
    [InlineData("http://[::1]/")]
    [InlineData("http://[::ffff:10.0.0.5]/")] //v4-mapped v6
    [InlineData("http://[::127.0.0.1]/")]     //v4-compatible v6 (::/96), unwrapped like mapped
    [InlineData("http://169.254.169.254/")]   //link-local, the metadata endpoint shape
    public void Validate_BlocksLiteralPrivateIps(string url) =>
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate(url));

    [Theory]
    [InlineData("10.1.2.3", true)] [InlineData("172.16.0.1", true)] [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)] [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)] [InlineData("ff02::1", true)] [InlineData("2606:4700::1111", false)]
    //CGNAT 100.64.0.0/10, blocked across the range and open on either side
    [InlineData("100.64.0.0", true)] [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)] [InlineData("100.128.0.1", false)]
    //class E 240.0.0.0/4, which covers broadcast
    [InlineData("240.0.0.1", true)] [InlineData("255.255.255.255", true)] [InlineData("239.255.255.255", true)]
    //a v4-compatible v6 address (::/96), an embedded blocked v4 blocks and an embedded public v4 stays open
    [InlineData("::127.0.0.1", true)] [InlineData("::10.0.0.5", true)] [InlineData("::8.8.8.8", false)]
    [InlineData("::", true)]          //unspecified, the embedded 0.0.0.0 is blocked
    public void IsBlocked_Table(string ip, bool blocked) =>
        Assert.Equal(blocked, UrlGuard.IsBlocked(IPAddress.Parse(ip)));

    [Fact]
    public void ValidateResolved_OneBadAddressPoisonsAll()
    {
        var addrs = new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.1") };
        Assert.Throws<GattoFetchException>(() => UrlGuard.ValidateResolved(addrs, "evil.example"));
    }

    //exact-origin allowlist

    [Fact]
    public void Origin_normalizes_scheme_host_port()
    {
        Assert.Equal("http://pi.local:8888", UrlGuard.Origin(new Uri("HTTP://PI.LOCAL:8888/search?q=x")));
        Assert.Equal("https://example.com:443", UrlGuard.Origin(new Uri("https://example.com/")));
        Assert.Equal("http://10.0.0.50:80", UrlGuard.Origin(new Uri("http://10.0.0.50/")));
    }

    [Fact]
    public void Validate_allows_exact_allowed_origin_only()
    {
        var allowed = new HashSet<string> { "http://10.0.0.50:8888" };

        var uri = UrlGuard.Validate("http://10.0.0.50:8888/search?q=x", allowed);
        Assert.Equal("10.0.0.50", uri.Host);

        //a wrong port is still blocked
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate("http://10.0.0.50:9999/", allowed));
        //a wrong host on the same subnet is still blocked
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate("http://10.0.0.51:8888/", allowed));
        //the origin includes the scheme, so https on the right host and port is blocked
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate("https://10.0.0.50:8888/", allowed));
        //a non-http scheme is rejected before the allowlist is consulted
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate("ftp://10.0.0.50:8888/", new HashSet<string> { "ftp://10.0.0.50:8888" }));
        //a null set leaves the private address rules in charge
        Assert.Throws<GattoFetchException>(() => UrlGuard.Validate("http://10.0.0.50:8888/", null));
    }
}
