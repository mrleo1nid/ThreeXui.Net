using System;

namespace ThreeXui;

/// <summary>
/// Tiny argument guards used across the client. Kept framework-agnostic so the
/// same call site compiles under both target frameworks (the BCL
/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> only exists on .NET 7+).
/// </summary>
internal static class Throw
{
    public static void IfNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or whitespace.", paramName);
    }
}

#if NETSTANDARD2_0
/// <summary>
/// netstandard2.0 shims for BCL methods that only gained their modern overloads
/// on .NET Core / .NET 5+. On net10.0 the in-box instance methods take priority
/// over these extensions, so this file is compiled out of that target.
/// </summary>
internal static class CompatExtensions
{
    public static bool Contains(this string source, string value, StringComparison comparison) =>
        source.IndexOf(value, comparison) >= 0;

    public static System.Threading.Tasks.Task<string> ReadAsStringAsync(
        this System.Net.Http.HttpContent content,
        System.Threading.CancellationToken cancellationToken
    ) => content.ReadAsStringAsync();
}
#endif
