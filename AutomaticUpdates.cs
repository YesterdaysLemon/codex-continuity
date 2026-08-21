using System.Diagnostics;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record PublishedContinuityRelease(
    string Version,
    DateTimeOffset PublishedAtUtc,
    string? ArchiveUrl,
    string? ChecksumUrl);

internal sealed record TrackedContinuityRelease(
    string Version,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset? StagedAtUtc,
    DateTimeOffset? AppliedAtUtc,
    string? LastError);

internal sealed record ContinuityUpdateState(
    int SchemaVersion,
    DateTimeOffset TrackingStartedAtUtc,
    DateTimeOffset? LastCheckedAtUtc,
    string BaselineVersion,
    string RunningVersion,
    string? LatestVersion,
    string? LastError,
    IReadOnlyList<TrackedContinuityRelease> Releases)
{
    public int ObservedCount => Releases.Count;
    public int StagedCount => Releases.Count(release => release.StagedAtUtc is not null);
    public int AppliedCount => Releases.Count(release => release.AppliedAtUtc is not null);

    public string LatestState
    {
        get
        {
            var latest = Releases.FirstOrDefault(release =>
                string.Equals(release.Version, LatestVersion, StringComparison.OrdinalIgnoreCase));
            return latest?.AppliedAtUtc is not null
                ? "active"
                : latest?.StagedAtUtc is not null
                    ? "staged"
                    : latest?.LastError is not null || LastError is not null
                        ? "failed"
                        : LatestVersion is null
                            ? "unknown"
                            : CompareVersions(RunningVersion, LatestVersion) >= 0
                                ? "active"
                                : "observed";
        }
    }

    private static int CompareVersions(string first, string second) =>
        System.Version.TryParse(first, out var firstVersion) &&
        System.Version.TryParse(second, out var secondVersion)
            ? firstVersion.CompareTo(secondVersion)
            : string.Compare(first, second, StringComparison.OrdinalIgnoreCase);
}

internal sealed class ContinuityUpdateStateStore(string path, int retainedReleases = 32)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal ContinuityUpdateState? Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ContinuityUpdateState>(
                    File.ReadAllText(path),
                    SerializerOptions)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal void Save(ContinuityUpdateState state)
    {
        var bounded = state with
        {
            Releases = state.Releases
                .OrderByDescending(release => ParseVersion(release.Version))
                .ThenByDescending(release => release.FirstObservedAtUtc)
                .Take(retainedReleases)
                .ToList(),
        };
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Update state path has no directory: {path}");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(bounded, SerializerOptions));
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

    private static System.Version ParseVersion(string version) =>
        System.Version.TryParse(version, out var parsed) ? parsed : new System.Version();
}

