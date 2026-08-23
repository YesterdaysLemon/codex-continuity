using CodexContinuity;
using System.Text.Json;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorStatusStoreTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-supervisor-status-{Guid.NewGuid():N}");

    [Fact]
    public void DistinguishesMissingMalformedAndLoadedStatus()
    {
        var path = ContinuityPaths.SupervisorStatusFile(root);
        var store = new SupervisorStatusStore(path);
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Missing, Status: null),
            store.Load());

        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{not-json");
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Unsafe, Status: null),
            store.Load());
        Assert.Null(store.Read());

        File.WriteAllText(path, "{}");
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Unsafe, Status: null),
            store.Load());

        var status = Status();
        store.Write(status);
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Loaded, status),
            store.Load());
        Assert.Equal(status, store.Read());
    }

    [Fact]
    public void ObstructedOversizedAndIncompleteStatusIsUnsafe()
    {
        var path = ContinuityPaths.SupervisorStatusFile(root);
        Directory.CreateDirectory(path);
        var store = new SupervisorStatusStore(path);
        Assert.Equal(SupervisorStatusLoadKind.Unsafe, store.Load().Kind);

        Directory.Delete(path);
        File.WriteAllText(path, new string('x', SupervisorStatusStore.MaximumStatusBytes + 1));
        Assert.Equal(SupervisorStatusLoadKind.Unsafe, store.Load().Kind);

        var incompleteIdentity = Status() with { SupervisorExecutable = null };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(incompleteIdentity, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        Assert.Equal(SupervisorStatusLoadKind.Unsafe, store.Load().Kind);
        Assert.Throws<InvalidDataException>(() => store.Write(incompleteIdentity));

        Assert.Throws<InvalidDataException>(() =>
            store.Write(Status() with { SupervisorStartedAtUtc = default(DateTimeOffset) }));
        Assert.Throws<InvalidDataException>(() =>
            store.Write(Status() with { SupervisorExecutable = "relative.exe" }));
    }

    [Fact]
    public void MaximumSupportedFieldsRoundTripWithinEncodedByteLimit()
    {
        var path = ContinuityPaths.SupervisorStatusFile(root);
        var store = new SupervisorStatusStore(path);
        var status = Status() with
        {
            State = new string('s', 64),
            CodexHome = new string('界', 32767),
            Detail = new string('界', 4096),
            SupervisorExecutable = $"C:\\{new string('界', 32764)}",
        };

        store.Write(status);

        Assert.InRange(new FileInfo(path).Length, 1, SupervisorStatusStore.MaximumStatusBytes);
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Loaded, status),
            store.Load());
    }

    [Fact]
    public void AtomicWriteSucceedsWhileStatusReadHandleIsOpen()
    {
        var path = ContinuityPaths.SupervisorStatusFile(root);
        var store = new SupervisorStatusStore(path);
        var first = Status() with { Detail = "First" };
        var replacement = Status() with { Detail = "Replacement" };
        store.Write(first);
        using var openRead = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        store.Write(replacement);

        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Loaded, replacement),
            store.Load());
        using var reader = new StreamReader(openRead);
        Assert.Contains("First", reader.ReadToEnd());
    }

    [Fact]
    public void DiagnosticProjectionBoundsAndReducesPersistedPaths()
    {
        var status = Status() with
        {
            CodexHome = new string('h', 600),
            Detail = new string('d', 1200),
            SupervisorExecutable = $"C:\\installed\\{new string('e', 300)}.exe",
        };

        var projected = Program.SupervisorStatusForDiagnostics(status);

        Assert.NotNull(projected);
        Assert.Equal(512, projected.CodexHome?.Length);
        Assert.Equal(1024, projected.Detail?.Length);
        Assert.Equal(260, projected.SupervisorExecutable?.Length);
        Assert.DoesNotContain("installed", projected.SupervisorExecutable);
        Assert.EndsWith("…", projected.CodexHome);
        Assert.EndsWith("…", projected.Detail);
        Assert.EndsWith("…", projected.SupervisorExecutable);
    }

    [Fact]
    public void LockedStatusIsUnsafeRatherThanMissing()
    {
        Directory.CreateDirectory(root);
        var path = ContinuityPaths.SupervisorStatusFile(root);
        File.WriteAllText(path, "{}");
        using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Unsafe, Status: null),
            new SupervisorStatusStore(path).Load());
    }

    [Fact]
    public void LoadsStatusWrittenBeforeDurableIdentityFields()
    {
        Directory.CreateDirectory(root);
        var path = ContinuityPaths.SupervisorStatusFile(root);
        File.WriteAllText(
            path,
            """
            {
              "state": "running",
              "supervisorProcessId": 42,
              "backendProcessId": null,
              "port": 45123,
              "codexHome": null,
              "consecutiveFailures": 0,
              "lastExitCode": null,
              "updatedAtUtc": "2026-08-23T00:00:00+00:00",
              "nextRetryAtUtc": null,
              "detail": "Legacy status"
            }
            """);

        var result = new SupervisorStatusStore(path).Load();

        Assert.Equal(SupervisorStatusLoadKind.Loaded, result.Kind);
        Assert.Equal(
            new SupervisorStatus(
                "running",
                42,
                BackendProcessId: null,
                45123,
                CodexHome: null,
                ConsecutiveFailures: 0,
                LastExitCode: null,
                DateTimeOffset.Parse("2026-08-23T00:00:00+00:00"),
                NextRetryAtUtc: null,
                "Legacy status"),
            result.Status);
    }

    [Fact]
    public void LoadsLegacyStatusWithFullWindowsPathDomain()
    {
        Directory.CreateDirectory(root);
        var path = ContinuityPaths.SupervisorStatusFile(root);
        var codexHome = $"C:\\{new string('界', 19997)}";
        var status = Status() with
        {
            CodexHome = codexHome,
            SupervisorStartedAtUtc = null,
            SupervisorExecutable = null,
        };
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(status, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));

        Assert.True(new FileInfo(path).Length > 96 * 1024);
        Assert.Equal(
            new SupervisorStatusLoadResult(SupervisorStatusLoadKind.Loaded, status),
            new SupervisorStatusStore(path).Load());
    }

    private static SupervisorStatus Status() => new(
        "running",
        Environment.ProcessId,
        BackendProcessId: 123,
        45123,
        CodexHome: "C:\\codex-home",
        ConsecutiveFailures: 0,
        LastExitCode: null,
        DateTimeOffset.UtcNow,
        NextRetryAtUtc: null,
        "Ready",
        DateTimeOffset.UtcNow.AddSeconds(-1),
        "C:\\CodexContinuity.exe");

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
