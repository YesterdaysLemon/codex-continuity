using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorSuccessorAdmissionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-successor-admission-tests-{Guid.NewGuid():N}");

    public SupervisorSuccessorAdmissionTests() => Directory.CreateDirectory(root);

    [Fact]
    public void ParsesBoundedSuccessorArguments()
    {
        var id = Guid.NewGuid().ToString("N");

        Assert.Null(SupervisorSuccessorAdmission.ParseRequest(["serve"]));
        Assert.Equal(
            new SupervisorSuccessorRequest(id, SupervisorSuccessorRole.Selected),
            SupervisorSuccessorAdmission.ParseRequest([
                "serve", "--successor-handoff", id, "--successor-role", "selected",
            ]));
        Assert.Equal(
            SupervisorSuccessorRole.Rollback,
            SupervisorSuccessorAdmission.ParseRequest([
                "serve", "--successor-handoff", id, "--successor-role", "ROLLBACK",
            ])!.Role);
    }

    [Theory]
    [InlineData("--successor-handoff")]
    [InlineData("--successor-role")]
    public void RequiresTheCompleteSuccessorArgumentPair(string loneArgument)
    {
        Assert.Throws<ArgumentException>(() =>
            SupervisorSuccessorAdmission.ParseRequest(["serve", loneArgument, "selected"]));
    }

    [Fact]
    public void RejectsNumericSuccessorRole()
    {
        Assert.Throws<ArgumentException>(() => SupervisorSuccessorAdmission.ParseRequest([
            "serve",
            "--successor-handoff",
            Guid.NewGuid().ToString("N"),
            "--successor-role",
            "0",
        ]));
    }

    [Fact]
    public async Task ExactSelectedSuccessorWaitsForPredecessorThenRevalidates()
    {
        var fixture = WriteFixture();
        var observations = new Queue<SupervisorPredecessorState>([
            SupervisorPredecessorState.Running,
            SupervisorPredecessorState.Exited,
        ]);
        var delays = 0;

        var admitted = await SupervisorSuccessorAdmission.PrepareAsync(
            root,
            new(fixture.Handoff.HandoffId, SupervisorSuccessorRole.Selected),
            fixture.Handoff.PublicPort,
            fixture.Handoff.CodexHome,
            fixture.Handoff.SelectedBuild.Executable,
            () => Now,
            (_, _) => observations.Dequeue(),
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(fixture.Handoff, admitted);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task ExactRollbackSuccessorCanBeAdmittedAfterPredecessorExit()
    {
        var fixture = WriteFixture();

        var admitted = await PrepareAsync(
            fixture,
            SupervisorSuccessorRole.Rollback,
            fixture.Handoff.RollbackBuild.Executable);

        Assert.Equal(fixture.Handoff.RollbackBuild.Executable, admitted.RollbackBuild.Executable);
    }

    [Fact]
    public async Task HandoffIdMustMatchExactly()
    {
        var fixture = WriteFixture();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrepareAsync(
                fixture,
                SupervisorSuccessorRole.Selected,
                fixture.Handoff.SelectedBuild.Executable,
                Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task ExecutableIdentityMustMatchTheRequestedRole()
    {
        var fixture = WriteFixture();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrepareAsync(
                fixture,
                SupervisorSuccessorRole.Selected,
                fixture.Handoff.RollbackBuild.Executable));
    }

    [Theory]
    [InlineData("port")]
    [InlineData("codexHome")]
    public async Task InstallationIdentityMustMatch(string mismatch)
    {
        var fixture = WriteFixture();
        var port = mismatch == "port" ? fixture.Handoff.PublicPort + 1 : fixture.Handoff.PublicPort;
        var codexHome = mismatch == "codexHome"
            ? Path.Combine(root, "other-home")
            : fixture.Handoff.CodexHome;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrepareAsync(
                fixture,
                SupervisorSuccessorRole.Selected,
                fixture.Handoff.SelectedBuild.Executable,
                publicPort: port,
                codexHome: codexHome));
    }

    [Fact]
    public async Task ChangedBackendLeaseFailsClosed()
    {
        var fixture = WriteFixture();
        new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Write(
            fixture.Handoff.Backend with { BackendProcessId = 999 });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PrepareAsync(
                fixture,
                SupervisorSuccessorRole.Selected,
                fixture.Handoff.SelectedBuild.Executable));
    }

    [Fact]
    public async Task UnknownPredecessorIdentityFailsClosed()
    {
        var fixture = WriteFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PrepareAsync(
                fixture,
                SupervisorSuccessorRole.Selected,
                fixture.Handoff.SelectedBuild.Executable,
                predecessorState: SupervisorPredecessorState.Unknown));
    }

    [Fact]
    public async Task ChangedHandoffDuringWaitFailsClosedOnRevalidation()
    {
        var fixture = WriteFixture();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SupervisorSuccessorAdmission.PrepareAsync(
                root,
                new(fixture.Handoff.HandoffId, SupervisorSuccessorRole.Selected),
                fixture.Handoff.PublicPort,
                fixture.Handoff.CodexHome,
                fixture.Handoff.SelectedBuild.Executable,
                () => Now,
                (_, _) => ++calls == 1
                    ? SupervisorPredecessorState.Running
                    : SupervisorPredecessorState.Exited,
                (_, _) =>
                {
                    new SupervisorSuccessorHandoffStore(
                        ContinuityPaths.SupervisorHandoffFile(root)).Write(
                        fixture.Handoff with { HandoffId = Guid.NewGuid().ToString("N") });
                    return Task.CompletedTask;
                },
                CancellationToken.None));
    }

    private Task<SupervisorSuccessorHandoff> PrepareAsync(
        Fixture fixture,
        SupervisorSuccessorRole role,
        string executable,
        string? handoffId = null,
        int? publicPort = null,
        string? codexHome = null,
        SupervisorPredecessorState predecessorState = SupervisorPredecessorState.Exited) =>
        SupervisorSuccessorAdmission.PrepareAsync(
            root,
            new(handoffId ?? fixture.Handoff.HandoffId, role),
            publicPort ?? fixture.Handoff.PublicPort,
            codexHome ?? fixture.Handoff.CodexHome,
            executable,
            () => Now,
            (_, _) => predecessorState,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

    private Fixture WriteFixture()
    {
        var selectedExecutable = typeof(Program).Assembly.Location;
        var selectedBuild = AutomaticUpdateRunner.ResolveBuildIdentity(selectedExecutable)!;
        var rollbackExecutable = typeof(SupervisorSuccessorAdmissionTests).Assembly.Location;
        var rollbackBuild = AutomaticUpdateRunner.ResolveBuildIdentity(rollbackExecutable)!;
        var codexHome = Path.Combine(root, "codex-home");
        var backend = new BackendLease(
            BackendLease.CurrentSchemaVersion,
            OwnerSupervisorProcessId: 41,
            BackendProcessId: 42,
            PublicPort: 45123,
            BackendPort: 45124,
            BackendExecutable: selectedExecutable,
            CodexHome: codexHome,
            BackendStartedAtUtc: Now - TimeSpan.FromMinutes(10));
        var handoff = new SupervisorSuccessorHandoff(
            SupervisorSuccessorHandoff.CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            PreviousSupervisorProcessId: 41,
            PreviousSupervisorStartedAtUtc: Now - TimeSpan.FromMinutes(5),
            PublicPort: 45123,
            CodexHome: codexHome,
            RunningBuild: Identity(selectedExecutable, selectedBuild),
            SelectedBuild: Identity(selectedExecutable, selectedBuild),
            RollbackBuild: Identity(rollbackExecutable, rollbackBuild),
            Backend: backend,
            CreatedAtUtc: Now - TimeSpan.FromSeconds(1),
            ExpiresAtUtc: Now + TimeSpan.FromMinutes(1));
        new SupervisorSuccessorHandoffStore(
            ContinuityPaths.SupervisorHandoffFile(root)).Write(handoff);
        new BackendLeaseStore(ContinuityPaths.BackendLeaseFile(root)).Write(backend);
        return new(handoff);
    }

    private static SupervisorExecutableIdentity Identity(
        string executable,
        ContinuityBuildIdentity build) => new(
            build.Version,
            executable,
            build.ExecutableSha256);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Fixture(SupervisorSuccessorHandoff Handoff);
}
