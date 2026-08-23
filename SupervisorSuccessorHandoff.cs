using System.Text.Json;

namespace CodexContinuity;

internal sealed record SupervisorExecutableIdentity(
    string Version,
    string Executable,
    string ExecutableSha256)
{
    internal void Validate()
    {
        if (!ContinuitySemanticVersion.IsValid(Version))
        {
            throw new InvalidDataException("Supervisor build version is invalid.");
        }
        if (!Path.IsPathFullyQualified(Executable))
        {
            throw new InvalidDataException("Supervisor executable must be fully qualified.");
        }
        if (!SupervisorSuccessorHandoff.IsSha256(ExecutableSha256))
        {
            throw new InvalidDataException("Supervisor executable digest is invalid.");
        }
    }
}

internal sealed record SupervisorSuccessorHandoff(
    int SchemaVersion,
    string HandoffId,
    int PreviousSupervisorProcessId,
    DateTimeOffset PreviousSupervisorStartedAtUtc,
    int PublicPort,
    string? CodexHome,
    SupervisorExecutableIdentity RunningBuild,
    SupervisorExecutableIdentity SelectedBuild,
    SupervisorExecutableIdentity RollbackBuild,
    BackendLease Backend,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    internal const int CurrentSchemaVersion = 1;
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(5);

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported supervisor handoff schema {SchemaVersion}.");
        }
        if (!Guid.TryParseExact(HandoffId, "N", out var parsedHandoffId) ||
            !parsedHandoffId.ToString("N").Equals(HandoffId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Supervisor handoff ID is invalid.");
        }
        if (PreviousSupervisorProcessId <= 0 || PreviousSupervisorStartedAtUtc == default)
        {
            throw new InvalidDataException("Previous supervisor identity is invalid.");
        }
        LoopbackEndpoint.ValidatePort(PublicPort);
        if (CodexHome is not null && !Path.IsPathFullyQualified(CodexHome))
        {
            throw new InvalidDataException("Supervisor handoff CODEX_HOME must be fully qualified.");
        }

        if (RunningBuild is null ||
            SelectedBuild is null ||
            RollbackBuild is null ||
            Backend is null)
        {
            throw new InvalidDataException("Supervisor handoff identities are required.");
        }
        RunningBuild.Validate();
        SelectedBuild.Validate();
        RollbackBuild.Validate();
        Backend.Validate();
        if (Backend.OwnerSupervisorProcessId != PreviousSupervisorProcessId ||
            Backend.PublicPort != PublicPort ||
            !SameOptionalPath(Backend.CodexHome, CodexHome))
        {
            throw new InvalidDataException(
                "The leased backend does not belong to the previous supervisor handoff.");
        }
        if (CreatedAtUtc == default ||
            ExpiresAtUtc <= CreatedAtUtc ||
            ExpiresAtUtc - CreatedAtUtc > MaximumLifetime ||
            PreviousSupervisorStartedAtUtc > CreatedAtUtc ||
            Backend.BackendStartedAtUtc > CreatedAtUtc)
        {
            throw new InvalidDataException("Supervisor handoff lifetime is invalid.");
        }
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}

internal enum SupervisorSuccessorHandoffLoadKind
{
    Missing,
    Loaded,
    Invalid,
    UnsupportedSchema,
    Expired,
    Unreadable,
}

internal sealed record SupervisorSuccessorHandoffLoadResult(
    SupervisorSuccessorHandoffLoadKind Kind,
    SupervisorSuccessorHandoff? Handoff);

internal sealed class SupervisorSuccessorHandoffStore(string path)
{
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal SupervisorSuccessorHandoffLoadResult Load(DateTimeOffset nowUtc)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            var bytes = new byte[MaximumBytes + 1];
            var bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                var read = stream.Read(bytes, bytesRead, bytes.Length - bytesRead);
                if (read == 0)
                {
                    break;
                }
                bytesRead += read;
            }
            if (bytesRead > MaximumBytes)
            {
                return Invalid();
            }

            using var document = JsonDocument.Parse(bytes.AsMemory(0, bytesRead));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return Invalid();
            }
            if (schemaVersion != SupervisorSuccessorHandoff.CurrentSchemaVersion)
            {
                return new(
                    SupervisorSuccessorHandoffLoadKind.UnsupportedSchema,
                    Handoff: null);
            }

            var handoff = document.RootElement.Deserialize<SupervisorSuccessorHandoff>(
                SerializerOptions);
            handoff?.Validate();
            if (handoff is null)
            {
                return Invalid();
            }
            if (handoff.CreatedAtUtc > nowUtc &&
                handoff.CreatedAtUtc - nowUtc > SupervisorSuccessorHandoff.MaximumClockSkew)
            {
                return Invalid();
            }
            return handoff.ExpiresAtUtc <= nowUtc
                ? new(SupervisorSuccessorHandoffLoadKind.Expired, handoff)
                : new(SupervisorSuccessorHandoffLoadKind.Loaded, handoff);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(SupervisorSuccessorHandoffLoadKind.Missing, Handoff: null);
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
            return new(SupervisorSuccessorHandoffLoadKind.Unreadable, Handoff: null);
        }
    }

    internal void Write(SupervisorSuccessorHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        handoff.Validate();
        var serialized = JsonSerializer.SerializeToUtf8Bytes(handoff, SerializerOptions);
        if (serialized.Length > MaximumBytes)
        {
            throw new InvalidDataException(
                $"Supervisor handoff exceeds the {MaximumBytes}-byte limit.");
        }

        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Supervisor handoff path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(temporaryPath, serialized);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SupervisorSuccessorHandoffLoadResult Invalid() =>
        new(SupervisorSuccessorHandoffLoadKind.Invalid, Handoff: null);
}
