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
}

internal sealed record StagedSupervisorTransitionLoadResult(
    StagedSupervisorTransitionLoadKind Kind,
    StagedSupervisorTransitionBuilds? Builds);

internal sealed record StagedSupervisorTransitionChecks(
    Func<string, ContinuitySelectedBuildLoadResult> LoadSelectedBuild,
    Func<string, SupervisorExecutableIdentity?> ResolveExecutable)
{
    internal static StagedSupervisorTransitionChecks Native { get; } = new(
        ContinuitySelectedBuildReader.Load,
        ResolveNativeExecutable);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(LoadSelectedBuild);
        ArgumentNullException.ThrowIfNull(ResolveExecutable);
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
    internal static StagedSupervisorTransitionLoadResult Load(
        string stateDirectory,
        StagedSupervisorTransitionChecks? checks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
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

        var selectedLoad = checks.LoadSelectedBuild(stateDirectory);
        if (selectedLoad.Kind != ContinuitySelectedBuildLoadKind.Loaded ||
            selectedLoad.Build is not { } selectedBuild)
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.SelectedBuildUnavailable);
        }
        var selectedIdentity = new SupervisorExecutableIdentity(
            selectedBuild.Version,
            Path.GetFullPath(installState.InstalledExecutable),
            selectedBuild.ExecutableSha256);
        if (!IsVersionedSupervisorPath(stateDirectory, selectedIdentity))
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
            updateState.SelectedVersion.Equals(
                updateState.RunningVersion,
                StringComparison.OrdinalIgnoreCase))
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
            !IsVersionedSupervisorPath(stateDirectory, rollback) ||
            !rollback.Version.Equals(updateState.RunningVersion, StringComparison.OrdinalIgnoreCase) ||
            !rollback.ExecutableSha256.Equals(runningSha256, StringComparison.OrdinalIgnoreCase) ||
            !rollback.ExecutableSha256.Equals(rollbackSha256, StringComparison.OrdinalIgnoreCase))
        {
            return Unavailable(StagedSupervisorTransitionLoadKind.RollbackBuildUnavailable);
        }

        return new(
            StagedSupervisorTransitionLoadKind.Loaded,
            new StagedSupervisorTransitionBuilds(
                RunningBuild: rollback,
                SelectedBuild: selectedIdentity,
                RollbackBuild: rollback));
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
            var versionDirectory = Path.GetDirectoryName(executable);
            return Path.GetFileName(executable).Equals(
                       "CodexContinuity.exe",
                       StringComparison.OrdinalIgnoreCase) &&
                versionDirectory is not null &&
                Path.GetFileName(versionDirectory).Equals(
                    $"{build.Version}-{build.ExecutableSha256[..12]}",
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(versionDirectory)?.Equals(
                    Path.GetFullPath(ContinuityPaths.VersionsDirectory(stateDirectory)),
                    StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }
}
