using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexContinuity;

internal sealed record TrackedContinuityRelease(
    string Version,
    DateTimeOffset PublishedAtUtc,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset? StagedAtUtc,
    DateTimeOffset? AppliedAtUtc,
    string? LastError,
    string? StagedExecutableSha256 = null,
    string? RollbackExecutableSha256 = null);

internal sealed record ContinuityUpdateState(
    int SchemaVersion,
    DateTimeOffset TrackingStartedAtUtc,
    DateTimeOffset? LastCheckedAtUtc,
    string BaselineVersion,
    string RunningVersion,
    string SelectedVersion,
    bool RunningProcessObserved,
    string? LatestVersion,
    string? LastError,
    int ObservedCount,
    int StagedCount,
    int AppliedCount,
    IReadOnlyList<TrackedContinuityRelease> Releases,
    string? RunningExecutableSha256 = null)
{
    public string LatestState
    {
        get
        {
            var latest = Releases.FirstOrDefault(release =>
                string.Equals(release.Version, LatestVersion, StringComparison.OrdinalIgnoreCase));
            if (RunningProcessObserved &&
                RunningExecutableSha256 is not null &&
                LatestVersion is not null &&
                (latest?.StagedExecutableSha256 is null || string.Equals(
                    latest.StagedExecutableSha256,
                    RunningExecutableSha256,
                    StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(
                    LatestVersion,
                    RunningVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                return "active";
            }
            if (!RunningProcessObserved && latest?.AppliedAtUtc is not null)
            {
                return "inactive";
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
            if (!RunningProcessObserved)
            {
                return "inactive";
            }
            return CompareVersions(RunningVersion, LatestVersion) > 0 ? "ahead" : "observed";
        }
    }

    private static int CompareVersions(string first, string second) =>
        ContinuitySemanticVersion.Compare(first, second);
}

internal enum ContinuityUpdateStateLoadKind
{
    Loaded,
    Missing,
    Invalid,
    UnsupportedSchema,
    Unreadable,
}

internal sealed record ContinuityUpdateStateLoadResult(
    ContinuityUpdateStateLoadKind Kind,
    ContinuityUpdateState? State);

internal static class ContinuitySemanticVersion
{
    internal const int MaximumLength = 64;
    private static readonly Regex Pattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant);

    internal static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaximumLength } && Pattern.IsMatch(value);

    internal static int Compare(string first, string second)
    {
        var firstParts = Parse(first);
        var secondParts = Parse(second);
        for (var index = 0; index < firstParts.Core.Count; index++)
        {
            var comparison = CompareNumericIdentifier(
                firstParts.Core[index],
                secondParts.Core[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (firstParts.PreRelease.Count == 0 || secondParts.PreRelease.Count == 0)
        {
            return firstParts.PreRelease.Count == secondParts.PreRelease.Count
                ? 0
                : firstParts.PreRelease.Count == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Min(
                 firstParts.PreRelease.Count,
                 secondParts.PreRelease.Count); index++)
        {
            var firstIdentifier = firstParts.PreRelease[index];
            var secondIdentifier = secondParts.PreRelease[index];
            var firstIsNumeric = firstIdentifier.All(char.IsAsciiDigit);
            var secondIsNumeric = secondIdentifier.All(char.IsAsciiDigit);
            var comparison = firstIsNumeric && secondIsNumeric
                ? CompareNumericIdentifier(firstIdentifier, secondIdentifier)
                : firstIsNumeric != secondIsNumeric
                    ? firstIsNumeric ? -1 : 1
                    : string.Compare(firstIdentifier, secondIdentifier, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return firstParts.PreRelease.Count.CompareTo(secondParts.PreRelease.Count);
    }

    private static (IReadOnlyList<string> Core, IReadOnlyList<string> PreRelease) Parse(
        string version)
    {
        var withoutBuild = version.Split('+', count: 2)[0];
        var versionParts = withoutBuild.Split('-', count: 2);
        return (
            versionParts[0].Split('.'),
            versionParts.Length == 1 ? [] : versionParts[1].Split('.'));
    }

    private static int CompareNumericIdentifier(string first, string second) =>
        first.Length != second.Length
            ? first.Length.CompareTo(second.Length)
            : string.Compare(first, second, StringComparison.Ordinal);
}

internal sealed class ContinuityUpdateStateStore(string path, int retainedReleases = 32)
{
    private const int CurrentSchemaVersion = 1;
    private const int MaximumStateBytes = 1024 * 1024;
    private const int MaximumErrorLength = 2048;
    private const int MaximumLoadedReleases = 256;
    private static readonly IComparer<string> SemanticVersionComparer =
        Comparer<string>.Create(ContinuitySemanticVersion.Compare);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal ContinuityUpdateStateLoadResult Load()
    {
        if (!File.Exists(path))
        {
            return new(ContinuityUpdateStateLoadKind.Missing, State: null);
        }

        try
        {
            if (new FileInfo(path).Length > MaximumStateBytes)
            {
                return new(ContinuityUpdateStateLoadKind.Invalid, State: null);
            }

            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                !schemaElement.TryGetInt32(out var schemaVersion))
            {
                return new(ContinuityUpdateStateLoadKind.Invalid, State: null);
            }
            if (schemaVersion != CurrentSchemaVersion)
            {
                return new(ContinuityUpdateStateLoadKind.UnsupportedSchema, State: null);
            }

            var state = JsonSerializer.Deserialize<ContinuityUpdateState>(json, SerializerOptions);
            return IsUsable(state)
                ? new(
                    ContinuityUpdateStateLoadKind.Loaded,
                    NormalizeCounts(state!))
                : new(ContinuityUpdateStateLoadKind.Invalid, State: null);
        }
        catch (JsonException)
        {
            return new(ContinuityUpdateStateLoadKind.Invalid, State: null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new(ContinuityUpdateStateLoadKind.Unreadable, State: null);
        }
    }

    internal void Save(ContinuityUpdateState state)
    {
        if (retainedReleases is < 1 or > MaximumLoadedReleases)
        {
            throw new InvalidOperationException(
                $"Retained update releases must be between 1 and {MaximumLoadedReleases}.");
        }
        if (!IsUsable(state))
        {
            throw new ArgumentException("Update state is invalid or unbounded.", nameof(state));
        }

        var bounded = NormalizeCounts(state) with
        {
            Releases = state.Releases
                .OrderByDescending(release => release.Version, SemanticVersionComparer)
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

    private static ContinuityUpdateState NormalizeCounts(ContinuityUpdateState state) => state with
    {
        ObservedCount = Math.Max(state.ObservedCount, state.Releases.Count),
        StagedCount = Math.Max(
            state.StagedCount,
            state.Releases.Count(release => release.StagedAtUtc is not null)),
        AppliedCount = Math.Max(
            state.AppliedCount,
            state.Releases.Count(release => release.AppliedAtUtc is not null)),
    };

    private static bool IsUsable(ContinuityUpdateState? state) =>
        state is
        {
            SchemaVersion: CurrentSchemaVersion,
            Releases: not null,
        } &&
        state.TrackingStartedAtUtc != default &&
        IsSemanticVersion(state.BaselineVersion) &&
        IsSemanticVersion(state.RunningVersion) &&
        IsSemanticVersion(state.SelectedVersion) &&
        (state.LatestVersion is null || IsSemanticVersion(state.LatestVersion)) &&
        IsBoundedOptionalText(state.LastError, MaximumErrorLength) &&
        IsOptionalSha256(state.RunningExecutableSha256) &&
        state.ObservedCount >= 0 &&
        state.StagedCount >= 0 &&
        state.AppliedCount >= 0 &&
        state.Releases.Count <= MaximumLoadedReleases &&
        state.Releases.All(release =>
            release is not null &&
            IsSemanticVersion(release.Version) &&
            release.PublishedAtUtc != default &&
            release.FirstObservedAtUtc != default &&
            IsOptionalSha256(release.StagedExecutableSha256) &&
            IsOptionalSha256(release.RollbackExecutableSha256) &&
            IsBoundedOptionalText(release.LastError, MaximumErrorLength));

    private static bool IsSemanticVersion(string? value) =>
        ContinuitySemanticVersion.IsValid(value);

    private static bool IsBoundedOptionalText(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength;

    private static bool IsOptionalSha256(string? value) =>
        value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
}
