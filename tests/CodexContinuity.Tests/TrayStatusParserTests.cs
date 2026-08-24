using CodexContinuity.Tray;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class TrayStatusParserTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UsesBrandedIconForEveryHealthState(int health)
    {
        var applicationIcon = SystemIcons.Application;

        Assert.Same(applicationIcon, TrayStatusPresentation.IconForHealth(
            (ContinuityHealth)health,
            applicationIcon));
    }

    [Theory]
    [InlineData("{\"port\":45124}", 45124)]
    [InlineData("{}", TrayStatusClient.DefaultPort)]
    [InlineData("[]", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":\"45124\"}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":0}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":-1}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":65536}", TrayStatusClient.DefaultPort)]
    [InlineData("{\"port\":2147483648}", TrayStatusClient.DefaultPort)]
    public void InstalledPortFallsBackForMissingOrInvalidValues(string json, int expected)
    {
        Assert.Equal(expected, TrayStatusClient.ParseInstalledPort(json));
    }

    [Fact]
    public void ParsesHealthyStatusWithoutReadingThreadNames()
    {
        const string json =
            """
            {
              "ready": true,
              "activeThreadCount": 3,
              "activeThreads": [{ "id": "secret", "name": "do not expose" }],
              "supervisor": { "state": "running" }
            }
            """;

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(
            new TrayStatusSnapshot(ContinuityHealth.Healthy, 3, "Backend ready"),
            status);
        Assert.DoesNotContain("do not expose", status.Detail);
    }

    [Fact]
    public void TreatsReadyBackendWithUnknownSupervisorAsDegraded()
    {
        const string json = """{"ready":true,"activeThreadCount":1,"supervisor":null}""";

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(ContinuityHealth.Degraded, status.Health);
    }

    [Fact]
    public void PresentsArmedSupervisorWithoutOfferingRecovery()
    {
        const string json =
            """{"ready":false,"activeThreadCount":0,"supervisor":{"state":"waitingForCodexExit"}}""";

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(
            new TrayStatusSnapshot(
                ContinuityHealth.Degraded,
                0,
                "Armed; waiting for the current Codex desktop to close naturally"),
            status);
        Assert.False(TrayStatusPresentation.ShowRecovery(status.Health));
    }

    [Fact]
    public void ParsesActualArmedCommandPayloadWithUnknownCounts()
    {
        var supervisor = new SupervisorStatus(
            State: "waitingForCodexExit",
            SupervisorProcessId: Environment.ProcessId,
            BackendProcessId: null,
            Port: 45123,
            CodexHome: "bounded-home",
            ConsecutiveFailures: 0,
            LastExitCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            NextRetryAtUtc: null,
            Detail: "Continuity is armed.");

        var json = CodexContinuity.Program.WaitingStatusJson(supervisor);
        using var document = JsonDocument.Parse(json);
        var status = TrayStatusParser.Parse(json);

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("threadCount").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("activeThreadCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("activeThreads").ValueKind);
        Assert.Equal(
            new TrayStatusSnapshot(
                ContinuityHealth.Degraded,
                null,
                "Armed; waiting for the current Codex desktop to close naturally"),
            status);
        Assert.False(TrayStatusPresentation.ShowRecovery(status.Health));
    }

    [Fact]
    public async Task TrayAcceptsArmedPayloadWithNonReadyExitCode()
    {
        var executable = Path.GetTempFileName();
        var supervisor = new SupervisorStatus(
            State: "waitingForCodexExit",
            SupervisorProcessId: Environment.ProcessId,
            BackendProcessId: null,
            Port: TrayStatusClient.DefaultPort,
            CodexHome: "bounded-home",
            ConsecutiveFailures: 0,
            LastExitCode: null,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            NextRetryAtUtc: null,
            Detail: "Continuity is armed.");
        try
        {
            var client = new TrayStatusClient(
                executable,
                mutationProcessRunner: (_, _, _) => Task.FromResult(new TrayCommandResult(
                    2,
                    CodexContinuity.Program.WaitingStatusJson(supervisor),
                    string.Empty)));

            var status = await client.ReadAsync(CancellationToken.None);

            Assert.Equal(ContinuityHealth.Degraded, status.Health);
            Assert.Null(status.ActiveAgentCount);
            Assert.False(TrayStatusPresentation.ShowRecovery(status.Health));
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void TreatsPreviouslyAttachedForeignBackendAsDegraded()
    {
        const string json =
            """{"ready":true,"activeThreadCount":1,"supervisor":{"state":"attached"}}""";

        var status = TrayStatusParser.Parse(json);

        Assert.Equal(ContinuityHealth.Degraded, status.Health);
    }

    [Theory, InlineData(0, false), InlineData(1, false), InlineData(2, true)]
    public void ShowsRecoveryOnlyWhileUnavailable(int health, bool expected) =>
        Assert.Equal(expected, TrayStatusPresentation.ShowRecovery((ContinuityHealth)health));

    [Fact]
    public void ParsesObservedStagedAndAppliedUpdateCounts()
    {
        const string json =
            """
            {
              "runningVersion": "0.2.1",
              "runningProcessObserved": true,
              "latestVersion": "0.3.0",
              "observedCount": 2,
              "stagedCount": 1,
              "appliedCount": 0,
              "latestState": "staged",
              "lastError": null
            }
            """;

        var update = TrayStatusParser.ParseUpdate(json);

        Assert.Equal(
            new ContinuityUpdateSnapshot("0.2.1", true, "0.3.0", 2, 1, 0, "staged", null),
            update);
        Assert.Equal(
            "Updates: 2 observed / 1 staged / 0 applied",
            TrayStatusPresentation.UpdateCounts(update));
    }

    [Fact]
    public async Task UpdateReadUsesTheExactRepositoryReleaseNotesLink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-release-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(stateDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(stateDirectory, "update-status.json"),
                """{"latestVersion":"0.6.0","latestReleaseUrl":"https://example.invalid/redirect"}""");
            var client = new TrayStatusClient(
                "status.exe",
                applicationDirectory,
                stateDirectory,
                legacyDirectory);

            var update = await client.ReadUpdateAsync(CancellationToken.None);

            Assert.Equal(
                "https://github.com/YesterdaysLemon/codex-continuity/releases/tag/v0.6.0",
                update.LatestReleaseUrl);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ReportsUnsignedInstalledBuildAsUpdaterReadinessBlocker()
    {
        var update = TrayStatusParser.ParseUpdate(
            """
            {
              "runningVersion": "0.5.0",
              "runningProcessObserved": true,
              "latestVersion": "0.6.0",
              "latestState": "failed",
              "lastError": "The installed Continuity build does not have a valid Authenticode signature; automatic staging is disabled for unsigned or development builds."
            }
            """);

        Assert.Equal("unsignedInstalledBuild", update.UpdaterReadiness);
        Assert.Contains(
            "installed build is unsigned",
            TrayStatusPresentation.UpdaterReadinessDetail(update),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsesHonestCodexDesktopStoreStatusWithoutClaimingEligibility()
    {
        var desktop = TrayStatusParser.ParseDesktopUpdate(
            """
            {
              "codexDesktopUpdate": {
                "installedVersion": "26.818.2872.0",
                "advertisedVersion": "26.818.3698.0",
                "manifestNewerThanInstalled": true,
                "microsoftStoreAvailability": "notChecked",
                "recommendedAction": "checkMicrosoftStore",
                "detail": "Store eligibility is not proven."
              }
            }
            """);

        Assert.True(desktop.ShouldOfferMicrosoftStore);
        Assert.Equal("notChecked", desktop.MicrosoftStoreAvailability);
        Assert.True(TrayStatusPresentation.ShowMicrosoftStoreUpdate(desktop));
    }

    [Fact]
    public void VersionProjectionLabelsRecordedBuildsSeparatelyFromProvenBuilds()
    {
        var update = new ContinuityUpdateSnapshot(
            "0.5.0",
            true,
            "0.6.0",
            1,
            1,
            0,
            "staged",
            null,
            SelectedVersion: "0.6.0",
            RollbackVersion: "0.4.0",
            RunningBuild: new("0.5.0", "running.exe", new string('a', 64), true, null),
            SelectedBuild: new("0.6.0", "selected.exe", new string('b', 64), true, null),
            RollbackBuild: new("0.4.0", "rollback.exe", new string('c', 64), false, "digest mismatch"));

        var detail = TrayStatusPresentation.VersionDetail(update);

        Assert.Contains("Running: v0.5.0 (proven)", detail);
        Assert.Contains("Startup target: v0.6.0 (proven)", detail);
        Assert.Contains("Rollback: v0.4.0 (recorded)", detail);
        Assert.False(TrayStatusPresentation.ShowRollback(update));
    }

    [Fact]
    public void NotificationsAreTransitionBasedAndDeduplicated()
    {
        var previous = new TrayNotificationSnapshot(
            ContinuityHealth.Healthy,
            "observed",
            "0.6.0",
            "waiting",
            "0.6.0",
            null);
        var staged = previous with
        {
            LatestState = "staged",
            LatestVersion = "0.6.0",
        };
        var notification = TrayNotificationPlanner.Plan(
            previous,
            staged,
            "https://github.com/YesterdaysLemon/codex-continuity/releases/tag/v0.6.0");
        var deduper = new TrayNotificationDeduper();

        Assert.NotNull(notification);
        Assert.Equal(TrayNotificationAction.OpenReleaseNotes, notification!.Action);
        Assert.True(deduper.ShouldShow(notification));
        Assert.False(deduper.ShouldShow(notification));
        Assert.Null(TrayNotificationPlanner.Plan(staged, staged, notification.Key));
    }

    [Fact]
    public void DiagnosticsSummaryOmitsPathsAndThreadIdentifiers()
    {
        var summary = TrayDiagnosticsFormatter.Format(
            new TrayStatusSnapshot(
                ContinuityHealth.Healthy,
                2,
                @"Backend failed at C:\Users\Yeste\private-state.json"),
            new ContinuityUpdateSnapshot(
                "0.5.0",
                true,
                "0.6.0",
                1,
                1,
                0,
                "staged",
                null,
                SelectedVersion: "0.6.0",
                RollbackVersion: "0.4.0",
                UpdaterReadiness: "ready"),
            ContinuityApplySnapshot.Default,
            new TrayDesktopUpdateSnapshot(
                "26.818.2872.0",
                "26.818.3698.0",
                true,
                "notChecked",
                "checkMicrosoftStore",
                "not proven"));

        Assert.DoesNotContain("C:\\Users", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thread-secret", summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Running version: 0.5.0", summary);
    }

    [Fact]
    public void CustomActivationWindowPlannerValidatesOvernightRangeAndExactArguments()
    {
        var valid = TrayActivationWindowPlanner.TryCreate(
            "22:00",
            "07:00",
            "UTC",
            out var selection,
            out var error);

        Assert.True(valid);
        Assert.Empty(error);
        Assert.Equal(
            new TrayActivationWindowSelection("22:00-07:00", "UTC", IsOvernight: true),
            selection);
        Assert.Equal(
            [
                "update-policy",
                "--activation-window",
                "22:00-07:00",
                "--time-zone",
                "UTC",
            ],
            TrayActivationWindowPlanner.BuildArguments(selection!));
    }

    [Theory]
    [InlineData("07:00", "07:00", "Start and end must be different.")]
    [InlineData("7:00", "08:00", "Enter both times as HH:mm.")]
    [InlineData("07:00", "08:00", "The selected time zone is not available on this computer.")]
    public void CustomActivationWindowPlannerRejectsUnsafeOrAmbiguousInput(
        string start,
        string end,
        string expectedError)
    {
        var valid = TrayActivationWindowPlanner.TryCreate(
            start,
            end,
            start == "07:00" && end == "08:00" ? "not-a-time-zone" : "UTC",
            out var selection,
            out var error);

        Assert.False(valid);
        Assert.Null(selection);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void CustomActivationWindowRangeValidationIsFailClosed()
    {
        Assert.False(TrayActivationWindowPlanner.TryCreateRange(
            "22:00-07:00-extra",
            "UTC",
            out var selection,
            out _));
        Assert.Null(selection);
    }

    [Fact]
    public void CancelledCustomActivationWindowHasNoSelectionOrMutation()
    {
        var selection = new TrayActivationWindowSelection("22:00-07:00", "UTC", true);

        Assert.Null(TrayActivationWindowDialog.AcceptedSelection(
            DialogResult.Cancel,
            selection));
    }

    [Fact]
    public void ActivityPlannerRecordsMeaningfulTransitionsOnly()
    {
        var previous = new TrayNotificationSnapshot(
            ContinuityHealth.Healthy,
            "observed",
            "0.5.0",
            "waiting",
            "0.6.0",
            null);
        var staged = previous with
        {
            LatestState = "staged",
            LatestVersion = "0.6.0",
        };

        var activity = TrayActivityPlanner.Plan(previous, staged);

        Assert.Equal(
            new TrayActivityEvent(
                TrayActivityKind.Staged,
                "0.6.0",
                "staged",
                TrayActivityCatalog.StagedSummary),
            activity);
        Assert.Null(TrayActivityPlanner.Plan(staged, staged));
        Assert.Null(TrayActivityPlanner.Plan(
            previous,
            previous with { ApplyState = "waiting" }));
    }

    [Fact]
    public void ActivityHistoryDeduplicatesAndRendersAllowlistedEntries()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"continuity-tray-history-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(root, "tray-activity-history.json");
        try
        {
            var store = new TrayActivityHistoryStore(historyPath);
            var activity = new TrayActivityEvent(
                TrayActivityKind.Staged,
                "0.6.0",
                "staged",
                TrayActivityCatalog.StagedSummary);
            var occurredAt = new DateTimeOffset(
                2026,
                8,
                24,
                20,
                0,
                0,
                TimeSpan.Zero);

            Assert.True(store.TryAppend(activity, occurredAt));
            Assert.False(store.TryAppend(activity, occurredAt.AddMinutes(1)));
            var entries = store.Load();
            var rendered = TrayActivityHistoryFormatter.Render(entries);

            Assert.Single(entries);
            Assert.Contains("2026-08-24 20:00 UTC", rendered);
            Assert.Contains("v0.6.0", rendered);
            Assert.DoesNotContain("C:\\", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("thread", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public void ActivityHistoryKeepsRepeatedSuccessfulUserMutations()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"continuity-tray-history-mutations-{Guid.NewGuid():N}");
        try
        {
            var store = new TrayActivityHistoryStore(
                Path.Combine(root, "tray-activity-history.json"));
            var mutation = new TrayActivityEvent(
                TrayActivityKind.UserMutationSucceeded,
                null,
                "succeeded",
                TrayActivityCatalog.SnoozeSummary,
                Deduplicate: false);

            Assert.True(store.TryAppend(mutation, DateTimeOffset.UtcNow));
            Assert.True(store.TryAppend(mutation, DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.Equal(2, store.Load().Count);
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public void ActivityHistoryCapsEntriesAndBytesWithAtomicReplacement()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"continuity-tray-history-cap-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(root, "tray-activity-history.json");
        try
        {
            var store = new TrayActivityHistoryStore(historyPath);
            for (var index = 0; index < TrayActivityHistoryStore.MaximumEntries + 8; index++)
            {
                Assert.True(store.TryAppend(
                    new TrayActivityEvent(
                        TrayActivityKind.Staged,
                        $"1.0.{index}",
                        "staged",
                        TrayActivityCatalog.StagedSummary),
                    DateTimeOffset.UtcNow.AddMinutes(index)));
            }

            var entries = store.Load();

            Assert.Equal(TrayActivityHistoryStore.MaximumEntries, entries.Count);
            Assert.True(new FileInfo(historyPath).Length <= TrayActivityHistoryStore.MaximumBytes);
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root),
                path => path.EndsWith(".tmp", StringComparison.Ordinal));
            Assert.Equal(
                $"1.0.{TrayActivityHistoryStore.MaximumEntries + 7}",
                entries[0].Version);
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public void ActivityHistoryFailsClosedForInvalidOrNewerSchema()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"continuity-tray-history-invalid-{Guid.NewGuid():N}");
        var historyPath = Path.Combine(root, "tray-activity-history.json");
        Directory.CreateDirectory(root);
        try
        {
            foreach (var json in new[]
            {
                """{"schemaVersion":2,"entries":[]}""",
                """{"schemaVersion":1,"entries":[{"kind":"failure","occurredAtUtc":"2026-08-24T20:00:00Z","version":"9.9.9","state":"failed","summary":"C:\\secret\\error"}]}""",
                new string('x', TrayActivityHistoryStore.MaximumBytes + 1),
            })
            {
                File.WriteAllText(historyPath, json);
                var store = new TrayActivityHistoryStore(historyPath);

                Assert.Empty(store.Load());
                Assert.False(store.TryAppend(
                    new TrayActivityEvent(
                        TrayActivityKind.Failure,
                        "0.6.0",
                        "failed",
                        TrayActivityCatalog.FailureSummary),
                    DateTimeOffset.UtcNow));
            }
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public void ActivityHistoryRejectsArbitrarySummaryAndExceptionText()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"continuity-tray-history-redaction-{Guid.NewGuid():N}");
        try
        {
            var store = new TrayActivityHistoryStore(
                Path.Combine(root, "tray-activity-history.json"));

            Assert.False(store.TryAppend(
                new TrayActivityEvent(
                    TrayActivityKind.Failure,
                    "0.6.0",
                    "failed",
                    @"failed C:\Users\Yeste\state\thread-secret"),
                DateTimeOffset.UtcNow));
            Assert.False(store.TryAppend(
                new TrayActivityEvent(
                    TrayActivityKind.Failure,
                    "not-a-public-version",
                    "failed",
                    TrayActivityCatalog.FailureSummary),
                DateTimeOffset.UtcNow));
            Assert.Empty(store.Load());
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void MenuControllerResetsAfterExternalClosedBeforeRepeatedToggleAndEscapeClose()
    {
        var controller = new TrayMenuController();
        var shown = 0;
        var closed = 0;

        controller.Toggle(() => shown++, () => closed++);
        Assert.True(controller.IsVisible);
        controller.MarkClosed();
        Assert.False(controller.IsVisible);

        controller.Toggle(() => shown++, () => closed++);
        controller.Toggle(() => shown++, () => closed++);
        controller.Toggle(() => shown++, () => closed++);
        controller.Close(() => closed++);

        Assert.Equal(3, shown);
        Assert.Equal(2, closed);
        Assert.False(controller.IsVisible);
    }

    [Fact]
    public void MissingApplyStateDefaultsToExplicitStagedOnlyOptOut()
    {
        var apply = TrayStatusParser.ParseApply(policyJson: null, statusJson: null);

        Assert.Equal(ContinuityApplySnapshot.Default, apply);
        Assert.Equal(
            "Activation: staged only; automatic apply is off",
            TrayStatusPresentation.ApplyDetail(apply));
        Assert.False(TrayStatusPresentation.ShowApplyRetry(apply));
    }

    [Fact]
    public void ParsesSchemaV2SnoozeAndActivationWindowWithoutConfusingActivationState()
    {
        var now = new DateTimeOffset(2026, 8, 24, 20, 0, 0, TimeSpan.Zero);
        var policy =
            """
            {
              "schemaVersion": 2,
              "automaticApplyWhenIdle": true,
              "generation": 8,
              "updatedAtUtc": "2026-08-24T19:00:00Z",
              "snoozedUntilUtc": "2026-08-24T21:00:00Z",
              "activationWindow": {
                "startMinuteLocal": 1320,
                "endMinuteLocal": 420,
                "timeZoneId": "UTC"
              }
            }
            """;
        var status =
            """{"schemaVersion":1,"state":"waiting","policyGeneration":8,"targetVersion":"0.6.0"}""";

        var apply = TrayStatusParser.ParseApply(policy, status);

        Assert.True(apply.ControlsAvailable);
        Assert.Equal(2, apply.PolicySchemaVersion);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 24, 21, 0, 0, TimeSpan.Zero),
            apply.SnoozedUntilUtc);
        Assert.Equal(new TrayActivationWindow(1320, 420, "UTC"), apply.ActivationWindow);
        Assert.Equal(
            "Activation schedule: activation snoozed until 2026-08-24 21:00 UTC; " +
            "window 22:00-07:00 (UTC)",
            TrayStatusPresentation.ActivationScheduleDetail(apply, now));
        Assert.Contains(
            "snoozed until 2026-08-24 21:00 UTC",
            TrayStatusPresentation.ApplyDetail(apply, now));
    }

    [Fact]
    public void InvalidSchemaV2ScheduleDisablesPolicyActions()
    {
        var policy =
            """{"schemaVersion":2,"automaticApplyWhenIdle":true,"generation":8,"activationWindow":{"startMinuteLocal":10,"endMinuteLocal":10,"timeZoneId":"UTC"}}""";

        var apply = TrayStatusParser.ParseApply(policy, statusJson: null);

        Assert.False(apply.ControlsAvailable);
        Assert.Contains("invalid activation window", apply.AvailabilityError);
        Assert.False(TrayStatusPresentation.CanChangeApplyPolicy(apply));
    }

    [Theory]
    [MemberData(nameof(ApplyDetails))]
    public void PresentsEveryApplyState(
        string state,
        string? targetVersion,
        string? idleSince,
        string? error,
        string expected,
        bool retry)
    {
        var policy = """
            {
              "schemaVersion": 1,
              "automaticApplyWhenIdle": true,
              "generation": 7,
              "updatedAtUtc": "2026-08-24T20:00:00Z"
            }
            """;
        var status = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            state,
            policyGeneration = 7,
            targetVersion,
            updatedAtUtc = "2026-08-24T20:00:00Z",
            idleSinceUtc = idleSince,
            lastError = error,
        });

        var apply = TrayStatusParser.ParseApply(policy, status);

        Assert.True(apply.AutomaticApplyWhenIdle);
        Assert.Equal(7, apply.PolicyGeneration);
        Assert.Equal(expected, TrayStatusPresentation.ApplyDetail(apply));
        Assert.Equal(retry, TrayStatusPresentation.ShowApplyRetry(apply));
        Assert.Equal(state != "applying", TrayStatusPresentation.CanChangeApplyPolicy(apply));
    }

    public static TheoryData<string, string?, string?, string?, string, bool> ApplyDetails => new()
    {
        { "stagedOnly", null, null, null,
            "Activation: automatic apply enabled; awaiting supervisor status", false },
        { "waiting", null, null, null,
            "Activation: waiting for a verified staged update", false },
        { "waiting", "0.5.0", null, null,
            "Activation: v0.5.0 waiting for a safe idle window", false },
        { "waiting", "0.5.0", "2026-08-24T19:59:50Z", null,
            "Activation: v0.5.0 proving a stable idle window", false },
        { "applying", "0.5.0", null, null,
            "Activation: handing off to v0.5.0; Codex stays open", false },
        { "active", "0.5.0", null, null,
            "Activation: v0.5.0 verified active", false },
        { "rolledBack", "0.5.0", null, "proof failed",
            "Activation: v0.5.0 rolled back safely: proof failed", true },
        { "failed", "0.5.0", null, "launch failed",
            "Activation failed for v0.5.0: launch failed", true },
    };

    [Theory]
    [InlineData("{}", null, "Automatic-apply policy")]
    [InlineData("{\"schemaVersion\":99}", null, "Automatic-apply policy")]
    [InlineData(null, "{}", "Activation status")]
    [InlineData(null, "{\"schemaVersion\":99}", "Activation status")]
    [InlineData(null, "{\"schemaVersion\":1,\"state\":\"future\"}", "Activation status")]
    public void InvalidApplyStateDisablesControls(
        string? policy,
        string? status,
        string expectedError)
    {
        var apply = TrayStatusParser.ParseApply(policy, status);

        Assert.False(apply.ControlsAvailable);
        Assert.Contains(expectedError, apply.AvailabilityError);
        Assert.False(TrayStatusPresentation.CanChangeApplyPolicy(apply));
        Assert.StartsWith(
            "Activation controls unavailable:",
            TrayStatusPresentation.ApplyDetail(apply));
    }

    [Fact]
    public void NewPolicyGenerationDoesNotResurfacePriorFailureAsRetryable()
    {
        const string policy =
            """{"schemaVersion":1,"automaticApplyWhenIdle":true,"generation":8}""";
        const string priorFailure =
            """{"schemaVersion":1,"state":"failed","policyGeneration":7,"targetVersion":"0.5.0","lastError":"old failure"}""";

        var apply = TrayStatusParser.ParseApply(policy, priorFailure);

        Assert.True(apply.AutomaticApplyWhenIdle);
        Assert.Equal(8, apply.PolicyGeneration);
        Assert.Equal("waiting", apply.State);
        Assert.Null(apply.LastError);
        Assert.False(TrayStatusPresentation.ShowApplyRetry(apply));
    }

    [Fact]
    public void InvalidStatusPreservesKnownEnabledPolicyWhileDisablingMutation()
    {
        const string policy =
            """{"schemaVersion":1,"automaticApplyWhenIdle":true,"generation":8}""";

        var apply = TrayStatusParser.ParseApply(policy, "{}");

        Assert.True(apply.AutomaticApplyWhenIdle);
        Assert.False(apply.ControlsAvailable);
        Assert.False(TrayStatusPresentation.CanChangeApplyPolicy(apply));
    }

    [Theory]
    [MemberData(nameof(UpdateDetails))]
    public void PresentsEveryUpdateState(string state, bool running, bool live, string? error, string expected)
    {
        var update = new ContinuityUpdateSnapshot(
            "0.2.1", running, "0.3.0", 2, 1, 1, state, error);

        Assert.Equal(expected, TrayStatusPresentation.UpdateDetail(
            update, live ? ContinuityHealth.Healthy : ContinuityHealth.Unavailable));
    }

    public static TheoryData<string, bool, bool, string?, string> UpdateDetails => new()
    {
        { "active", true, true, null, "Running v0.2.1; latest is active" },
        { "staged", true, true, null, "Running v0.2.1; v0.3.0 staged" },
        { "staged", false, true, null, "Last ran v0.2.1; v0.3.0 staged" },
        { "deferred", true, true, null, "Running v0.2.1; v0.3.0 deferred by rollback" },
        { "inactive", false, true, null, "Last ran v0.2.1; latest v0.3.0 is not active" },
        { "ahead", true, true, null, "Running v0.2.1; ahead of stable v0.3.0" },
        { "failed", true, true, null, "Running v0.2.1; v0.3.0 could not be staged" },
        { "observed", true, true, null, "Running v0.2.1; v0.3.0 observed; staging pending" },
        { "unknown", true, true, null, "Running v0.2.1; update state unknown" },
        { "future", true, true, null, "Running v0.2.1; update state future" },
        { "failed", true, false, "checksum mismatch", "Last ran v0.2.1; latest v0.3.0; last check failed: checksum mismatch" },
    };

    [Fact]
    public void CompactsMultilineUpdateErrors() => Assert.Equal(
        $"Last ran v0.2.1; latest v0.3.0; last check failed: first  {new string('x', 153)}\u2026",
        TrayStatusPresentation.UpdateDetail(new(
            "0.2.1", true, "0.3.0", 0, 0, 0, "failed", "first\r\n" + new string('x', 170)),
            ContinuityHealth.Unavailable));

    [Theory]
    [InlineData("stderr", "stdout", 2, "Update check failed: stderr")]
    [InlineData("", "stdout", 2, "Update check failed: stdout")]
    [InlineData("", "", 2, "Update check failed: exit code 2")]
    public void CommandFailureUsesBoundedUsefulDetail(
        string error, string output, int exitCode, string expected) => Assert.Equal(
            expected,
            TrayStatusPresentation.CommandFailure(
                "Update check", new TrayCommandResult(exitCode, output, error)));

    [Fact]
    public async Task MutationPresenterContainsLauncherFailureAndRestoresActions()
    {
        var enabledStates = new List<bool>();
        var feedback = string.Empty;
        await new TrayMutationPresenter().RunAsync(
            "Checking\u2026",
            "Update check",
            _ => throw new Win32Exception("launch failed"),
            CancellationToken.None,
            enabledStates.Add,
            text => feedback = text,
            () => throw new InvalidOperationException("Refresh must not run."));
        Assert.Equal([false, true], enabledStates);
        Assert.Equal("Update check failed: launch failed", feedback);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"observedCount\":\"many\"}")]
    public void InvalidUpdateShapeIsUnavailable(string json) => Assert.Equal(
        ContinuityUpdateSnapshot.Unavailable("Update status is invalid."),
        TrayStatusParser.ParseUpdate(json));

    [Fact]
    public void StatusAndDiagnosticsResolutionUseOwningPaths()
    {
        static string SupervisorStatus(int processId, DateTimeOffset updatedAt) =>
            $$"""{"supervisorProcessId":{{processId}},"updatedAtUtc":"{{updatedAt:O}}"}""";
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-routing-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var stableExecutable = Path.Combine(stateDirectory, "bin", "CodexContinuity.exe");
        var installStatePath = Path.Combine(stateDirectory, "install-state.json");
        var legacyDirectory = Path.Combine(root, "legacy");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stableExecutable)!);
            File.WriteAllText(stableExecutable, "fixture");
            File.WriteAllText(installStatePath, "{}");

            Assert.Equal(
                stableExecutable,
                TrayStatusClient.ResolveSupervisorExecutable(applicationDirectory, stateDirectory));
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "update-status.json"), "{}");
            var statusPath = Path.Combine(legacyDirectory, "supervisor-status.json");
            using var currentProcess = Process.GetCurrentProcess();
            File.WriteAllText(statusPath, SupervisorStatus(Environment.ProcessId,
                new DateTimeOffset(currentProcess.StartTime).AddMinutes(-1)));
            Assert.Equal(stateDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
            File.WriteAllText(statusPath, SupervisorStatus(int.MaxValue, DateTimeOffset.UtcNow));
            Assert.Equal(stateDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
            File.WriteAllText(statusPath, SupervisorStatus(Environment.ProcessId, DateTimeOffset.UtcNow));
            File.WriteAllText(Path.Combine(stateDirectory, "supervisor-status.json"), "{}");
            Assert.Equal(legacyDirectory, TrayStatusClient.ResolveDiagnosticsDirectory(stateDirectory, legacyDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplyStatusReadsFromInjectedOwningStateDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-apply-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        Directory.CreateDirectory(applicationDirectory);
        Directory.CreateDirectory(stateDirectory);
        try
        {
            File.WriteAllText(
                Path.Combine(stateDirectory, "update-apply-policy.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    automaticApplyWhenIdle = true,
                    generation = 4,
                    updatedAtUtc = DateTimeOffset.UtcNow,
                }));
            File.WriteAllText(
                Path.Combine(stateDirectory, "update-apply-status.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    state = "applying",
                    policyGeneration = 4,
                    targetVersion = "0.5.0",
                    updatedAtUtc = DateTimeOffset.UtcNow,
                }));
            var client = new TrayStatusClient(
                "status.exe",
                applicationDirectory,
                stateDirectory,
                legacyDirectory);

            var apply = await client.ReadApplyAsync(CancellationToken.None);

            Assert.True(apply.AutomaticApplyWhenIdle);
            Assert.Equal("applying", apply.State);
            Assert.Equal("0.5.0", apply.TargetVersion);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void MutationResolutionUsesVersionedCommandsAndFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-mutation-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        var bundledExecutable = Path.Combine(applicationDirectory, "CodexContinuity.exe");
        var stableExecutable = Path.Combine(stateDirectory, "bin", "CodexContinuity.exe");
        var installedExecutable = Path.Combine(stateDirectory, "versions", "v1", "CodexContinuity.exe");
        var statePath = Path.Combine(stateDirectory, "install-state.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stableExecutable)!);
            Directory.CreateDirectory(Path.GetDirectoryName(installedExecutable)!);
            Directory.CreateDirectory(applicationDirectory);
            File.WriteAllText(bundledExecutable, "bundled");
            File.WriteAllText(stableExecutable, "stable");
            File.WriteAllText(installedExecutable, "versioned");
            WriteInstallState(statePath, installedExecutable, "AA", lifecycle: 0);

            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(installedExecutable),
                    Path.GetFullPath(installedExecutable),
                    "AA",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            File.Delete(installedExecutable);
            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(bundledExecutable),
                    Path.GetFullPath(installedExecutable),
                    "AA",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            WriteInstallState(statePath, stableExecutable, "BB", lifecycle: 0);
            Assert.Equal(
                new TrayMutationTarget(
                    Path.GetFullPath(bundledExecutable),
                    Path.GetFullPath(stableExecutable),
                    "BB",
                    null),
                TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory));

            WriteInstallState(statePath, stableExecutable, "BB", lifecycle: 1);
            var deferred = TrayStatusClient.ResolveMutationTarget(
                applicationDirectory, stateDirectory, legacyDirectory);
            Assert.False(deferred.Available);
            Assert.Contains("deferred uninstall", deferred.Error);

            foreach (var invalidState in new[] { "not json", """{"lifecycle":9}""" })
            {
                File.WriteAllText(statePath, invalidState);
                var invalid = TrayStatusClient.ResolveMutationTarget(
                    applicationDirectory, stateDirectory, legacyDirectory);
                Assert.False(invalid.Available);
                Assert.Contains("state is invalid", invalid.Error);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MutationActionsUseOneGateAndExactVersionedCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-actions-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        var installedExecutable = Path.Combine(stateDirectory, "versions", "v1", "CodexContinuity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedExecutable)!);
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(installedExecutable, "versioned");
        WriteInstallState(
            Path.Combine(stateDirectory, "install-state.json"),
            installedExecutable,
            "ABC123",
            lifecycle: 0);
        var calls = new List<string>();
        var firstEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<TrayCommandResult> RunProcess(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken _)
        {
            calls.Add($"{executable}|{string.Join('|', arguments)}");
            if (arguments[0] == "update")
            {
                firstEntered.SetResult(true);
                await releaseFirst.Task;
            }
            else
            {
                secondEntered.TrySetResult(true);
            }
            return new(0, string.Empty, string.Empty);
        }

        try
        {
            var client = new TrayStatusClient(
                "read-only-status.exe",
                applicationDirectory,
                stateDirectory,
                legacyDirectory,
                RunProcess);
            var update = client.CheckForUpdatesAsync(CancellationToken.None);
            await firstEntered.Task;
            var recovery = client.RestartSupervisorAsync(CancellationToken.None);

            Assert.False(secondEntered.Task.IsCompleted);
            releaseFirst.SetResult(true);
            await Task.WhenAll(update, recovery);
            await client.SetAutomaticApplyAsync(enabled: true, CancellationToken.None);
            await client.SetAutomaticApplyAsync(enabled: false, CancellationToken.None);
            await client.SetSnoozeAsync(minutes: 60, CancellationToken.None);
            await client.ClearSnoozeAsync(CancellationToken.None);
            await client.SetDefaultActivationWindowAsync(CancellationToken.None);
            await client.SetActivationWindowAsync(
                "22:00-07:00",
                "UTC",
                CancellationToken.None);
            await client.ClearActivationWindowAsync(CancellationToken.None);
            Assert.Equal(
                [
                    $"{Path.GetFullPath(installedExecutable)}|update",
                    $"{Path.GetFullPath(installedExecutable)}|repair|--start-now|" +
                        $"--expected-installed-executable|{Path.GetFullPath(installedExecutable)}|" +
                        "--expected-installed-sha256|ABC123",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--enable",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--disable",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--snooze-minutes|60",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--clear-snooze",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--activation-window|22:00-07:00",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--activation-window|22:00-07:00|" +
                        "--time-zone|UTC",
                    $"{Path.GetFullPath(installedExecutable)}|update-policy|--clear-activation-window",
                ],
                calls);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RollbackActionPinsExpectedSelectionAndDigest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"continuity-tray-rollback-{Guid.NewGuid():N}");
        var applicationDirectory = Path.Combine(root, "tray");
        var stateDirectory = Path.Combine(root, "state");
        var legacyDirectory = Path.Combine(root, "legacy");
        var installedExecutable = Path.Combine(stateDirectory, "versions", "v2", "CodexContinuity.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(installedExecutable)!);
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(installedExecutable, "selected");
        WriteInstallState(
            Path.Combine(stateDirectory, "install-state.json"),
            installedExecutable,
            "ABC123",
            lifecycle: 0);
        var calls = new List<string>();
        try
        {
            var client = new TrayStatusClient(
                "status.exe",
                applicationDirectory,
                stateDirectory,
                legacyDirectory,
                (executable, arguments, _) =>
                {
                    calls.Add($"{executable}|{string.Join('|', arguments)}");
                    return Task.FromResult(new TrayCommandResult(0, string.Empty, string.Empty));
                });

            await client.RollbackAsync(CancellationToken.None);

            Assert.Equal(
                $"{Path.GetFullPath(installedExecutable)}|rollback|" +
                $"--expected-installed-executable|{Path.GetFullPath(installedExecutable)}|" +
                "--expected-installed-sha256|ABC123",
                Assert.Single(calls));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MutationGateReleasesAfterFaultAndSkipsCanceledQueue()
    {
        var gate = new TrayCommandGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync<int>(
            () => throw new InvalidOperationException("fixture"),
            CancellationToken.None));
        Assert.Equal(1, await gate.RunAsync(() => Task.FromResult(1), CancellationToken.None));

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = gate.RunAsync(async () =>
        {
            await release.Task;
            return 1;
        }, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var entered = false;
        var queued = gate.RunAsync(() =>
        {
            entered = true;
            return Task.FromResult(2);
        }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(entered);
        release.SetResult(true);
        Assert.Equal(1, await held);
    }

    [Fact]
    public async Task CancelingTrayProcessKillsTheChildProcessTree()
    {
        var recordPath = Path.GetTempFileName();
        File.Delete(recordPath);
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var commandTask = TrayStatusClient.RunProcessAsync(
            powershell,
            [
                "-NoProfile",
                "-Command",
                $"$child = Start-Process -FilePath '{powershell}' -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 15' -WindowStyle Hidden -PassThru; Set-Content -LiteralPath '{recordPath}' -Value $child.Id; Wait-Process -Id $child.Id",
            ],
            cancellation.Token);
        try
        {
            while (!File.Exists(recordPath) && !commandTask.IsCompleted)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
            var processId = int.Parse(await File.ReadAllTextAsync(recordPath));

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commandTask);

            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        }
        finally
        {
            File.Delete(recordPath);
        }
    }

    private static void WriteInstallState(
        string path,
        string installedExecutable,
        string binarySha256,
        int lifecycle) => File.WriteAllText(
            path,
            JsonSerializer.Serialize(new { installedExecutable, binarySha256, lifecycle }));
}