internal static class GitHubReleaseFeed
{
    private const string ReleasesUrl =
        "https://api.github.com/repos/YesterdaysLemon/codex-continuity/releases?per_page=30";
    private const string ArchiveName = "CodexContinuity-win-x64.zip";
    private const string ChecksumName = $"{ArchiveName}.sha256";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
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
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The GitHub releases response is not an array.");
        }

        var releases = new List<PublishedContinuityRelease>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (ReadBoolean(release, "draft") || ReadBoolean(release, "prerelease"))
            {
                continue;
            }
            var tag = release.GetProperty("tag_name").GetString();
            if (!TryNormalizeVersion(tag, out var version))
            {
                continue;
            }
            var publishedAt = release.GetProperty("published_at").GetDateTimeOffset();
            var assets = release.GetProperty("assets").EnumerateArray().ToList();
            releases.Add(new PublishedContinuityRelease(
                version,
                publishedAt,
                AssetUrl(assets, ArchiveName),
                AssetUrl(assets, ChecksumName)));
        }
        return releases
            .OrderByDescending(release => System.Version.Parse(release.Version))
            .ToList();
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.GetBoolean();

    private static string? AssetUrl(IEnumerable<JsonElement> assets, string name)
    {
        foreach (var asset in assets)
        {
            if (string.Equals(
                    asset.GetProperty("name").GetString(),
                    name,
                    StringComparison.OrdinalIgnoreCase) &&
                asset.TryGetProperty("browser_download_url", out var url))
            {
                return url.GetString();
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
    Func<PublishedContinuityRelease, Task> stageRelease,
    Func<DateTimeOffset> utcNow)
{
    internal async Task<ContinuityUpdateState> CheckAndStageAsync(
        string runningVersion,
        CancellationToken cancellationToken)
    {
        var now = utcNow();
        var state = MarkRunning(
            store.Load() ?? new ContinuityUpdateState(
                SchemaVersion: 1,
                TrackingStartedAtUtc: now,
                LastCheckedAtUtc: null,
                BaselineVersion: runningVersion,
                RunningVersion: runningVersion,
                LatestVersion: null,
                LastError: null,
                Releases: []),
            runningVersion,
            now);
        try
        {
            var releases = await readReleases(cancellationToken);
            state = Observe(state, releases, now);
            store.Save(state);
            var latest = releases.FirstOrDefault();
            if (latest is not null && CompareVersions(latest.Version, runningVersion) > 0)
            {
                var tracked = state.Releases.Single(release => release.Version == latest.Version);
                if (tracked.StagedAtUtc is null)
                {
                    if (latest.ArchiveUrl is null || latest.ChecksumUrl is null)
                    {
                        throw new InvalidDataException(
                            $"Release v{latest.Version} is missing the Windows archive or checksum asset.");
                    }
                    await stageRelease(latest);
                    state = MarkStaged(state, latest.Version, now);
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
        foreach (var release in published.Where(release =>
                     CompareVersions(release.Version, state.BaselineVersion) > 0))
        {
            tracked.TryAdd(release.Version, new TrackedContinuityRelease(
                release.Version,
                release.PublishedAtUtc,
                observedAt,
                StagedAtUtc: null,
                AppliedAtUtc: null,
                LastError: null));
        }
        return state with
        {
            LatestVersion = published.FirstOrDefault()?.Version,
            Releases = tracked.Values.ToList(),
        };
    }

    private static ContinuityUpdateState MarkRunning(
        ContinuityUpdateState state,
        string runningVersion,
        DateTimeOffset appliedAt)
    {
        var releases = state.Releases.Select(release =>
            release.Version == runningVersion && release.StagedAtUtc is not null
                ? release with { AppliedAtUtc = release.AppliedAtUtc ?? appliedAt, LastError = null }
                : release).ToList();
        return state with { RunningVersion = runningVersion, Releases = releases };
    }

    private static ContinuityUpdateState MarkStaged(
        ContinuityUpdateState state,
        string version,
        DateTimeOffset stagedAt) => state with
        {
            Releases = state.Releases.Select(release => release.Version == version
                ? release with { StagedAtUtc = stagedAt, LastError = null }
                : release).ToList(),
        };

    private static ContinuityUpdateState MarkFailure(
        ContinuityUpdateState state,
        string error,
        DateTimeOffset checkedAt) => state with
        {
            LastCheckedAtUtc = checkedAt,
            LastError = error,
            Releases = state.Releases.Select(release => release.Version == state.LatestVersion
                ? release with { LastError = error }
                : release).ToList(),
        };

    private static int CompareVersions(string first, string second) =>
        System.Version.Parse(first).CompareTo(System.Version.Parse(second));
}

internal static class AutomaticUpdateRunner
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    internal static async Task RunAsync(
        int port,
        string stateDirectory,
        string runningVersion,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CheckOnceAsync(
                    port,
                    stateDirectory,
                    runningVersion,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            try
            {
                await Task.Delay(CheckInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal static async Task<ContinuityUpdateState?> CheckOnceAsync(
        int port,
        string stateDirectory,
        string? runningVersion,
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
                GitHubReleaseFeed.ReadAsync,
                release => BootstrapInstaller.RunReleaseAsync(
                    new BootstrapRelease(
                        release.Version,
                        release.ArchiveUrl!,
                        release.ChecksumUrl!),
                    port,
                    trayMode,
                    startNow: false,
                    skipSelfTest: false,
                    quiet: true),
                () => DateTimeOffset.UtcNow);
            return await coordinator.CheckAndStageAsync(
                runningVersion ?? ResolveRunningVersion(stateDirectory),
                cancellationToken);
        }
    }

    private static string ResolveRunningVersion(string stateDirectory)
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
                    return $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        return new ContinuityUpdateStateStore(
            ContinuityPaths.UpdateStatusFile(stateDirectory)).Load()?.RunningVersion ??
            "0.0.0";
    }
}
