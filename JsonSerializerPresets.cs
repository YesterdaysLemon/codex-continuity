using System.Text.Json;

namespace CodexContinuity;

internal static class ContinuityJsonSerializerPresets
{
    internal static JsonSerializerOptions CamelCaseIndented() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
