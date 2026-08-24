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
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SnoozedUntilUtc = null,
    ContinuityActivationWindow? ActivationWindow = null)
{
    internal const int CurrentSchemaVersion = 2;
    internal const int LegacySchemaVersion = 1;
    internal static readonly TimeSpan MaximumSnooze = TimeSpan.FromDays(7);

    internal static ContinuityUpdateApplyPolicy Default(DateTimeOffset nowUtc) => new(
        CurrentSchemaVersion,
        AutomaticApplyWhenIdle: false,
        Generation: 0,
        UpdatedAtUtc: nowUtc,
        SnoozedUntilUtc: null,
        ActivationWindow: null);

    internal ContinuityUpdateApplyPolicy WithAutomaticApply(
        bool enabled,
        DateTimeOffset nowUtc) => this with
        {
            SchemaVersion = CurrentSchemaVersion,
            AutomaticApplyWhenIdle = enabled,
            Generation = checked(Generation + 1),
            UpdatedAtUtc = nowUtc,
        };

    internal ContinuityUpdateApplyPolicy WithSnooze(
        DateTimeOffset? snoozedUntilUtc,
        DateTimeOffset nowUtc)
    {
        if (snoozedUntilUtc is { } until &&
            (until <= nowUtc || until - nowUtc > MaximumSnooze))
        {
            throw new ArgumentOutOfRangeException(
                nameof(snoozedUntilUtc),
                "An activation snooze must end within the next seven days.");
        }
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            SnoozedUntilUtc = snoozedUntilUtc,
            Generation = checked(Generation + 1),
            UpdatedAtUtc = nowUtc,
        };
    }

    internal ContinuityUpdateApplyPolicy WithActivationWindow(
        ContinuityActivationWindow? activationWindow,
        DateTimeOffset nowUtc)
    {
        activationWindow?.Validate();
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            ActivationWindow = activationWindow,
            Generation = checked(Generation + 1),
            UpdatedAtUtc = nowUtc,
        };
    }
}

internal sealed record ContinuityActivationWindow(
    int StartMinuteLocal,
    int EndMinuteLocal,
    string TimeZoneId)
{
    internal void Validate()
    {
        if (StartMinuteLocal is < 0 or >= 24 * 60 ||
            EndMinuteLocal is < 0 or >= 24 * 60 ||
            StartMinuteLocal == EndMinuteLocal)
        {
            throw new ArgumentOutOfRangeException(
                nameof(StartMinuteLocal),
                "An activation window needs two distinct local times within one day.");
        }
        if (string.IsNullOrWhiteSpace(TimeZoneId) || TimeZoneId.Length > 128)
        {
            throw new ArgumentException("The activation-window time zone is invalid.", nameof(TimeZoneId));
        }
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException(
                "The activation-window time zone is unavailable.",
                nameof(TimeZoneId),
                exception);
        }
    }
}

internal sealed record ContinuityUpdateApplyEligibility(
    bool Eligible,
    string Reason);

internal static class ContinuityUpdateApplySchedule
{
    internal static ContinuityUpdateApplyEligibility Evaluate(
        ContinuityUpdateApplyPolicy policy,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SnoozedUntilUtc is { } snoozedUntilUtc && nowUtc < snoozedUntilUtc)
        {
            return new(false, "snoozed");
        }
        if (policy.ActivationWindow is not { } window)
        {
            return new(true, "anyTime");
        }

        window.Validate();
        var local = TimeZoneInfo.ConvertTime(
            nowUtc,
            TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId));
        var minute = (local.Hour * 60) + local.Minute;
        var inside = window.StartMinuteLocal < window.EndMinuteLocal
            ? minute >= window.StartMinuteLocal && minute < window.EndMinuteLocal
            : minute >= window.StartMinuteLocal || minute < window.EndMinuteLocal;
        return new(inside, inside ? "insideWindow" : "outsideWindow");
    }
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
            if (schemaVersion is not (
                    ContinuityUpdateApplyPolicy.LegacySchemaVersion or
                    ContinuityUpdateApplyPolicy.CurrentSchemaVersion))
            {
                return new(ContinuityUpdateApplyLoadKind.UnsupportedSchema, Policy: null);
            }
            var policy = JsonSerializer.Deserialize<ContinuityUpdateApplyPolicy>(
                bytes.Span,
                SerializerOptions);
            if (policy?.SchemaVersion == ContinuityUpdateApplyPolicy.LegacySchemaVersion)
            {
                policy = policy with
                {
                    SchemaVersion = ContinuityUpdateApplyPolicy.CurrentSchemaVersion,
                    SnoozedUntilUtc = null,
                    ActivationWindow = null,
                };
            }
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
    } &&
        policy.UpdatedAtUtc != default &&
        (policy.SnoozedUntilUtc is null ||
            policy.SnoozedUntilUtc > policy.UpdatedAtUtc &&
            policy.SnoozedUntilUtc - policy.UpdatedAtUtc <=
                ContinuityUpdateApplyPolicy.MaximumSnooze) &&
        IsValid(policy.ActivationWindow);

    private static bool IsValid(ContinuityActivationWindow? window)
    {
        if (window is null)
        {
            return true;
        }
        try
        {
            window.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

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
