using System.Text.Json;

namespace ThreeXui.ConnectionStrings;

/// <summary>
/// Shared JSON-poking helper used by every "extract a field from the 3x-ui
/// settings blob" path. The relevant client inside <c>settings.clients[]</c> is
/// the one matching the stored <c>ExternalClientId</c> — match-field depends on
/// protocol.
///
/// <list type="bullet">
///   <item>If the settings JSON is missing / malformed, return null — caller
///         falls back to its protocol-specific default.</item>
///   <item>If the <c>clients</c> array is missing or empty, return null.</item>
///   <item>If the requested field is present but not a string / number, coerce
///         to its <c>ToString()</c> — connection-string consumers all want a
///         string anyway.</item>
/// </list>
/// </summary>
internal static class SettingsExtractor
{
    /// <summary>
    /// Finds the client whose <paramref name="matchFieldName"/> equals
    /// <paramref name="matchValue"/>, then returns
    /// <paramref name="targetFieldName"/> from that same row.
    /// </summary>
    public static string? GetClientFieldByMatch(
        string? settingsJson,
        string matchFieldName,
        string matchValue,
        string targetFieldName
    )
    {
        if (string.IsNullOrWhiteSpace(settingsJson) || string.IsNullOrWhiteSpace(matchValue))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson!);
            if (!doc.RootElement.TryGetProperty("clients", out var clients))
                return null;
            if (clients.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var client in clients.EnumerateArray())
            {
                if (!client.TryGetProperty(matchFieldName, out var fieldValue))
                    continue;
                var fieldString = ReadAsString(fieldValue);
                if (fieldString is null)
                    continue;
                if (!string.Equals(fieldString, matchValue, StringComparison.Ordinal))
                    continue;
                if (!client.TryGetProperty(targetFieldName, out var target))
                    return null;
                return ReadAsString(target);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the first client's <paramref name="fieldName"/>. Used by
    /// shadowsocks (top-level single-client shape) and as a diagnostic fallback
    /// when match-by-id failed.
    /// </summary>
    public static string? GetFirstClientField(string? settingsJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson!);
            if (!doc.RootElement.TryGetProperty("clients", out var clients))
                return null;
            if (clients.ValueKind != JsonValueKind.Array || clients.GetArrayLength() == 0)
                return null;
            var first = clients[0];
            if (!first.TryGetProperty(fieldName, out var value))
                return null;
            return ReadAsString(value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadAsString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
}
