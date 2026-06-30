using FluentAssertions;
using ThreeXui;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Covers the BaseUrl scheme + host policy. The validator is the
/// only gate between "admin pastes an external URL" and a successful
/// XuiBackend insert/update, so the table of accepted/rejected values is
/// dense on purpose.
/// </summary>
public class XuiBaseUrlValidatorTests
{
    [Theory]
    [InlineData("https://panel.example.com")]
    [InlineData("https://xui.internal.corp:2053")]
    [InlineData("http://localhost:2053")]
    [InlineData("http://127.0.0.1:2053")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://host.docker.internal:2053")]
    [InlineData("http://10.0.0.5:8443")]
    [InlineData("http://172.16.0.1:2053")]
    [InlineData("http://172.31.255.254")]
    [InlineData("http://192.168.1.1:2053")]
    [InlineData("http://my-xui.local")]
    [InlineData("http://[::1]")]
    public void Accepts_PrivateAndHttps_Urls(string url)
    {
        var ok = XuiBaseUrlValidator.IsAllowed(url, out var reason);
        ok.Should().BeTrue(reason);
    }

    [Theory]
    [InlineData("http://1.2.3.4:2053", "private/loopback")]
    [InlineData("http://example.com", "private/loopback")]
    [InlineData("http://panel.example.com:2053", "private/loopback")]
    [InlineData("http://172.32.0.1:2053", "private/loopback")] // edge — just outside 172.16/12.
    [InlineData("http://172.15.255.254", "private/loopback")] // edge — just outside 172.16/12 low side.
    [InlineData("http://11.0.0.5", "private/loopback")] // 11.x is public.
    public void Rejects_PublicHttpUrls(string url, string fragment)
    {
        var ok = XuiBaseUrlValidator.IsAllowed(url, out var reason);
        ok.Should().BeFalse();
        reason.Should().NotBeNull();
        reason!.Should().Contain(fragment);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://10.0.0.1")] // unsupported scheme
    [InlineData("//10.0.0.1")] // relative
    public void Rejects_MalformedOrUnsupportedSchemes(string url)
    {
        var ok = XuiBaseUrlValidator.IsAllowed(url, out var reason);
        ok.Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }
}
