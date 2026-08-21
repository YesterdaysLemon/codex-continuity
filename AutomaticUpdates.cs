using System.Diagnostics;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record PublishedContinuityRelease(
    string Version,
    DateTimeOffset PublishedAtUtc,
    string? ArchiveUrl,
    string? ChecksumUrl);

internal sealed record ContinuityBuildIdentity(string Version, string ExecutableSha256);

internal sealed record StagedContinuityBuild(
    string ExecutableSha256,
    string RollbackExecutableSha256);

internal static class GitHubReleaseFeed
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/YesterdaysLemon/codex-continuity/releases?per_page=30";
    private const string ArchiveName = "CodexContinuity-win-x64.zip";
    private const string ChecksumName = $"{ArchiveName}.sha256";
    private const int MaximumResponseCharacters = 1024 * 1024;
    private const string ReleaseAssetPathPrefix =
        "/YesterdaysLemon/codex-continuity/releases/download/";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = MaximumResponseCharacters,
        DefaultRequestHeaders =
        {
            UserAgent = { new("CodexContinuity", "automatic-updater") },
        },
    };

    internal static async Task<IReadOnlyList<PublishedContinuityRelease>> ReadAsync(
        CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(ReleasesUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return Parse(json);
    }

    internal static IReadOnlyList<PublishedContinuityRelease> Parse(string json)
    {
        if (json.Length > MaximumResponseCharacters)
        {
            throw new InvalidDataException("The GitHub releases response exceeds the updater limit.");
        }
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The GitHub releases response is not an array.");
        }

        var releases = new List<PublishedContinuityRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                !release.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.ValueKind != JsonValueKind.String ||
                !release.TryGetProperty("published_at", out var publishedElement) ||
                !publishedElement.TryGetDateTimeOffset(out var publishedAt))
            {
                continue;
            }
            if (ReadBoolean(release, "draft") || ReadBoolean(release, "prerelease"))
            {
                continue;
            }
            var tag = tagElement.GetString();
            if (!TryNormalizeVersion(tag, out var version))
            {
                continue;
            }
            var assets = release.TryGetProperty("assets", out var assetsElement) &&
                assetsElement.ValueKind == JsonValueKind.Array
                    ? assetsElement.EnumerateArray().ToList()
                    : [];
            releases.Add(new PublishedContinuityRelease(
                version,
                publishedAt,
                AssetUrl(assets, ArchiveName),
                AssetUrl(assets, ChecksumName)));
        }
        return releases
            .OrderByDescending(
                release => release.Version,
                Comparer<string>.Create(ContinuitySemanticVersion.Compare))
            .ToList();
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? AssetUrl(IEnumerable<JsonElement> assets, string name)
    {
        foreach (var asset in assets)
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    nameElement.GetString(),
                    name,
                    StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) &&
                url.Scheme == Uri.UriSchemeHttps &&
                string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                url.AbsolutePath.StartsWith(
                    ReleaseAssetPathPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return url.AbsoluteUri;
            }
        }
        return null;
    }

    private static bool TryNormalizeVersion(string? tag, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(tag) || tag[0] is not ('v' or 'V') ||
            !System.Version.TryParse(tag[1..], out var parsed) ||
            parsed.Build < 0 || parsed.Revision >= 0)
        {
            return false;
        }
        version = $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";
        return true;
    }
}

