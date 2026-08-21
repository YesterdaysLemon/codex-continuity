using System.Text.Json;

namespace CodexContinuity;

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
    string SelectedVersion,
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
            if (LatestVersion is not null && string.Equals(
                    LatestVersion,
                    RunningVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "active";
            }
            if (latest?.StagedAtUtc is not null)
            {
                return string.Equals(
                    latest.Version,
                    SelectedVersion,
                    StringComparison.OrdinalIgnoreCase)
                        ? "staged"
                        : "deferred";
            }
            if (latest?.LastError is not null || LastError is not null)
            {
                return "failed";
            }
            if (LatestVersion is null)
            {
                return "unknown";
            }
            return CompareVersions(RunningVersion, LatestVersion) > 0 ? "ahead" : "observed";
        }
    }

    private static int CompareVersions(string first, string second) =>
        Version.TryParse(first, out var firstVersion) &&
        Version.TryParse(second, out var secondVersion)
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
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<ContinuityUpdateState>(
                    File.ReadAllText(path),
                    SerializerOptions)
                : null;
            return IsUsable(state) ? state : null;
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

    private static Version ParseVersion(string version) =>
        Version.TryParse(version, out var parsed) ? parsed : new Version();

    private static bool IsUsable(ContinuityUpdateState? state) =>
        state is
        {
            SchemaVersion: 1,
            BaselineVersion.Length: > 0,
            RunningVersion.Length: > 0,
            SelectedVersion.Length: > 0,
            Releases: not null,
        } && state.Releases.All(release =>
            release is not null && !string.IsNullOrWhiteSpace(release.Version));
}
