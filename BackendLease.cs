using System.Text.Json;

namespace CodexContinuity;

internal sealed record BackendLease(
    int SchemaVersion,
    int OwnerSupervisorProcessId,
    int BackendProcessId,
    int PublicPort,
    int BackendPort,
    string BackendExecutable,
    string? CodexHome,
    DateTimeOffset BackendStartedAtUtc)
{
    internal const int CurrentSchemaVersion = 1;

    internal void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported backend lease schema {SchemaVersion}.");
        }
        if (OwnerSupervisorProcessId <= 0 || BackendProcessId <= 0)
        {
            throw new InvalidDataException("Backend lease process IDs must be positive.");
        }
        LoopbackEndpoint.ValidatePort(PublicPort);
        LoopbackEndpoint.ValidatePort(BackendPort);
        if (PublicPort == BackendPort)
        {
            throw new InvalidDataException("Backend lease ports must be distinct.");
        }
        if (!Path.IsPathFullyQualified(BackendExecutable))
        {
            throw new InvalidDataException("Backend lease executable must be fully qualified.");
        }
        if (CodexHome is not null && !Path.IsPathFullyQualified(CodexHome))
        {
            throw new InvalidDataException("Backend lease CODEX_HOME must be fully qualified.");
        }
        if (BackendStartedAtUtc == default)
        {
            throw new InvalidDataException("Backend lease start time is required.");
        }
    }
}

internal enum BackendLeaseLoadKind
{
    Missing,
    Loaded,
    Invalid,
}

internal sealed record BackendLeaseLoadResult(
    BackendLeaseLoadKind Kind,
    BackendLease? Lease);

internal sealed class BackendLeaseStore(string path)
{
    private const long MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal BackendLeaseLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new(BackendLeaseLoadKind.Missing, Lease: null);
        }

        try
        {
            if (new FileInfo(path).Length > MaximumBytes)
            {
                return new(BackendLeaseLoadKind.Invalid, Lease: null);
            }
            var lease = JsonSerializer.Deserialize<BackendLease>(
                File.ReadAllText(path),
                SerializerOptions);
            lease?.Validate();
            return lease is null
                ? new(BackendLeaseLoadKind.Invalid, Lease: null)
                : new(BackendLeaseLoadKind.Loaded, lease);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(BackendLeaseLoadKind.Invalid, Lease: null);
        }
    }

    internal void Write(BackendLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.Validate();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Backend lease path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(lease, SerializerOptions));
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

    internal void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

internal enum BackendRecoveryKind
{
    None,
    Recovered,
    Stale,
    Unsafe,
}

internal sealed record BackendRecoveryResult(
    BackendRecoveryKind Kind,
    WindowsProcessGroup? Backend,
    BackendLease? Lease,
    string? Detail);

internal static class BackendLeaseRecovery
{
    internal static BackendRecoveryResult TryRecover(
        BackendLeaseStore store,
        int publicPort,
        string expectedExecutable,
        string? expectedCodexHome)
    {
        var loadResult = store.Load();
        if (loadResult.Kind == BackendLeaseLoadKind.Missing)
        {
            return new(BackendRecoveryKind.None, Backend: null, Lease: null, Detail: null);
        }
        if (loadResult.Kind == BackendLeaseLoadKind.Invalid || loadResult.Lease is null)
        {
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                Lease: null,
                "The persisted backend lease is invalid.");
        }

        var lease = loadResult.Lease;
        bool installationMatches;
        try
        {
            installationMatches =
                lease.PublicPort == publicPort &&
                SamePath(lease.BackendExecutable, expectedExecutable) &&
                SameOptionalPath(lease.CodexHome, expectedCodexHome);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            installationMatches = false;
        }
        if (!installationMatches)
        {
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The persisted backend lease does not match this installation.");
        }

        WindowsProcessGroup backend;
        try
        {
            backend = WindowsProcessGroup.Attach(lease.BackendProcessId);
        }
        catch (ArgumentException)
        {
            return new(
                BackendRecoveryKind.Stale,
                Backend: null,
                lease,
                "The leased backend process no longer exists.");
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The leased backend process could not be inspected.");
        }

        bool identityMatches;
        try
        {
            identityMatches =
                !backend.HasExited &&
                backend.StartedAtUtc == lease.BackendStartedAtUtc &&
                SamePath(backend.ExecutablePath, lease.BackendExecutable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            backend.Dispose();
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The leased process identity could not be verified.");
        }
        if (!identityMatches)
        {
            backend.Dispose();
            return new(
                BackendRecoveryKind.Stale,
                Backend: null,
                lease,
                "The leased process identity is stale.");
        }
        bool ownsPort;
        try
        {
            ownsPort = WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy(
                lease.BackendPort,
                lease.BackendProcessId);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
        if (!ownsPort)
        {
            backend.Dispose();
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "The leased backend does not own its private loopback port.");
        }

        return new(
            BackendRecoveryKind.Recovered,
            backend,
            lease,
            "Recovered the verified backend left by a previous supervisor.");
    }

    private static bool SameOptionalPath(string? left, string? right) =>
        left is null && right is null ||
        left is not null && right is not null && SamePath(left, right);

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
