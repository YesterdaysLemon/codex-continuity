using System.Text.Json;

namespace CodexContinuity;

internal sealed record StagedSupervisorTransitionBuilds(
    SupervisorExecutableIdentity RunningBuild,
    SupervisorExecutableIdentity SelectedBuild,
    SupervisorExecutableIdentity RollbackBuild);

internal enum StagedSupervisorTransitionLoadKind
{
    Loaded,
    InstallStateUnavailable,
    UpdateStateUnavailable,
    SelectedBuildUnavailable,
    NoPendingStagedUpdate,
    RollbackBuildUnavailable,
    PublisherVerificationUnavailable,
}

internal sealed record StagedSupervisorTransitionLoadResult(
    StagedSupervisorTransitionLoadKind Kind,
    StagedSupervisorTransitionBuilds? Builds);

internal sealed record StagedSupervisorTransitionChecks(
    Func<string, SupervisorExecutableIdentity?> ResolveExecutable,
    Func<string, IReadOnlyList<string>, CancellationToken, Task> VerifyMatchingPublisher)
{
    internal static StagedSupervisorTransitionChecks Native { get; } = new(
        ResolveNativeExecutable,
        AuthenticodeReleaseVerifier.VerifyMatchingPublisherAsync);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ResolveExecutable);
        ArgumentNullException.ThrowIfNull(VerifyMatchingPublisher);
    }

    private static SupervisorExecutableIdentity? ResolveNativeExecutable(string executable)
    {
        var build = AutomaticUpdateRunner.ResolveBuildIdentity(executable);
        return build is null
            ? null
            : new(build.Version, Path.GetFullPath(executable), build.ExecutableSha256);
    }
}

internal static class StagedSupervisorTransitionReader
{
    internal static async Task<StagedSupervisorTransitionLoadResult> LoadAsync(
        string stateDirectory,
        StagedSupervisorTransitionChecks? checks = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        checks ??= StagedSupervisorTransitionChecks.Native;
        checks.Validate();

        InstallState? installState;
        try
        {
            installState = new InstallStateStore(
                ContinuityPaths.InstallStateFile(stateDirectory)).Load();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or NotSupportedException)
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.InstallStateUnavailable);
        }
        if (installState is not
            {
                SchemaVersion: InstallStateStore.CurrentSchemaVersion,
                Lifecycle: InstallLifecycle.Installed,
                PreviousInstalledExecutable: { } rollbackExecutable,
            } ||
            !Path.IsPathFullyQualified(rollbackExecutable))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.InstallStateUnavailable);
        }

        var selectedIdentity = checks.ResolveExecutable(installState.InstalledExecutable);
        if (selectedIdentity is null ||
            !PathsEqual(selectedIdentity.Executable, installState.InstalledExecutable) ||
            !IsVersionedSupervisorPath(stateDirectory, selectedIdentity) ||
            !selectedIdentity.ExecutableSha256.Equals(
                installState.BinarySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable);
        }

        var updateLoad = new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load();
        if (updateLoad.Kind != ContinuityUpdateStateLoadKind.Loaded ||
            updateLoad.State is not { } updateState)
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.UpdateStateUnavailable);
        }
        if (!updateState.RunningProcessObserved ||
            updateState.RunningExecutableSha256 is not { } runningSha256 ||
            ContinuitySemanticVersion.Compare(
                updateState.SelectedVersion,
                updateState.RunningVersion) <= 0)
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.NoPendingStagedUpdate);
        }

        var selectedReleases = updateState.Releases.Where(release =>
                release.Version.Equals(
                    updateState.SelectedVersion,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (selectedReleases is not [{ } selectedRelease] ||
            selectedRelease is not
            {
                StagedAtUtc: not null,
                AppliedAtUtc: null,
                StagedExecutableSha256: { } stagedSha256,
                RollbackExecutableSha256: { } rollbackSha256,
            } ||
            !selectedIdentity.Version.Equals(
                updateState.SelectedVersion,
                StringComparison.OrdinalIgnoreCase) ||
            !selectedIdentity.ExecutableSha256.Equals(
                stagedSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !installState.BinarySha256.Equals(
                stagedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.NoPendingStagedUpdate);
        }

        var rollback = checks.ResolveExecutable(rollbackExecutable);
        if (rollback is null ||
            !PathsEqual(rollback.Executable, rollbackExecutable) ||
            !IsVersionedSupervisorPath(stateDirectory, rollback) ||
            !rollback.Version.Equals(updateState.RunningVersion, StringComparison.OrdinalIgnoreCase) ||
            !rollback.ExecutableSha256.Equals(runningSha256, StringComparison.OrdinalIgnoreCase) ||
            !rollback.ExecutableSha256.Equals(rollbackSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable);
        }

        try
        {
            await checks.VerifyMatchingPublisher(
                rollback.Executable,
                [selectedIdentity.Executable],
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
            return Unavailable(
                StagedSupervisorTransitionLoadKind.PublisherVerificationUnavailable);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var verifiedSelected = checks.ResolveExecutable(selectedIdentity.Executable);
        if (!BuildsEqual(verifiedSelected, selectedIdentity))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable);
        }
        var verifiedRollback = checks.ResolveExecutable(rollback.Executable);
        if (!BuildsEqual(verifiedRollback, rollback))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable);
        }

        return new(
            StagedSupervisorTransitionLoadKind.Loaded,
            new StagedSupervisorTransitionBuilds(
                RunningBuild: verifiedRollback!,
                SelectedBuild: verifiedSelected!,
                RollbackBuild: verifiedRollback!));
    }

    private static StagedSupervisorTransitionLoadResult Unavailable(
        StagedSupervisorTransitionLoadKind kind) => new(kind, Builds: null);

    private static bool IsVersionedSupervisorPath(
        string stateDirectory,
        SupervisorExecutableIdentity build)
    {
        try
        {
            build.Validate();
            var executable = Path.GetFullPath(build.Executable);
            return PathsEqual(
                executable,
                ContinuityPaths.VersionedSupervisorExecutable(
                    stateDirectory,
                    build.Version,
                    build.ExecutableSha256));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool BuildsEqual(
        SupervisorExecutableIdentity? first,
        SupervisorExecutableIdentity second) =>
        first is not null &&
        first.Version.Equals(second.Version, StringComparison.OrdinalIgnoreCase) &&
        PathsEqual(first.Executable, second.Executable) &&
        first.ExecutableSha256.Equals(
            second.ExecutableSha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string first, string second) =>
        Path.GetFullPath(first).Equals(
            Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);
}
