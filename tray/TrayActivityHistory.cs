using System.Globalization;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using CodexContinuity.Contracts;

namespace CodexContinuity.Tray;

internal enum TrayActivityKind
{
    Staged,
    Failure,
    RolledBack,
    BackendUnavailable,
    BackendRecovered,
    UserMutationSucceeded,
}

internal sealed record TrayActivityEvent(
    TrayActivityKind Kind,
    string? Version,
    string State,
    string Summary,
    bool Deduplicate = true);

internal sealed record TrayActivityEntry(
    TrayActivityKind Kind,
    DateTimeOffset OccurredAtUtc,
    string? Version,
    string State,
    string Summary);

internal static class TrayActivityCatalog
{
    internal const string StagedSummary =
        "A verified Continuity update was staged without stopping Codex.";
    internal const string FailureSummary =
        "Safe Continuity update activation failed; diagnostics are available.";
    internal const string RolledBackSummary =
        "Safe Continuity update activation rolled back; active agents were preserved.";
    internal const string BackendUnavailableSummary =
        "The Continuity backend became unavailable.";
    internal const string BackendRecoveredSummary =
        "The Continuity backend recovered; active agents were preserved.";
    internal const string UpdateCheckSummary = "A Continuity update check completed.";
    internal const string AutomaticApplySummary = "Automatic activation settings changed.";
    internal const string SnoozeSummary = "Activation snooze settings changed.";
    internal const string ActivationWindowSummary = "Activation window settings changed.";
    internal const string BackendRestartSummary = "The Continuity backend restart was requested.";
    internal const string RollbackSelectionSummary =
        "The previous verified build was selected for next safe start.";

    private static readonly HashSet<string> AllowedStates =
    [
        "staged",
        "failed",
        "rolledBack",
        "unavailable",
        "healthy",
        "succeeded",
        "update",
    ];

    private static readonly HashSet<string> AllowedSummaries =
    [
        StagedSummary,
        FailureSummary,
        RolledBackSummary,
        BackendUnavailableSummary,
        BackendRecoveredSummary,
        UpdateCheckSummary,
        AutomaticApplySummary,
        SnoozeSummary,
        ActivationWindowSummary,
        BackendRestartSummary,
        RollbackSelectionSummary,
    ];

    internal static bool IsAllowed(TrayActivityEvent activity) =>
        Enum.IsDefined(activity.Kind) &&
        AllowedStates.Contains(activity.State) &&
        AllowedSummaries.Contains(activity.Summary) &&
        IsPublicVersion(activity.Version);

    internal static bool IsAllowed(TrayActivityEntry activity) => IsAllowed(
        new TrayActivityEvent(
            activity.Kind,
            activity.Version,
            activity.State,
            activity.Summary));

    internal static bool IsPublicVersion(string? version) =>
        version is null ||
        (version.Length <= 64 && Regex.IsMatch(
            version,
            @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$",
            RegexOptions.CultureInvariant));

