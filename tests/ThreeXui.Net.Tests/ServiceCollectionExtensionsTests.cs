using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ThreeXui;
using ThreeXui.DependencyInjection;
using ThreeXui.Http;
using Xunit;

namespace ThreeXui.Net.Tests;

/// <summary>
/// Tests for the <c>AddXuiClient</c> DI extension: resolution, singleton
/// lifetime, options validation, and respecting a host-supplied HTTP factory.
/// </summary>
public class ServiceCollectionExtensionsTests
{
    private static void ConfigureValid(XuiClientOptions o)
    {
        o.BaseAddress = new Uri("https://panel.example.com:2053/");
        o.Username = "admin";
        o.Password = "secret";
    }

    [Fact]
    public void AddXuiClient_ResolvesIXuiClient()
    {
        var services = new ServiceCollection();
        services.AddXuiClient(ConfigureValid);

        using var provider = services.BuildServiceProvider();

        provider.GetService<IXuiClient>().Should().NotBeNull().And.BeOfType<XuiClient>();
    }

    [Fact]
    public void AddXuiClient_RegistersSingleton()
    {
        var services = new ServiceCollection();
        services.AddXuiClient(ConfigureValid);

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IXuiClient>();
        var second = provider.GetRequiredService<IXuiClient>();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AddXuiClient_RegistersDefaultHttpFactory()
    {
        var services = new ServiceCollection();
        services.AddXuiClient(ConfigureValid);

        using var provider = services.BuildServiceProvider();

        provider
            .GetService<IXuiHttpClientFactory>()
            .Should()
            .NotBeNull()
            .And.BeOfType<XuiHttpClientFactory>();
    }

    [Fact]
    public void AddXuiClient_KeepsHostSuppliedHttpFactory()
    {
        var custom = new XuiHttpClientFactory();
        var services = new ServiceCollection();
        services.AddSingleton<IXuiHttpClientFactory>(custom);
        services.AddXuiClient(ConfigureValid);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IXuiHttpClientFactory>().Should().BeSameAs(custom);
    }

    [Fact]
    public void AddXuiClient_ThrowsWhenBaseAddressMissing()
    {
        var services = new ServiceCollection();
        services.AddXuiClient(o =>
        {
            o.Username = "admin";
            o.Password = "secret";
        });

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IXuiClient>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*BaseAddress*");
    }

    [Fact]
    public void AddXuiClient_ThrowsWhenCredentialsMissing()
    {
        var services = new ServiceCollection();
        services.AddXuiClient(o => o.BaseAddress = new Uri("https://panel.example.com/"));

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IXuiClient>();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddXuiClient_ThrowsOnNullArguments()
    {
        var services = new ServiceCollection();

        var nullServices = () =>
            ServiceCollectionExtensions.AddXuiClient(null!, ConfigureValid);
        var nullConfigure = () => services.AddXuiClient(null!);

        nullServices.Should().Throw<ArgumentNullException>();
        nullConfigure.Should().Throw<ArgumentNullException>();
    }
}
