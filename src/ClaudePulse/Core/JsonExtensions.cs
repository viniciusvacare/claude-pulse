using System.Globalization;
using System.Text.Json;

namespace ClaudePulse.Core;

internal static class JsonExtensions
{
    public static bool Has(this JsonElement e, string name, out JsonElement value)
    {
        value = default;
        return e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty(name, out value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public static string? Str(this JsonElement e, string name) =>
        e.Has(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Long(this JsonElement e, string name) =>
        e.Has(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    public static double? Double(this JsonElement e, string name) =>
        e.Has(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.Has(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