    internal static string KindText(TrayActivityKind kind) => kind switch
    {
        TrayActivityKind.Staged => "staged",
        TrayActivityKind.Failure => "failure",
        TrayActivityKind.RolledBack => "rollback",
        TrayActivityKind.BackendUnavailable => "backendUnavailable",
        TrayActivityKind.BackendRecovered => "backendRecovered",
        TrayActivityKind.UserMutationSucceeded => "userMutationSucceeded",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    internal static bool TryParseKind(string? text, out TrayActivityKind kind)
    {
        kind = text switch
        {
            "staged" => TrayActivityKind.Staged,
            "failure" => TrayActivityKind.Failure,
            "rollback" => TrayActivityKind.RolledBack,
            "backendUnavailable" => TrayActivityKind.BackendUnavailable,
            "backendRecovered" => TrayActivityKind.BackendRecovered,
            "userMutationSucceeded" => TrayActivityKind.UserMutationSucceeded,
            _ => (TrayActivityKind)(-1),
        };
        return Enum.IsDefined(kind);
    }
}

internal static class TrayActivityPlanner
{
    internal static TrayActivityEvent? Plan(
        TrayNotificationSnapshot? previous,
        TrayNotificationSnapshot current)
    {
        if (previous is null)
        {
            return null;
        }

        if (current.ApplyState == ContinuityUpdateApplyStateNames.RolledBack &&
            previous.ApplyState != ContinuityUpdateApplyStateNames.RolledBack)
        {
            return new(
                TrayActivityKind.RolledBack,
                current.ApplyTargetVersion,
                "rolledBack",
                TrayActivityCatalog.RolledBackSummary);
        }

        if (current.ApplyState == ContinuityUpdateApplyStateNames.Failed &&
            previous.ApplyState != ContinuityUpdateApplyStateNames.Failed)
        {
            return new(
                TrayActivityKind.Failure,
                current.ApplyTargetVersion,
                "failed",
                TrayActivityCatalog.FailureSummary);
        }

        if (current.LatestState == ContinuityUpdateCheckStateNames.Staged &&
            (previous.LatestState != ContinuityUpdateCheckStateNames.Staged ||
             !string.Equals(
                 previous.LatestVersion,
                 current.LatestVersion,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return new(
                TrayActivityKind.Staged,
                current.LatestVersion,
                "staged",
                TrayActivityCatalog.StagedSummary);
        }

        if (current.Health == ContinuityHealth.Healthy &&
            previous.Health != ContinuityHealth.Healthy)
        {
            return new(
                TrayActivityKind.BackendRecovered,
                null,
                "healthy",
                TrayActivityCatalog.BackendRecoveredSummary);
        }

        if (current.Health == ContinuityHealth.Unavailable &&
            previous.Health != ContinuityHealth.Unavailable)
        {
            return new(
                TrayActivityKind.BackendUnavailable,
                null,
                "unavailable",
                TrayActivityCatalog.BackendUnavailableSummary);
        }

        return null;
    }
}

internal sealed class TrayActivityHistoryStore
{
    internal const int CurrentSchemaVersion = 1;
    internal const int MaximumEntries = 32;
    internal const int MaximumBytes = 16 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string path;

    internal TrayActivityHistoryStore(string path)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
    }

    internal string Path => path;

    internal IReadOnlyList<TrayActivityEntry> Load()
    {
        var entries = LoadCore(out _);
        return entries;
    }

    internal bool TryAppend(TrayActivityEvent activity, DateTimeOffset occurredAtUtc)
    {
        if (!TrayActivityCatalog.IsAllowed(activity) ||
            occurredAtUtc.Offset != TimeSpan.Zero)
        {
            return false;
        }

        var existing = LoadCore(out var valid);
        if (!valid)
        {
            return false;
        }

        var entry = new TrayActivityEntry(
            activity.Kind,
            occurredAtUtc.ToUniversalTime(),
            activity.Version,
            activity.State,
            activity.Summary);
        if (activity.Deduplicate && existing.Count > 0 && SameEvent(existing[0], entry))
        {
            return false;
        }

        var updated = new List<TrayActivityEntry>(existing.Count + 1) { entry };
        updated.AddRange(existing.Take(MaximumEntries - 1));
        return TryWrite(updated);
    }

    private IReadOnlyList<TrayActivityEntry> LoadCore(out bool valid)
    {
        valid = true;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaximumBytes)
            {
                valid = false;
                return [];
            }

            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != CurrentSchemaVersion ||
                !root.TryGetProperty("entries", out var entriesElement) ||
                entriesElement.ValueKind != JsonValueKind.Array)
            {
                valid = false;
                return [];
            }

            var entries = new List<TrayActivityEntry>();
            foreach (var element in entriesElement.EnumerateArray())
            {
                if (!TryReadEntry(element, out var entry))
                {
                    valid = false;
                    return [];
                }
                entries.Add(entry);
            }

