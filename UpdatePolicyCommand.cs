using System.Globalization;
using System.Text.Json;

namespace CodexContinuity;

internal static class UpdatePolicyCommand
{
    private const int MaximumSnoozeMinutes = 7 * 24 * 60;

    internal static int Run(
        string[] args,
        string stateDirectory,
        Func<DateTimeOffset> utcNow,
        TextWriter output,
        JsonSerializerOptions jsonOptions)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        ArgumentNullException.ThrowIfNull(utcNow);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(jsonOptions);

        var options = Parse(args);
        var store = new ContinuityUpdateApplyPolicyStore(
            ContinuityPaths.UpdateApplyPolicyFile(stateDirectory));

        ContinuityUpdateApplyPolicy Load(DateTimeOffset nowUtc)
        {
            var loaded = store.Load();
            return loaded.Kind switch
            {
                ContinuityUpdateApplyLoadKind.Missing =>
                    ContinuityUpdateApplyPolicy.Default(nowUtc),
                ContinuityUpdateApplyLoadKind.Loaded => loaded.Policy!,
                _ => throw new InvalidDataException(
                    $"The persisted update apply policy is {loaded.Kind.ToString().ToLowerInvariant()}.")
            };
        }

        var now = utcNow();
        var policy = Load(now);
        if (options.HasMutation)
        {
            using var lifecycleLock = ContinuityLifecycleLock.Acquire(stateDirectory);
            now = utcNow();
            policy = Load(now);
            if (options.AutomaticApply is { } automaticApply)
            {
                policy = policy.WithAutomaticApply(automaticApply, now);
            }
            if (options.SnoozeMinutes is { } snoozeMinutes)
            {
                policy = policy.WithSnooze(now + TimeSpan.FromMinutes(snoozeMinutes), now);
            }
            else if (options.ClearSnooze)
            {
                policy = policy.WithSnooze(null, now);
            }
            if (options.ActivationWindow is { } activationWindow)
            {
                policy = policy.WithActivationWindow(activationWindow, now);
            }
            else if (options.ClearActivationWindow)
            {
                policy = policy.WithActivationWindow(null, now);
            }
            store.Save(policy);
        }

        output.WriteLine(JsonSerializer.Serialize(policy, jsonOptions));
        return 0;
    }

    private static UpdatePolicyOptions Parse(string[] args)
    {
        bool? automaticApply = null;
        int? snoozeMinutes = null;
        var clearSnooze = false;
        ContinuityActivationWindow? activationWindow = null;
        var clearActivationWindow = false;
        string? timeZoneId = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (index == 0 && argument.Equals("update-policy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            switch (argument.ToLowerInvariant())
            {
                case "--enable":
                    if (automaticApply is not null)
                    {
                        throw new ArgumentException("Choose either --enable or --disable once.");
                    }
                    automaticApply = true;
                    break;
                case "--disable":
                    if (automaticApply is not null)
                    {
                        throw new ArgumentException("Choose either --enable or --disable once.");
                    }
                    automaticApply = false;
                    break;
                case "--snooze-minutes":
                    if (snoozeMinutes is not null)
                    {
                        throw new ArgumentException("--snooze-minutes may be specified only once.");
                    }
                    var snoozeText = Value(args, ref index, "--snooze-minutes");
                    if (!int.TryParse(snoozeText, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var parsedSnooze) || parsedSnooze is < 1 or > MaximumSnoozeMinutes)
                    {
                        throw new ArgumentException(
                            $"--snooze-minutes must be between 1 and {MaximumSnoozeMinutes}.");
                    }
                    snoozeMinutes = parsedSnooze;
                    break;
                case "--clear-snooze":
                    if (clearSnooze)
                    {
                        throw new ArgumentException("--clear-snooze may be specified only once.");
                    }
                    clearSnooze = true;
                    break;
                case "--activation-window":
                    if (activationWindow is not null)
                    {
                        throw new ArgumentException("--activation-window may be specified only once.");
                    }
                    activationWindow = ParseWindow(
                        Value(args, ref index, "--activation-window"),
                        TimeZoneInfo.Local.Id);
                    break;
                case "--time-zone":
                    if (timeZoneId is not null)
                    {
                        throw new ArgumentException("--time-zone may be specified only once.");
                    }
                    timeZoneId = Value(args, ref index, "--time-zone");
                    break;
                case "--clear-activation-window":
                    if (clearActivationWindow)
                    {
                        throw new ArgumentException(
                            "--clear-activation-window may be specified only once.");
                    }
                    clearActivationWindow = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown update-policy option '{argument}'.");
            }
        }

        if (snoozeMinutes is not null && clearSnooze)
        {
            throw new ArgumentException(
                "Choose either --snooze-minutes or --clear-snooze, not both.");
        }
        if (activationWindow is not null && clearActivationWindow)
        {
            throw new ArgumentException(
                "Choose either --activation-window or --clear-activation-window, not both.");
        }
        if (timeZoneId is not null && activationWindow is null)
        {
            throw new ArgumentException("--time-zone requires --activation-window.");
        }
        if (activationWindow is not null && timeZoneId is not null)
        {
            activationWindow = activationWindow with { TimeZoneId = timeZoneId };
            activationWindow.Validate();
        }

        return new(
            automaticApply,
            snoozeMinutes,
            clearSnooze,
            activationWindow,
            clearActivationWindow);
    }

    private static string Value(string[] args, ref int index, string name)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }
        return args[index];
    }

    private static ContinuityActivationWindow ParseWindow(string value, string timeZoneId)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !TimeOnly.TryParseExact(
                parts[0],
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var start) ||
            !TimeOnly.TryParseExact(
                parts[1],
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var end))
        {
            throw new ArgumentException(
                "--activation-window must use local 24-hour times such as 23:00-07:00.");
        }
        var window = new ContinuityActivationWindow(
            (start.Hour * 60) + start.Minute,
            (end.Hour * 60) + end.Minute,
            timeZoneId);
        window.Validate();
        return window;
    }

    private sealed record UpdatePolicyOptions(
        bool? AutomaticApply,
        int? SnoozeMinutes,
        bool ClearSnooze,
        ContinuityActivationWindow? ActivationWindow,
        bool ClearActivationWindow)
    {
        internal bool HasMutation => AutomaticApply is not null || SnoozeMinutes is not null ||
            ClearSnooze || ActivationWindow is not null || ClearActivationWindow;
    }
}
