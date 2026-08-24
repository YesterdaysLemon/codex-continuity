using System.Text.Json;

namespace CodexContinuity;

internal enum ContinuityUpdateApplyLoadKind
{
    Missing,
    Loaded,
    Invalid,
    UnsupportedSchema,
    Unreadable,
}

internal sealed record ContinuityUpdateApplyPolicy(
    int SchemaVersion,
    bool AutomaticApplyWhenIdle,
    long Generation,
    DateTimeOffset UpdatedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;

    internal static ContinuityUpdateApplyPolicy Default(DateTimeOffset nowUtc) => new(
        CurrentSchemaVersion,
        AutomaticApplyWhenIdle: false,
        Generation: 0,
        UpdatedAtUtc: nowUtc);

    internal ContinuityUpdateApplyPolicy WithAutomaticApply(
        bool enabled,
        DateTimeOffset nowUtc) => this with
        {
            AutomaticApplyWhenIdle = enabled,
            Generation = checked(Generation + 1),
            UpdatedAtUtc = nowUtc,
        };
}

internal sealed record ContinuityUpdateApplyPolicyLoadResult(
    ContinuityUpdateApplyLoadKind Kind,
    ContinuityUpdateApplyPolicy? Policy);

internal sealed class ContinuityUpdateApplyPolicyStore(string path)
{
    private const int MaximumBytes = 16 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal ContinuityUpdateApplyPolicyLoadResult Load()
    {
        try
        {
            using var stateFile = BoundedStateFile.Open(path, MaximumBytes);
            var bytes = stateFile.Read();
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion))
            {
                return Invalid();
            }
            if (schemaVersion != ContinuityUpdateApplyPolicy.CurrentSchemaVersion)
            {
                return new(ContinuityUpdateApplyLoadKind.UnsupportedSchema, Policy: null);
            }
            var policy = JsonSerializer.Deserialize<ContinuityUpdateApplyPolicy>(
                bytes.Span,
                SerializerOptions);
            return IsValid(policy)
                ? new(ContinuityUpdateApplyLoadKind.Loaded, policy)
                : Invalid();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ContinuityUpdateApplyLoadKind.Missing, Policy: null);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return Invalid();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(ContinuityUpdateApplyLoadKind.Unreadable, Policy: null);
        }
    }

    internal void Save(ContinuityUpdateApplyPolicy policy)
    {
        if (!IsValid(policy))
        {
            throw new ArgumentException("Update apply policy is invalid.", nameof(policy));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(policy, SerializerOptions);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("Update apply policy exceeds its persisted limit.");
        }
        BoundedStateFile.WriteAtomically(path, bytes);
    }

    private static bool IsValid(ContinuityUpdateApplyPolicy? policy) => policy is
    {
        SchemaVersion: ContinuityUpdateApplyPolicy.CurrentSchemaVersion,
        Generation: >= 0,
    } && policy.UpdatedAtUtc != default;

    private static ContinuityUpdateApplyPolicyLoadResult Invalid() => new(
        ContinuityUpdateApplyLoadKind.Invalid,
        Policy: null);
}

internal static class ContinuityUpdateApplyStates
{
    internal const string StagedOnly = "stagedOnly";
    internal const string Waiting = "waiting";
    internal const string Applying = "applying";
    internal const string Active = "active";
    internal const string RolledBack = "rolledBack";
    internal const string Failed = "failed";

    internal static bool IsKnown(string state) => state is
        StagedOnly or Waiting or Applying or Active or RolledBack or Failed;
}

internal sealed record ContinuityUpdateApplyStatus(
    int SchemaVersion,
    string State,
    long PolicyGeneration,
    string? TargetVersion,
    string? TargetExecutableSha256,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? IdleSinceUtc,
    string? HandoffId,
    string? LastError)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed record ContinuityUpdateApplyStatusLoadResult(
    ContinuityUpdateApplyLoadKind Kind,
    ContinuityUpdateApplyStatus? Status);

internal sealed class ContinuityUpdateApplyStatusStore(string path)
{
    private const int MaximumBytes = 32 * 1024;
    private const int MaximumErrorCharacters = 2048;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal ContinuityUpdateApplyStatusLoadResult Load()
    {
        try
        {
            using var stateFile = BoundedStateFile.Open(path, MaximumBytes);
            var bytes = stateFile.Read();
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                !schema.TryGetInt32(out var schemaVersion))
            {
                return Invalid();
            }
            if (schemaVersion != ContinuityUpdateApplyStatus.CurrentSchemaVersion)
            {
                return new(ContinuityUpdateApplyLoadKind.UnsupportedSchema, Status: null);
            }
            var status = JsonSerializer.Deserialize<ContinuityUpdateApplyStatus>(
                bytes.Span,
                SerializerOptions);
            return IsValid(status)
                ? new(ContinuityUpdateApplyLoadKind.Loaded, status)
                : Invalid();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(ContinuityUpdateApplyLoadKind.Missing, Status: null);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or ArgumentException or
                NotSupportedException or PathTooLongException)
        {
            return Invalid();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(ContinuityUpdateApplyLoadKind.Unreadable, Status: null);
        }
    }

    internal void Save(ContinuityUpdateApplyStatus status)
    {
        if (!IsValid(status))
        {
            throw new ArgumentException("Update apply status is invalid.", nameof(status));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(status, SerializerOptions);
        if (bytes.Length > MaximumBytes)
        {
            throw new InvalidDataException("Update apply status exceeds its persisted limit.");
        }
        BoundedStateFile.WriteAtomically(path, bytes);
    }

    private static bool IsValid(ContinuityUpdateApplyStatus? status) => status is
    {
        SchemaVersion: ContinuityUpdateApplyStatus.CurrentSchemaVersion,
        PolicyGeneration: >= 0,
    } &&
        status.UpdatedAtUtc != default &&
        ContinuityUpdateApplyStates.IsKnown(status.State) &&
        (status.TargetVersion is null || ContinuitySemanticVersion.IsValid(status.TargetVersion)) &&
        (status.TargetExecutableSha256 is null || IsSha256(status.TargetExecutableSha256)) &&
        (status.HandoffId is null || IsHandoffId(status.HandoffId)) &&
        (status.LastError is null || status.LastError.Length <= MaximumErrorCharacters) &&
        (status.IdleSinceUtc is null || status.IdleSinceUtc <= status.UpdatedAtUtc);

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool IsHandoffId(string value) =>
        Guid.TryParseExact(value, "N", out var id) &&
        id.ToString("N").Equals(value, StringComparison.Ordinal);

    private static ContinuityUpdateApplyStatusLoadResult Invalid() => new(
        ContinuityUpdateApplyLoadKind.Invalid,
        Status: null);
}
