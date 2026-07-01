using System;

namespace ThreeXui.DependencyInjection;

/// <summary>
/// Options for a single 3x-ui backend registered through
/// <see cref="ServiceCollectionExtensions.AddXuiClient(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{XuiClientOptions})"/>.
///
/// <para>
/// One <see cref="XuiClient"/> talks to exactly one panel, so a registration
/// carries the panel's base URL, the admin credentials, and the per-backend TLS
/// policy. The values are typically bound from configuration / secrets by the
/// host application.
/// </para>
/// </summary>
public sealed class XuiClientOptions
{
    /// <summary>
    /// The 3x-ui panel API root, e.g. <c>https://panel.example.com:2053/</c>.
    /// Must be an absolute URI. Required.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Admin username used for the cookie-session login. Required.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Admin password (plaintext) used for the cookie-session login. Required.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// When <c>true</c>, TLS certificate validation is skipped for this backend
    /// (self-signed panels). Defaults to <c>false</c>.
    ///
    /// <para>
    /// On .NET (Core) / net10.0 this always works. On the netstandard2.0 target
    /// running under .NET Framework, skipping validation requires 4.7.1+ (the
    /// underlying <c>HttpClientHandler.ServerCertificateCustomValidationCallback</c>
    /// throws <c>PlatformNotSupportedException</c> on 4.6.1–4.7.0).
    /// </para>
    /// </summary>
    public bool AllowInsecureTls { get; set; }

    /// <summary>
    /// Per-request HTTP timeout. When <c>null</c> the factory default (30s) is
    /// used.
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}
