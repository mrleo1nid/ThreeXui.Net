namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Inputs for building a single client's connection string. Decoupled from any
/// host application's domain entities — the caller maps its own config / inbound
/// / backend objects into this shape.
/// </summary>
/// <param name="ExternalClientId">
/// The secret/identifier matching the client inside <c>settings.clients[]</c>
/// (UUID for vless/vmess, password for trojan; ignored for the single-client
/// shadowsocks shape).
/// </param>
/// <param name="Name">User-visible label, used as the share-link fragment (<c>#name</c>).</param>
/// <param name="InboundPort">The inbound's listening port (used unless an externalProxy entry overrides it).</param>
/// <param name="PublicHost">
/// Preferred public host to advertise in the link. When null/blank the host
/// portion of <paramref name="BaseUrl"/> is used instead.
/// </param>
/// <param name="BaseUrl">The backend base URL — its host is the fallback when <paramref name="PublicHost"/> is blank.</param>
/// <param name="Inbound">The 3x-ui inbound (carries the verbatim <c>settings</c> + <c>streamSettings</c> blobs).</param>
public sealed record XuiConnectionStringRequest(
    string ExternalClientId,
    string Name,
    int InboundPort,
    string? PublicHost,
    string BaseUrl,
    XuiInboundDto Inbound
);

/// <summary>
/// Strategy that turns a single client inside a 3x-ui inbound into a
/// protocol-specific connection string (paste-able into v2rayN / Streisand /
/// Shadowrocket). One implementation per protocol.
///
/// <para>
/// The implementation reads the matching client out of
/// <c>settings.clients[]</c> by <see cref="XuiConnectionStringRequest.ExternalClientId"/>
/// (per-protocol match-field — UUID for vless/vmess, password for trojan, the
/// top-level shape for shadowsocks). If the client can't be matched the builder
/// falls back to a well-formed-but-non-connectable string so the user sees a
/// diagnostic surface rather than an exception.
/// </para>
/// </summary>
public interface IXuiConnectionStringBuilder
{
    /// <summary>Protocol this builder owns. Matched case-insensitively by the resolver.</summary>
    string Protocol { get; }

    /// <summary>Builds the protocol-specific connection string for the requested client.</summary>
    string Build(XuiConnectionStringRequest request);
}

/// <summary>
/// Resolves the right <see cref="IXuiConnectionStringBuilder"/> for a given
/// protocol string.
/// </summary>
public interface IXuiConnectionStringBuilderResolver
{
    /// <summary>
    /// Returns the builder owning <paramref name="protocol"/>, or <c>null</c> if
    /// none is registered. Case-insensitive.
    /// </summary>
    IXuiConnectionStringBuilder? Resolve(string protocol);
}
