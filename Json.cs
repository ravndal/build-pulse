using System.Globalization;
using System.Text.Json.Nodes;

namespace DevOpsWatcher;

/// <summary>Raised by pipeline sources; <see cref="StatusCode"/> is the HTTP status, or 0 for network errors.</summary>
public sealed class PipelineException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Lenient readers for REST JSON: missing or odd-typed values become empty/zero instead of throwing.</summary>
static class Json
{
    public static string Str(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => n.ToJsonString(),
    };

    public static int Int(JsonNode? n) => int.TryParse(Str(n), out var i) ? i : 0;

    public static long Long(JsonNode? n) => long.TryParse(Str(n), out var i) ? i : 0;

    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string? NullIfEmpty(string s) => s == "" ? null : s;

    public static DateTime? Date(JsonNode? n) =>
        DateTime.TryParse(Str(n), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.Kind == DateTimeKind.Local ? d : d.ToLocalTime()
            : null;

    public static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow,
        _ => null,
    };

    /// <summary>The "message" of a JSON error body, if there is one.</summary>
    public static string? ErrorMessage(string text)
    {
        try { return JsonNode.Parse(text).N("message") is { } m ? Str(m) : null; }
        catch { return null; }
    }
}

static class JsonExtensions
{
    /// <summary>Null-safe property access that tolerates non-object nodes.</summary>
    public static JsonNode? N(this JsonNode? node, string key) => (node as JsonObject)?[key];
}