            return entries
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .Take(MaximumEntries)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or InvalidOperationException or FormatException or
                ArgumentException or NotSupportedException)
        {
            valid = false;
            return [];
        }
    }

    private static bool TryReadEntry(
        JsonElement element,
        out TrayActivityEntry entry)
    {
        entry = default!;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !TrayActivityCatalog.TryParseKind(kindElement.GetString(), out var kind) ||
            !element.TryGetProperty("occurredAtUtc", out var occurredElement) ||
            occurredElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                occurredElement.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var occurredAtUtc) ||
            occurredAtUtc.Offset != TimeSpan.Zero ||
            !element.TryGetProperty("state", out var stateElement) ||
            stateElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("summary", out var summaryElement) ||
            summaryElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? version = null;
        if (element.TryGetProperty("version", out var versionElement))
        {
            if (versionElement.ValueKind != JsonValueKind.Null &&
                (versionElement.ValueKind != JsonValueKind.String ||
                 (version = versionElement.GetString()) is null))
            {
                return false;
            }
        }

        var candidate = new TrayActivityEntry(
            kind,
            occurredAtUtc.ToUniversalTime(),
            version,
            stateElement.GetString() ?? string.Empty,
            summaryElement.GetString() ?? string.Empty);
        if (!TrayActivityCatalog.IsAllowed(candidate))
        {
            return false;
        }

        entry = candidate;
        return true;
    }

    private static bool SameEvent(TrayActivityEntry left, TrayActivityEntry right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.State, right.State, StringComparison.Ordinal) &&
        string.Equals(left.Summary, right.Summary, StringComparison.Ordinal);

    private static bool TryWrite(IReadOnlyList<TrayActivityEntry> entries, string path)
    {
        try
        {
            var payload = new HistoryDocument(
                CurrentSchemaVersion,
                entries.Select(entry => new HistoryEntry(
                    TrayActivityCatalog.KindText(entry.Kind),
                    entry.OccurredAtUtc.ToUniversalTime().ToString("O"),
                    entry.Version,
                    entry.State,
                    entry.Summary)).ToArray());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            if (bytes.Length > MaximumBytes)
            {
                return false;
            }

            var directory = System.IO.Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }
            Directory.CreateDirectory(directory);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, path, overwrite: true);
                return true;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or
                InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool TryWrite(IReadOnlyList<TrayActivityEntry> entries)
    {
        if (entries.Count > MaximumEntries)
        {
            entries = entries.Take(MaximumEntries).ToArray();
        }

        while (entries.Count > 0)
        {
            if (TryWrite(entries, path))
            {
                return true;
            }
            entries = entries.Take(entries.Count - 1).ToArray();
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record HistoryDocument(int SchemaVersion, IReadOnlyList<HistoryEntry> Entries);

    private sealed record HistoryEntry(
        string Kind,
        string OccurredAtUtc,
        string? Version,
        string State,
        string Summary);
}

internal static class TrayActivityHistoryFormatter
{
    internal static string Render(IReadOnlyList<TrayActivityEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return "No recent Continuity activity.";
        }

        return string.Join(
            Environment.NewLine,
            entries.Take(TrayActivityHistoryStore.MaximumEntries).Select(entry =>
            {
                var version = entry.Version is null ? string.Empty : $" (v{entry.Version})";
                return $"{entry.OccurredAtUtc:yyyy-MM-dd HH:mm} UTC — " +
                    $"{entry.Summary}{version} [{entry.State}]";
            }));
    }
}

internal sealed class TrayActivityHistoryDialog : Form
{
    internal TrayActivityHistoryDialog(string renderedHistory)
    {
        AccessibleRole = AccessibleRole.Dialog;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Recent Continuity activity";
        ClientSize = new Size(620, 380);

        var description = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Text = "Only bounded, redacted Continuity events are shown here.",
            AccessibleName = "Activity history explanation",
        };
        var history = new TextBox
        {
            AcceptsReturn = true,
            AccessibleName = "Recent Continuity activity",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            TabIndex = 0,
            Text = renderedHistory,
            WordWrap = false,
        };
        var close = new Button
        {
            AutoSize = true,
            AccessibleName = "Close recent activity",
            DialogResult = DialogResult.Cancel,
            Text = "Close",
            TabIndex = 1,
        };
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(close);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(description, 0, 0);
        layout.Controls.Add(history, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        CancelButton = close;
    }
}
