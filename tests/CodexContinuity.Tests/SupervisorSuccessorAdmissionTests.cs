using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorSuccessorAdmissionTests
{
    [Fact]
    public async Task ExactEvidenceGrantsOnlyABoundedWaitForThePreviousSupervisor()
    {
        using var fixture = new AdmissionFixture();

        var result = fixture.TryCreate();

        Assert.Equal(SupervisorSuccessorAdmissionKind.Admitted, result.Kind);
        Assert.NotNull(result.Admission);
        using (result.Admission)
        {
            fixture.ExitPreviousSupervisor();
            Assert.Equal(
                PreviousSupervisorWaitKind.Exited,
                await result.Admission.WaitForPreviousExitAsync(CancellationToken.None));
        }
        Assert.True(fixture.ObservationDisposed);
    }

    [Fact]
    public void MissingExpiredOrWrongHandoffAuthorityFailsClosed()
    {
        using var fixture = new AdmissionFixture();
        File.Delete(ContinuityPaths.SupervisorHandoffFile(fixture.Root));
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.HandoffUnavailable,
            fixture.TryCreate().Kind);

        fixture.WriteHandoff();
        fixture.UtcNow = fixture.Handoff.ExpiresAtUtc;
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.HandoffUnavailable,
            fixture.TryCreate().Kind);

        fixture.UtcNow = fixture.Handoff.CreatedAtUtc;
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.HandoffMismatch,
            fixture.TryCreate(handoffId: new string('0', 32)).Kind);
        Assert.False(fixture.ObservationCreated);
    }

    [Fact]
    public void PortOrCodexHomeMismatchFailsBeforeProcessInspection()
    {
        using var fixture = new AdmissionFixture();

        Assert.Equal(
            SupervisorSuccessorAdmissionKind.EndpointMismatch,
            fixture.TryCreate(publicPort: fixture.Handoff.PublicPort + 1).Kind);
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.EndpointMismatch,
            fixture.TryCreate(codexHome: Path.Combine(fixture.Root, "other-home")).Kind);
        Assert.False(fixture.ObservationCreated);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("sha256")]
    public void EverySelectedSuccessorCoordinateMustMatch(string coordinate)
    {
        using var fixture = new AdmissionFixture();
        var selected = fixture.Handoff.SelectedBuild;
        var mismatched = coordinate switch
        {
            "version" => selected with { Version = "9.0.0" },
            "path" => selected with { Executable = Path.Combine(fixture.Root, "other.exe") },
            "sha256" => selected with { ExecutableSha256 = new string('f', 64) },
            _ => throw new InvalidOperationException($"Unknown successor coordinate {coordinate}."),
        };
        fixture.ResolvedSuccessorBuild = mismatched;

        Assert.Equal(
            SupervisorSuccessorAdmissionKind.SuccessorMismatch,
            fixture.TryCreate(successorExecutable: mismatched.Executable).Kind);
        Assert.False(fixture.ObservationCreated);
    }

    [Fact]
    public void ThePersistedBackendLeaseMustStillMatchExactly()
    {
        using var fixture = new AdmissionFixture();
        var leasePath = ContinuityPaths.BackendLeaseFile(fixture.Root);
        File.Delete(leasePath);
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.BackendLeaseMismatch,
            fixture.TryCreate().Kind);

        new BackendLeaseStore(leasePath).Write(fixture.Handoff.Backend with
        {
            BackendProcessId = fixture.Handoff.Backend.BackendProcessId + 1,
        });
        Assert.Equal(
            SupervisorSuccessorAdmissionKind.BackendLeaseMismatch,
            fixture.TryCreate().Kind);
        Assert.False(fixture.ObservationCreated);
    }

    [Theory]
    [InlineData(nameof(PreviousSupervisorObservationKind.Missing))]
    [InlineData(nameof(PreviousSupervisorObservationKind.Unsafe))]
    public void MissingOrUninspectablePreviousSupervisorFailsClosed(string kindName)
    {
        using var fixture = new AdmissionFixture
        {
            ObservationKind = Enum.Parse<PreviousSupervisorObservationKind>(kindName),
        };

        Assert.Equal(
            fixture.ObservationKind == PreviousSupervisorObservationKind.Missing
                ? SupervisorSuccessorAdmissionKind.PreviousSupervisorMissing
                : SupervisorSuccessorAdmissionKind.PreviousSupervisorUnsafe,
            fixture.TryCreate().Kind);
    }

    [Theory]
    [InlineData("processId")]
    [InlineData("startTime")]
    [InlineData("path")]
    [InlineData("build")]
    public void EveryPreviousSupervisorCoordinateMustMatch(string coordinate)
    {
        using var fixture = new AdmissionFixture();
        switch (coordinate)
        {
            case "processId":
                fixture.ObservedProcessId++;
                break;
            case "startTime":
                fixture.ObservedStartedAtUtc += TimeSpan.FromSeconds(1);
                break;
            case "path":
                fixture.ObservedExecutable = Path.Combine(fixture.Root, "unexpected.exe");
                break;
            case "build":
                fixture.ResolvedRunningBuild = fixture.Handoff.RunningBuild with
                {
                    ExecutableSha256 = new string('f', 64),
                };
                break;
            default:
                throw new InvalidOperationException($"Unknown process coordinate {coordinate}.");
        }

        Assert.Equal(
            SupervisorSuccessorAdmissionKind.PreviousSupervisorMismatch,
            fixture.TryCreate().Kind);
        Assert.True(fixture.ObservationDisposed);
    }

    [Fact]
    public async Task PreviousSupervisorWaitExpiresAtTheManifestBoundary()
    {
        using var fixture = new AdmissionFixture();
        var result = fixture.TryCreate();
        Assert.NotNull(result.Admission);
        using var admission = result.Admission;
        fixture.UtcNow = fixture.Handoff.ExpiresAtUtc;

        Assert.Equal(
            PreviousSupervisorWaitKind.Expired,
            await admission.WaitForPreviousExitAsync(CancellationToken.None));
        Assert.False(fixture.WaitStarted);
    }

    [Fact]
    public async Task PreviousSupervisorWaitIsCanceledAtTheFutureManifestBoundary()
    {
        using var fixture = new AdmissionFixture();
        var result = fixture.TryCreate();
        Assert.NotNull(result.Admission);
        using var admission = result.Admission;
        fixture.UtcNow = fixture.Handoff.ExpiresAtUtc - TimeSpan.FromMilliseconds(20);

        Assert.Equal(
            PreviousSupervisorWaitKind.Expired,
            await admission.WaitForPreviousExitAsync(CancellationToken.None));
        Assert.True(fixture.WaitStarted);
    }

    [Fact]
    public async Task PreviousSupervisorWaitPreservesCallerCancellation()
    {
        using var fixture = new AdmissionFixture();
        var result = fixture.TryCreate();
        Assert.NotNull(result.Admission);
        using var admission = result.Admission;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            admission.WaitForPreviousExitAsync(cancellation.Token));
    }

    [Fact]
    public async Task WaitCompletionWithoutAnObservedExitFailsClosed()
    {
        using var fixture = new AdmissionFixture { CompleteWaitWithoutExit = true };
        var result = fixture.TryCreate();
        Assert.NotNull(result.Admission);
        using var admission = result.Admission;

        Assert.Equal(
            PreviousSupervisorWaitKind.Unsafe,
            await admission.WaitForPreviousExitAsync(CancellationToken.None));
    }

    private sealed class AdmissionFixture : IDisposable
    {
        private readonly TaskCompletionSource previousExit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal AdmissionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"codex-continuity-successor-admission-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            var codexHome = Path.Combine(Root, "codex-home");
            var running = Build("0.3.0", "running.exe", 'a');
            Handoff = new SupervisorSuccessorHandoff(
                SupervisorSuccessorHandoff.CurrentSchemaVersion,
                Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToString("N"),
                PreviousSupervisorProcessId: 42,
                PreviousSupervisorStartedAtUtc: DateTimeOffset.Parse("2026-08-23T11:00:00Z"),
                PublicPort: 45123,
                codexHome,
                running,
                SelectedBuild: Build("0.4.0", "selected.exe", 'b'),
                RollbackBuild: running,
                new BackendLease(
                    BackendLease.CurrentSchemaVersion,
                    OwnerSupervisorProcessId: 42,
                    BackendProcessId: 43,
                    PublicPort: 45123,
                    BackendPort: 45124,
                    BackendExecutable: Path.Combine(Root, "codex.exe"),
                    codexHome,
                    BackendStartedAtUtc: DateTimeOffset.Parse("2026-08-23T11:30:00Z")),
                CreatedAtUtc: DateTimeOffset.Parse("2026-08-23T12:00:00Z"),
                ExpiresAtUtc: DateTimeOffset.Parse("2026-08-23T12:01:00Z"));
            UtcNow = Handoff.CreatedAtUtc;
            ObservedProcessId = Handoff.PreviousSupervisorProcessId;
            ObservedStartedAtUtc = Handoff.PreviousSupervisorStartedAtUtc;
            ObservedExecutable = Handoff.RunningBuild.Executable;
            ResolvedRunningBuild = Handoff.RunningBuild;
            ResolvedSuccessorBuild = Handoff.SelectedBuild;
            WriteHandoff();
            new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(Root)).Write(Handoff.Backend);
        }

        internal string Root { get; }
        internal SupervisorSuccessorHandoff Handoff { get; }
        internal DateTimeOffset UtcNow { get; set; }
        internal PreviousSupervisorObservationKind ObservationKind { get; set; } =
            PreviousSupervisorObservationKind.Observed;
        internal int ObservedProcessId { get; set; }
        internal DateTimeOffset ObservedStartedAtUtc { get; set; }
        internal string ObservedExecutable { get; set; }
        internal SupervisorExecutableIdentity? ResolvedRunningBuild { get; set; }
        internal SupervisorExecutableIdentity? ResolvedSuccessorBuild { get; set; }
        internal bool ObservationCreated { get; private set; }
        internal bool ObservationDisposed { get; private set; }
        internal bool WaitStarted { get; private set; }
        internal bool CompleteWaitWithoutExit { get; init; }
        private bool PreviousExited { get; set; }

        internal SupervisorSuccessorAdmissionResult TryCreate(
            string? handoffId = null,
            int? publicPort = null,
            string? codexHome = null,
            string? successorExecutable = null) =>
            SupervisorSuccessorAdmission.TryCreate(
                Root,
                handoffId ?? Handoff.HandoffId,
                publicPort ?? Handoff.PublicPort,
                codexHome ?? Handoff.CodexHome,
                successorExecutable ?? Handoff.SelectedBuild.Executable,
                new SupervisorSuccessorAdmissionChecks(
                    () => UtcNow,
                    Observe,
                    ResolveExecutable));

        internal void WriteHandoff() => new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(Root)).Write(Handoff);

        internal void ExitPreviousSupervisor()
        {
            PreviousExited = true;
            previousExit.TrySetResult();
        }

        public void Dispose()
        {
            previousExit.TrySetCanceled();
            Directory.Delete(Root, recursive: true);
        }

        private PreviousSupervisorObservationResult Observe(int processId)
        {
            if (ObservationKind != PreviousSupervisorObservationKind.Observed)
            {
                return new(ObservationKind, Observation: null);
            }
            ObservationCreated = true;
            return new(
                PreviousSupervisorObservationKind.Observed,
                new PreviousSupervisorObservation(
                    ObservedProcessId,
                    ObservedStartedAtUtc,
                    ObservedExecutable,
                    () => PreviousExited,
                    async cancellationToken =>
                    {
                        WaitStarted = true;
                        if (!CompleteWaitWithoutExit)
                        {
                            await previousExit.Task.WaitAsync(cancellationToken);
                        }
                    },
                    () => ObservationDisposed = true));
        }

        private SupervisorExecutableIdentity? ResolveExecutable(string executable)
        {
            if (ResolvedRunningBuild is { } running &&
                PathsEqual(executable, running.Executable))
            {
                return running;
            }
            return ResolvedSuccessorBuild is { } successor &&
                PathsEqual(executable, successor.Executable)
                    ? successor
                    : null;
        }

        private SupervisorExecutableIdentity Build(
            string version,
            string fileName,
            char sha256Character) => new(
                version,
                Path.Combine(Root, fileName),
                new string(sha256Character, 64));

        private static bool PathsEqual(string left, string right) =>
            Path.GetFullPath(left).Equals(
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
    }
}