internal sealed class AutomaticUpdateCoordinator(
    ContinuityUpdateStateStore store,
    Func<CancellationToken, Task<IReadOnlyList<PublishedContinuityRelease>>> readReleases,
    Func<PublishedContinuityRelease, Task<StagedContinuityBuild>> stageRelease,
    Func<DateTimeOffset> utcNow)
{
    internal async Task<ContinuityUpdateState> CheckAndStageAsync(
        ContinuityBuildIdentity runningBuild,
        ContinuityBuildIdentity? selectedBuild,
        bool runningProcessObserved,
        CancellationToken cancellationToken)
    {
        ValidateBuildIdentity(runningBuild, nameof(runningBuild));
        if (selectedBuild is not null)
        {
            ValidateBuildIdentity(selectedBuild, nameof(selectedBuild));
        }

        var now = utcNow();
        var loadResult = store.Load();
        var previousState = loadResult.Kind switch
        {
            ContinuityUpdateStateLoadKind.Loaded => loadResult.State!,
            ContinuityUpdateStateLoadKind.Missing => new ContinuityUpdateState(
                SchemaVersion: 1,
                TrackingStartedAtUtc: now,
                LastCheckedAtUtc: null,
                BaselineVersion: runningBuild.Version,
                RunningVersion: runningBuild.Version,
                SelectedVersion: selectedBuild?.Version ?? "0.0.0",
                RunningProcessObserved: runningProcessObserved,
                LatestVersion: null,
                LastError: null,
                ObservedCount: 0,
                StagedCount: 0,
                AppliedCount: 0,
                Releases: [],
                RunningExecutableSha256: runningBuild.ExecutableSha256),
            ContinuityUpdateStateLoadKind.Invalid => throw new InvalidDataException(
                "The persisted automatic-update state is invalid; it was not overwritten."),
            ContinuityUpdateStateLoadKind.UnsupportedSchema => throw new InvalidDataException(
                "The persisted automatic-update state uses an unsupported schema; it was not overwritten."),
            ContinuityUpdateStateLoadKind.Unreadable => throw new IOException(
                "The persisted automatic-update state could not be read; it was not overwritten."),
            _ => throw new InvalidOperationException("Unknown automatic-update state outcome."),
        };
        var state = MarkRunning(
            previousState,
            runningBuild,
            selectedBuild?.Version ?? "0.0.0",
            runningProcessObserved,
            now);
        try
        {
            var releases = await readReleases(cancellationToken);
            state = Observe(state, releases, now);
            store.Save(state);
            var latest = releases.FirstOrDefault();
            var latestComparison = latest is null
                ? -1
                : CompareVersions(latest.Version, runningBuild.Version);
            if (latest is not null &&
                (latestComparison > 0 || latestComparison == 0 && selectedBuild is null))
            {
                var tracked = state.Releases.SingleOrDefault(release =>
                    release.Version == latest.Version);
                if (tracked is null)
                {
                    tracked = new TrackedContinuityRelease(
                        latest.Version,
                        latest.PublishedAtUtc,
                        now,
                        StagedAtUtc: null,
                        AppliedAtUtc: null,
                        LastError: null);
                    state = state with { Releases = [.. state.Releases, tracked] };
                }
                var latestIsSelected = selectedBuild is not null && string.Equals(
                    selectedBuild.Version,
                    latest.Version,
                    StringComparison.OrdinalIgnoreCase);
                var selectedProofMatches = latestIsSelected &&
                    tracked.StagedExecutableSha256 is not null &&
                    string.Equals(
                        selectedBuild!.ExecutableSha256,
                        tracked.StagedExecutableSha256,
                        StringComparison.OrdinalIgnoreCase);
                var shouldStage = tracked.StagedAtUtc is null ||
                    selectedBuild is null ||
                    latestIsSelected && !selectedProofMatches;
                if (shouldStage)
                {
                    if (latest.ArchiveUrl is null || latest.ChecksumUrl is null)
                    {
                        throw new InvalidDataException(
                            $"Release v{latest.Version} is missing the Windows archive or checksum asset.");
                    }
                    var stagedBuild = await stageRelease(latest);
                    ValidateStagedBuild(stagedBuild);
                    state = MarkStaged(state, latest.Version, stagedBuild, now) with
                    {
                        SelectedVersion = latest.Version,
                    };
                }
            }
            state = state with { LastCheckedAtUtc = now, LastError = null };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state = MarkFailure(state, exception.Message, now);
        }
        store.Save(state);
        return state;
    }

    private static ContinuityUpdateState Observe(
        ContinuityUpdateState state,
        IReadOnlyList<PublishedContinuityRelease> published,
        DateTimeOffset observedAt)
    {
        var tracked = state.Releases.ToDictionary(release => release.Version);
        var newlyObserved = 0;
        foreach (var release in published.Where(release =>
                     CompareVersions(release.Version, state.BaselineVersion) > 0))
        {
            if (tracked.TryAdd(release.Version, new TrackedContinuityRelease(
                release.Version,
                release.PublishedAtUtc,
                observedAt,
                StagedAtUtc: null,
                AppliedAtUtc: null,
                LastError: null)))
            {
                newlyObserved++;
            }
        }
        return state with
        {
            LatestVersion = published.FirstOrDefault()?.Version,
            ObservedCount = state.ObservedCount + newlyObserved,
            Releases = tracked.Values.ToList(),
        };
    }

    private static ContinuityUpdateState MarkRunning(
        ContinuityUpdateState state,
        ContinuityBuildIdentity runningBuild,
        string selectedVersion,
        bool runningProcessObserved,
        DateTimeOffset appliedAt)
    {
        var newlyApplied = runningProcessObserved
            ? state.Releases.Count(release =>
                release.Version == runningBuild.Version &&
                release.StagedAtUtc is not null &&
                release.StagedExecutableSha256 is not null &&
                string.Equals(
                    release.StagedExecutableSha256,
                    runningBuild.ExecutableSha256,
                    StringComparison.OrdinalIgnoreCase) &&
                release.AppliedAtUtc is null)
            : 0;
        var releases = state.Releases.Select(release =>
            runningProcessObserved &&
            release.Version == runningBuild.Version &&
            release.StagedAtUtc is not null &&
            release.StagedExecutableSha256 is not null &&
            string.Equals(
                release.StagedExecutableSha256,
                runningBuild.ExecutableSha256,
                StringComparison.OrdinalIgnoreCase)
                ? release with { AppliedAtUtc = release.AppliedAtUtc ?? appliedAt, LastError = null }
                : release).ToList();
        return state with
        {
            RunningVersion = runningBuild.Version,
            RunningExecutableSha256 = runningBuild.ExecutableSha256,
            SelectedVersion = selectedVersion,
            RunningProcessObserved = runningProcessObserved,
            AppliedCount = state.AppliedCount + newlyApplied,
            Releases = releases,
        };
    }

    private static ContinuityUpdateState MarkStaged(
        ContinuityUpdateState state,
        string version,
        StagedContinuityBuild stagedBuild,
        DateTimeOffset stagedAt)
    {
        var newlyStaged = state.Releases.Any(release =>
            release.Version == version && release.StagedAtUtc is null);
        return state with
        {
            StagedCount = state.StagedCount + (newlyStaged ? 1 : 0),
            Releases = state.Releases.Select(release => release.Version == version
                ? release with
                {
                    StagedAtUtc = stagedAt,
                    LastError = null,
                    StagedExecutableSha256 = stagedBuild.ExecutableSha256,
                    RollbackExecutableSha256 = stagedBuild.RollbackExecutableSha256,
                }
                : release).ToList(),
        };
    }

    private static ContinuityUpdateState MarkFailure(
        ContinuityUpdateState state,
        string error,
        DateTimeOffset checkedAt)
    {
        var boundedError = BoundError(error);
        return state with
        {
            LastCheckedAtUtc = checkedAt,
            LastError = boundedError,
            Releases = state.Releases.Select(release => release.Version == state.LatestVersion
                ? release with { LastError = boundedError }
                : release).ToList(),
        };
    }

    private static string BoundError(string error)
    {
        const int maximumLength = 1000;
        var singleLine = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : $"{singleLine[..maximumLength]}…";
    }

    private static int CompareVersions(string first, string second) =>
        ContinuitySemanticVersion.Compare(first, second);

    private static void ValidateBuildIdentity(ContinuityBuildIdentity build, string parameterName)
    {
        if (!ContinuitySemanticVersion.IsValid(build.Version) ||
            !IsSha256(build.ExecutableSha256))
        {
            throw new ArgumentException(
                "Continuity build identity requires a semantic version and SHA-256 digest.",
                parameterName);
        }
    }

    private static void ValidateStagedBuild(StagedContinuityBuild build)
    {
        if (!IsSha256(build.ExecutableSha256) || !IsSha256(build.RollbackExecutableSha256))
        {
            throw new InvalidDataException(
                "Staging did not prove the selected and rollback executable digests.");
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);
}

internal static class AutomaticUpdateRunner
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    internal static Task RunAsync(
        string stateDirectory,
        string runningVersion,
        CancellationToken cancellationToken) => RunAsync(
            stateDirectory,
            runningVersion,
            async (directory, version, token) =>
            {
                await CheckOnceAsync(directory, version, token);
            },
            Task.Delay,
            cancellationToken);

    internal static async Task RunAsync(
        string stateDirectory,
        string runningVersion,
        Func<string, string, CancellationToken, Task> checkOnce,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await checkOnce(
                    stateDirectory,
                    runningVersion,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Automatic update check failed: {exception.Message}");
            }
            try
            {
                await delay(CheckInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal static Task<ContinuityUpdateState?> CheckOnceAsync(
        string stateDirectory,
        string? runningVersion,
        CancellationToken cancellationToken) => CheckOnceAsync(
            stateDirectory,
            runningVersion,
            GitHubReleaseFeed.ReadAsync,
            (release, port, trayMode) => BootstrapInstaller.RunReleaseAsync(
                new BootstrapRelease(
                    release.Version,
                    release.ArchiveUrl!,
                    release.ChecksumUrl!),
                port,
                trayMode,
                startNow: false,
                skipSelfTest: false,
                quiet: true),
            () => DateTimeOffset.UtcNow,
            cancellationToken);

    internal static async Task<ContinuityUpdateState?> CheckOnceAsync(
        string stateDirectory,
        string? runningVersion,
        Func<CancellationToken, Task<IReadOnlyList<PublishedContinuityRelease>>> readReleases,
        Func<PublishedContinuityRelease, int, TrayInstallMode, Task> stageRelease,
        Func<DateTimeOffset> utcNow,
        CancellationToken cancellationToken)
    {
        var installState = new InstallStateStore(
            ContinuityPaths.InstallStateFile(stateDirectory)).Load();
        if (installState is null)
        {
            return null;
        }

        Directory.CreateDirectory(stateDirectory);
        FileStream? updateLock = null;
        try
        {
            updateLock = new FileStream(
                ContinuityPaths.UpdateLockFile(stateDirectory),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return new ContinuityUpdateStateStore(
                ContinuityPaths.UpdateStatusFile(stateDirectory)).Load();
        }

        await using (updateLock)
        {
            var trayMode = installState.InstalledTrayExecutable is null
                ? TrayInstallMode.Disabled
                : TrayInstallMode.Enabled;
            var coordinator = new AutomaticUpdateCoordinator(
                new ContinuityUpdateStateStore(
                    ContinuityPaths.UpdateStatusFile(stateDirectory)),
                readReleases,
                release => stageRelease(release, installState.Port, trayMode),
                utcNow);
            var resolvedRunning = runningVersion is null
                ? ResolveRunningVersion(stateDirectory)
                : new RunningContinuityVersion(runningVersion, ProcessObserved: true);
            return await coordinator.CheckAndStageAsync(
                resolvedRunning.Version,
                ResolveSelectedVersion(installState.InstalledExecutable),
                resolvedRunning.ProcessObserved,
                cancellationToken);
        }
    }

    private static RunningContinuityVersion ResolveRunningVersion(string stateDirectory)
    {
        var status = new SupervisorStatusStore(
            ContinuityPaths.SupervisorStatusFile(stateDirectory)).Read() ??
            new SupervisorStatusStore(ContinuityPaths.SupervisorStatusFile(
                ContinuityPaths.LegacyOpenAiStateDirectory)).Read();
        if (status is not null)
        {
            try
            {
                using var process = Process.GetProcessById(status.SupervisorProcessId);
                var path = process.MainModule?.FileName;
                if (path is not null)
                {
                    var version = FileVersionInfo.GetVersionInfo(path);
                    return new RunningContinuityVersion(
                        $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}",
                        ProcessObserved: true);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        var lastRunningVersion = new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load()?.RunningVersion;
        return new RunningContinuityVersion(lastRunningVersion ?? "0.0.0", ProcessObserved: false);
    }

    internal static string? ResolveSelectedVersion(string executable)
    {
        if (!File.Exists(executable))
        {
            return null;
        }
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executable);
            return version.FileMajorPart == 0 && version.FileMinorPart == 0 && version.FileBuildPart == 0
                ? null
                : $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private sealed record RunningContinuityVersion(string Version, bool ProcessObserved);
}
