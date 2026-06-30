namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Resolves <see cref="IXuiConnectionStringBuilder"/> by protocol string. Indexes
/// the supplied builders at construction time so the lookup is O(1) and
/// case-insensitive. Stateless after construction — the underlying builders are
/// stateless too.
/// </summary>
public sealed class XuiConnectionStringBuilderResolver : IXuiConnectionStringBuilderResolver
{
    private readonly IReadOnlyDictionary<string, IXuiConnectionStringBuilder> _byProtocol;

    /// <summary>
    /// Builds a resolver over the supplied builders. With no arguments, registers
    /// the four built-in protocol builders (vless/vmess/trojan/shadowsocks).
    /// </summary>
    public XuiConnectionStringBuilderResolver(
        IEnumerable<IXuiConnectionStringBuilder>? builders = null
    )
    {
        builders ??= Default();
        _byProtocol = builders.ToDictionary(b => b.Protocol, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The four built-in protocol builders.</summary>
    public static IReadOnlyList<IXuiConnectionStringBuilder> Default() =>
        new IXuiConnectionStringBuilder[]
        {
            new VlessConnectionStringBuilder(),
            new VmessConnectionStringBuilder(),
            new TrojanConnectionStringBuilder(),
            new ShadowsocksConnectionStringBuilder(),
        };

    public IXuiConnectionStringBuilder? Resolve(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            return null;
        return _byProtocol.TryGetValue(protocol, out var b) ? b : null;
    }
}
