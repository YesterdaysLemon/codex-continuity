using System.Collections;
using System.Diagnostics;

namespace CodexContinuity;

internal static class FutureProcessEnvironment
{
    private static readonly string[] DerivedProcessVariables =
    [
        "ALLUSERSPROFILE",
        "APPDATA",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "COMPUTERNAME",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "LOGONSERVER",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "PUBLIC",
        "SystemDrive",
        "SystemRoot",
        "USERDOMAIN",
        "USERDOMAIN_ROAMINGPROFILE",
        "USERNAME",
        "USERPROFILE",
    ];

    internal static IReadOnlyDictionary<string, string> Snapshot()
    {
        var snapshot = Merge(
            Enumerate(EnvironmentVariableTarget.Machine),
            Enumerate(EnvironmentVariableTarget.User));
        foreach (var name in DerivedProcessVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!snapshot.ContainsKey(name) && !string.IsNullOrWhiteSpace(value))
            {
                snapshot[name] = value;
            }
        }
        return snapshot;
    }

    internal static Dictionary<string, string> Merge(
        IEnumerable<KeyValuePair<string, string>> machine,
        IEnumerable<KeyValuePair<string, string>> user)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in machine)
        {
            merged[entry.Key] = entry.Value;
        }
        foreach (var entry in user)
        {
            if (entry.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase) &&
                merged.TryGetValue(entry.Key, out var machinePath) &&
                !string.IsNullOrWhiteSpace(machinePath) &&
                !string.IsNullOrWhiteSpace(entry.Value))
            {
                merged[entry.Key] = $"{machinePath}{Path.PathSeparator}{entry.Value}";
            }
            else
            {
                merged[entry.Key] = entry.Value;
            }
        }
        return merged;
    }

    internal static void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var entry in Snapshot())
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        startInfo.Environment.Remove(InstallCoordinator.AppServerUrlVariable);
        startInfo.Environment.Remove(InstallCoordinator.DisableUpdaterVariable);
    }

    internal static string ResolveCodexHome()
    {
        var environment = Snapshot();
        if (environment.TryGetValue("CODEX_HOME", out var configuredHome) &&
            !string.IsNullOrWhiteSpace(configuredHome))
        {
            return Path.GetFullPath(configuredHome);
        }
        if (environment.TryGetValue("USERPROFILE", out var userProfile) &&
            !string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, ".codex");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    private static IEnumerable<KeyValuePair<string, string>> Enumerate(
        EnvironmentVariableTarget target)
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(target))
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }
}
