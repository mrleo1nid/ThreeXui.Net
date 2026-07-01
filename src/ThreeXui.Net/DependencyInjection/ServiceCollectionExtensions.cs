using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ThreeXui;
using ThreeXui.Http;

namespace ThreeXui.DependencyInjection;

/// <summary>
/// DI wiring for <see cref="IXuiClient"/>. Lets a host register a fully
/// configured 3x-ui client with a single call instead of hand-building the
/// <c>HttpClient</c> + credentials.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IXuiClient"/> for one 3x-ui backend.
    ///
    /// <para>
    /// The client is a singleton because a single <see cref="XuiClient"/> caches
    /// its cookie session and per-inbound mutexes across calls — sharing one
    /// instance is exactly what serializes concurrent client mutations and avoids
    /// duplicate logins. Its <c>HttpClient</c> (cookie container + TLS policy) is
    /// built once from the registered <see cref="IXuiHttpClientFactory"/>.
    /// </para>
    ///
    /// <para>
    /// A default <see cref="IXuiHttpClientFactory"/> (<see cref="XuiHttpClientFactory"/>)
    /// is registered if the host hasn't supplied one. An <c>ILogger&lt;XuiClient&gt;</c>
    /// is resolved from the container when logging is configured, otherwise the
    /// client logs to the null sink.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Callback that fills in the backend's URL, credentials and TLS policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    public static IServiceCollection AddXuiClient(
        this IServiceCollection services,
        Action<XuiClientOptions> configure
    )
    {
        if (services is null)
            throw new ArgumentNullException(nameof(services));
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        services.TryAddSingleton<IXuiHttpClientFactory, XuiHttpClientFactory>();

        services.AddSingleton<IXuiClient>(sp =>
        {
            var options = new XuiClientOptions();
            configure(options);
            Validate(options);

            var httpFactory = sp.GetRequiredService<IXuiHttpClientFactory>();
            var http = httpFactory.Create(
                options.BaseAddress!,
                options.AllowInsecureTls,
                options.Timeout
            );

            var logger = sp.GetService<ILogger<XuiClient>>();
            return new XuiClient(http, options.Username, options.Password, logger);
        });

        return services;
    }

    private static void Validate(XuiClientOptions options)
    {
        if (options.BaseAddress is null)
            throw new InvalidOperationException(
                $"{nameof(XuiClientOptions)}.{nameof(XuiClientOptions.BaseAddress)} is required."
            );
        if (!options.BaseAddress.IsAbsoluteUri)
            throw new InvalidOperationException(
                $"{nameof(XuiClientOptions)}.{nameof(XuiClientOptions.BaseAddress)} must be an absolute URI."
            );
        // Enforce the transport policy the library ships a validator for: HTTPS
        // is always fine; plain HTTP only to private/loopback hosts. A public
        // http:// panel would ship admin credentials in the clear, so reject it
        // here rather than silently allowing it.
        if (!XuiBaseUrlValidator.IsAllowed(options.BaseAddress.ToString(), out var reason))
            throw new InvalidOperationException(
                $"{nameof(XuiClientOptions)}.{nameof(XuiClientOptions.BaseAddress)} rejected: {reason}"
            );
        if (string.IsNullOrWhiteSpace(options.Username))
            throw new InvalidOperationException(
                $"{nameof(XuiClientOptions)}.{nameof(XuiClientOptions.Username)} is required."
            );
        if (string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException(
                $"{nameof(XuiClientOptions)}.{nameof(XuiClientOptions.Password)} is required."
            );
    }
}
