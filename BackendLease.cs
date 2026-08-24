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
    private const int MaximumBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        ContinuityJsonSerializerPresets.CamelCaseIndented();

    internal BackendLeaseLoadResult Load()
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
                return new(BackendLeaseLoadKind.Invalid, Lease: null);
            }

            var lease = JsonSerializer.Deserialize<BackendLease>(
                bytes.AsSpan(0, bytesRead),
                SerializerOptions);
            lease?.Validate();
            return lease is null
                ? new(BackendLeaseLoadKind.Invalid, Lease: null)
                : new(BackendLeaseLoadKind.Loaded, lease);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(BackendLeaseLoadKind.Missing, Lease: null);
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
        var serialized = JsonSerializer.SerializeToUtf8Bytes(lease, SerializerOptions);
        if (serialized.Length > MaximumBytes)
        {
            throw new InvalidDataException(
                $"Backend lease exceeds the {MaximumBytes}-byte limit.");
        }
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Backend lease path has no directory: {path}");
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
        string? expectedCodexHome) => TryRecover(
            store,
            publicPort,
            expectedCodexHome,
            WindowsTcpPortOwnership.IsLoopbackListenerOwnedBy);

    internal static BackendRecoveryResult TryRecover(
        BackendLeaseStore store,
        int publicPort,
        string? expectedCodexHome,
        Func<int, int, bool> ownsLoopbackListener)
    {
        ArgumentNullException.ThrowIfNull(ownsLoopbackListener);
        // The caller owns the public-port mutex; a persisted supervisor PID is not durable identity.
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
                SupervisorActivationSupport.SameOptionalPath(
                    lease.CodexHome,
                    expectedCodexHome);
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
            ownsPort = ownsLoopbackListener(
                lease.BackendPort,
                lease.BackendProcessId);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            backend.Dispose();
            return new(
                BackendRecoveryKind.Unsafe,
                Backend: null,
                lease,
                "Private loopback port ownership could not be inspected.");
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

    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
}
