using FluentAssertions;
using ThreeXui.ConnectionStrings;
using ThreeXui.Http;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Smoke tests for the library-only surface: the HttpClient factory and the
/// connection-string resolver's built-in registry.
/// </summary>
public class XuiHttpClientFactoryTests
{
    [Fact]
    public void Create_SetsBaseAddressAndDefaultTimeout()
    {
        var factory = new XuiHttpClientFactory();

        using var http = factory.Create(new Uri("https://panel.example.com:2053/"), allowInsecureTls: false);

        http.BaseAddress.Should().Be(new Uri("https://panel.example.com:2053/"));
        http.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Create_HonoursExplicitTimeout()
    {
        var factory = new XuiHttpClientFactory();

        using var http = factory.Create(
            new Uri("https://panel.example.com/"),
            allowInsecureTls: true,
            timeout: TimeSpan.FromSeconds(5)
        );

        http.Timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Create_RejectsRelativeBaseAddress()
    {
        var factory = new XuiHttpClientFactory();

        var act = () => factory.Create(new Uri("/relative", UriKind.Relative), allowInsecureTls: false);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("vless")]
    [InlineData("VMESS")]
    [InlineData("Trojan")]
    [InlineData("shadowsocks")]
    public void Resolver_ResolvesBuiltInBuilders_CaseInsensitive(string protocol)
    {
        var resolver = new XuiConnectionStringBuilderResolver();

        resolver.Resolve(protocol).Should().NotBeNull();
    }

    [Fact]
    public void Resolver_ReturnsNull_ForUnknownProtocol()
    {
        var resolver = new XuiConnectionStringBuilderResolver();

        resolver.Resolve("wireguard").Should().BeNull();
    }
}
