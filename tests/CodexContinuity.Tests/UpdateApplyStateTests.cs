using System.Text.Json;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class UpdateApplyStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 13, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"codex-continuity-update-apply-tests-{Guid.NewGuid():N}");

    public UpdateApplyStateTests() => Directory.CreateDirectory(root);

    [Fact]
    public void AutomaticApplyDefaultsOffAndEveryChoiceAdvancesGeneration()
    {
        var initial = ContinuityUpdateApplyPolicy.Default(Now);

        Assert.False(initial.AutomaticApplyWhenIdle);
        Assert.Equal(0, initial.Generation);
        Assert.Null(initial.SnoozedUntilUtc);
        Assert.Null(initial.ActivationWindow);
        var enabled = initial.WithAutomaticApply(true, Now + TimeSpan.FromSeconds(1));
        var reaffirmed = enabled.WithAutomaticApply(true, Now + TimeSpan.FromSeconds(2));
        var disabled = reaffirmed.WithAutomaticApply(false, Now + TimeSpan.FromSeconds(3));

        Assert.True(enabled.AutomaticApplyWhenIdle);
        Assert.Equal(1, enabled.Generation);
        Assert.Equal(2, reaffirmed.Generation);
        Assert.False(disabled.AutomaticApplyWhenIdle);
        Assert.Equal(3, disabled.Generation);
    }

    [Fact]
    public void PolicyStoreMigratesLegacyPolicyWithoutInventingRestrictions()
    {
        var path = ContinuityPaths.UpdateApplyPolicyFile(root);
        File.WriteAllText(
            path,
            $$"""{"schemaVersion":1,"automaticApplyWhenIdle":true,"generation":7,"updatedAtUtc":"{{Now:O}}"}""");

        var loaded = new ContinuityUpdateApplyPolicyStore(path).Load();

        Assert.Equal(ContinuityUpdateApplyLoadKind.Loaded, loaded.Kind);
        Assert.Equal(ContinuityUpdateApplyPolicy.CurrentSchemaVersion, loaded.Policy!.SchemaVersion);
        Assert.True(loaded.Policy.AutomaticApplyWhenIdle);
        Assert.Equal(7, loaded.Policy.Generation);
        Assert.Null(loaded.Policy.SnoozedUntilUtc);
        Assert.Null(loaded.Policy.ActivationWindow);
    }

    [Fact]
    public void PolicyStoreRoundTripsBoundedSnoozeAndActivationWindow()
    {
        var store = new ContinuityUpdateApplyPolicyStore(
            ContinuityPaths.UpdateApplyPolicyFile(root));
        var policy = ContinuityUpdateApplyPolicy.Default(Now)
            .WithAutomaticApply(true, Now)
            .WithSnooze(Now + TimeSpan.FromHours(2), Now)
            .WithActivationWindow(new(23 * 60, 7 * 60, "UTC"), Now);

        store.Save(policy);

        Assert.Equal(policy, store.Load().Policy);
        Assert.Throws<ArgumentException>(() => store.Save(policy with
        {
            ActivationWindow = new(0, 0, "UTC"),
        }));
        Assert.Throws<ArgumentException>(() => store.Save(policy with
        {
            ActivationWindow = new(0, 60, "missing/time-zone"),
        }));
        Assert.Throws<ArgumentException>(() => store.Save(policy with
        {
            SnoozedUntilUtc = policy.UpdatedAtUtc + TimeSpan.FromDays(8),
        }));
    }

    [Fact]
    public void PolicyStoreRoundTripsAndSeparatesMissingInvalidAndUnsupported()
    {
        var path = ContinuityPaths.UpdateApplyPolicyFile(root);
        var store = new ContinuityUpdateApplyPolicyStore(path);
        Assert.Equal(ContinuityUpdateApplyLoadKind.Missing, store.Load().Kind);

        var policy = ContinuityUpdateApplyPolicy.Default(Now).WithAutomaticApply(true, Now);
        store.Save(policy);
        Assert.Equal(
            new ContinuityUpdateApplyPolicyLoadResult(
                ContinuityUpdateApplyLoadKind.Loaded,
                policy),
            store.Load());

        File.WriteAllText(path, "{}");
        Assert.Equal(ContinuityUpdateApplyLoadKind.Invalid, store.Load().Kind);
        File.WriteAllText(path, "{\"schemaVersion\":99}");
        Assert.Equal(ContinuityUpdateApplyLoadKind.UnsupportedSchema, store.Load().Kind);
    }

    [Fact]
    public void PolicyCommandInspectsAndMutatesOnlyExplicitly()
    {
        var firstOutput = new StringWriter();
        Assert.Equal(0, Program.UpdatePolicy(["update-policy"], root, () => Now, firstOutput));
        Assert.False(File.Exists(ContinuityPaths.UpdateApplyPolicyFile(root)));
        Assert.False(ReadPolicy(firstOutput).AutomaticApplyWhenIdle);

        var enableOutput = new StringWriter();
        Assert.Equal(0, Program.UpdatePolicy(
            ["update-policy", "--enable"],
            root,
            () => Now + TimeSpan.FromSeconds(1),
            enableOutput));
        var enabled = ReadPolicy(enableOutput);
        Assert.True(enabled.AutomaticApplyWhenIdle);
        Assert.Equal(1, enabled.Generation);

        var retryOutput = new StringWriter();
        Program.UpdatePolicy(
            ["update-policy", "--enable"],
            root,
            () => Now + TimeSpan.FromSeconds(2),
            retryOutput);
        Assert.Equal(2, ReadPolicy(retryOutput).Generation);

        var disableOutput = new StringWriter();
        Program.UpdatePolicy(
            ["update-policy", "--disable"],
            root,
            () => Now + TimeSpan.FromSeconds(3),
            disableOutput);
        Assert.False(ReadPolicy(disableOutput).AutomaticApplyWhenIdle);
        Assert.Throws<ArgumentException>(() => Program.UpdatePolicy(
            ["update-policy", "--enable", "--disable"],
            root,
            () => Now,
            TextWriter.Null));
    }

    [Fact]
    public void PolicyCommandConfiguresAndClearsSnoozeAndActivationWindow()
    {
        var configuredOutput = new StringWriter();
        Assert.Equal(0, Program.UpdatePolicy(
            [
                "update-policy",
                "--enable",
                "--snooze-minutes", "90",
                "--activation-window", "23:00-07:00",
                "--time-zone", "UTC",
            ],
            root,
            () => Now,
            configuredOutput));
        var configured = ReadPolicy(configuredOutput);
        Assert.True(configured.AutomaticApplyWhenIdle);
        Assert.Equal(Now + TimeSpan.FromMinutes(90), configured.SnoozedUntilUtc);
        Assert.Equal(new ContinuityActivationWindow(23 * 60, 7 * 60, "UTC"),
            configured.ActivationWindow);

        var clearedOutput = new StringWriter();
        Program.UpdatePolicy(
            ["update-policy", "--clear-snooze", "--clear-activation-window"],
            root,
            () => Now + TimeSpan.FromSeconds(1),
            clearedOutput);
        var cleared = ReadPolicy(clearedOutput);
        Assert.Null(cleared.SnoozedUntilUtc);
        Assert.Null(cleared.ActivationWindow);
    }

    [Theory]
    [InlineData("--snooze-minutes", "0")]
    [InlineData("--snooze-minutes", "10081")]
    [InlineData("--activation-window", "07:00-07:00")]
    [InlineData("--activation-window", "tomorrow")]
    [InlineData("--time-zone", "UTC")]
    [InlineData("--unknown", "value")]
    public void PolicyCommandRejectsUnsafeOrUnknownOptions(string option, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => Program.UpdatePolicy(
            ["update-policy", option, value],
            root,
            () => Now,
            TextWriter.Null));

    [Theory]
    [InlineData(ContinuityUpdateApplyStates.StagedOnly)]
    [InlineData(ContinuityUpdateApplyStates.Waiting)]
    [InlineData(ContinuityUpdateApplyStates.Applying)]
    [InlineData(ContinuityUpdateApplyStates.Active)]
    [InlineData(ContinuityUpdateApplyStates.RolledBack)]
    [InlineData(ContinuityUpdateApplyStates.Failed)]
    public void StatusStoreRoundTripsEveryBoundedState(string state)
    {
        var store = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root));
        var expected = Status(state);

        store.Save(expected);

        Assert.Equal(
            new ContinuityUpdateApplyStatusLoadResult(
                ContinuityUpdateApplyLoadKind.Loaded,
                expected),
            store.Load());
    }

    [Fact]
    public void StatusStoreRejectsUnboundedOrIncoherentProof()
    {
        var store = new ContinuityUpdateApplyStatusStore(
            ContinuityPaths.UpdateApplyStatusFile(root));
        Assert.Throws<ArgumentException>(() => store.Save(Status("surprise")));
        Assert.Throws<ArgumentException>(() => store.Save(Status(
            ContinuityUpdateApplyStates.Failed) with
        {
            LastError = new string('x', 2049),
        }));
        Assert.Throws<ArgumentException>(() => store.Save(Status(
            ContinuityUpdateApplyStates.Waiting) with
        {
            IdleSinceUtc = Now + TimeSpan.FromSeconds(1),
        }));
        Assert.Throws<ArgumentException>(() => store.Save(Status(
            ContinuityUpdateApplyStates.Applying) with
        {
            HandoffId = "not-a-handoff",
        }));
    }

    private static ContinuityUpdateApplyPolicy ReadPolicy(StringWriter output) =>
        JsonSerializer.Deserialize<ContinuityUpdateApplyPolicy>(
            output.ToString(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            })!;

    private static ContinuityUpdateApplyStatus Status(string state) => new(
        ContinuityUpdateApplyStatus.CurrentSchemaVersion,
        state,
        PolicyGeneration: 2,
        TargetVersion: "0.5.0",
        TargetExecutableSha256: new string('a', 64),
        UpdatedAtUtc: Now,
        IdleSinceUtc: state == ContinuityUpdateApplyStates.Waiting
            ? Now - TimeSpan.FromSeconds(5)
            : null,
        HandoffId: state == ContinuityUpdateApplyStates.Applying
            ? Guid.NewGuid().ToString("N")
            : null,
        LastError: state is ContinuityUpdateApplyStates.Failed or
            ContinuityUpdateApplyStates.RolledBack
                ? "fixture failure"
                : null);

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
}
