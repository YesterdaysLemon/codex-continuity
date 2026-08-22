using System.Diagnostics;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record AuthenticodeSignature(
    string Path,
    string Status,
    string? Thumbprint);

internal static class AuthenticodeReleaseVerifier
{
    private const string SignatureScript =
        "& { $results = @($args | ForEach-Object { " +
        "$signature = Get-AuthenticodeSignature -LiteralPath $_; " +
        "[pscustomobject]@{ path = $_; status = [string]$signature.Status; " +
        "thumbprint = $signature.SignerCertificate.Thumbprint } }); " +
        "ConvertTo-Json -InputObject $results -Compress }";

    internal static async Task VerifyMatchingPublisherAsync(
        string trustedExecutable,
        IReadOnlyList<string> candidateExecutables,
        CancellationToken cancellationToken)
    {
        var paths = new[] { trustedExecutable }.Concat(candidateExecutables).ToArray();
        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Authenticode verification requires every executable to exist.",
                    path);
            }
        }

        var signatures = await ReadSignaturesAsync(paths, cancellationToken);
        if (signatures.Count != paths.Length)
        {
            throw new InvalidDataException(
                "Authenticode verification did not return one result per executable.");
        }
        for (var index = 0; index < paths.Length; index++)
        {
            if (!string.Equals(
                    Path.GetFullPath(paths[index]),
                    Path.GetFullPath(signatures[index].Path),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Authenticode verification returned results for an unexpected executable.");
            }
        }

        VerifyMatchingPublisher(signatures);
    }

    internal static void VerifyMatchingPublisher(
        IReadOnlyList<AuthenticodeSignature> signatures)
    {
        if (signatures.Count < 2)
        {
            throw new InvalidDataException(
                "Authenticode verification requires a trusted build and at least one candidate.");
        }
        var trusted = signatures[0];
        if (!IsValid(trusted))
        {
            throw new InvalidDataException(
                "The installed Continuity build does not have a valid Authenticode signature; automatic staging is disabled for unsigned or development builds.");
        }

        foreach (var candidate in signatures.Skip(1))
        {
            if (!IsValid(candidate))
            {
                throw new InvalidDataException(
                    $"Automatic update candidate {Path.GetFileName(candidate.Path)} does not have a valid Authenticode signature.");
            }
            if (!string.Equals(
                    candidate.Thumbprint,
                    trusted.Thumbprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Automatic update candidate {Path.GetFileName(candidate.Path)} is signed by a different publisher certificate.");
            }
        }
    }

    private static bool IsValid(AuthenticodeSignature signature) =>
        string.Equals(signature.Status, "Valid", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(signature.Thumbprint);

    private static async Task<IReadOnlyList<AuthenticodeSignature>> ReadSignaturesAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ResolvePowerShell())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(SignatureScript);
        foreach (var path in paths)
        {
            startInfo.ArgumentList.Add(path);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start PowerShell for signature verification.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Authenticode verification failed: {Bound(error)}");
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ParseSignature).ToList()
                : [ParseSignature(document.RootElement)];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Authenticode verification returned invalid output.",
                exception);
        }
    }

    private static AuthenticodeSignature ParseSignature(JsonElement element) => new(
        element.GetProperty("path").GetString() ?? string.Empty,
        element.GetProperty("status").GetString() ?? string.Empty,
        element.TryGetProperty("thumbprint", out var thumbprint) &&
            thumbprint.ValueKind == JsonValueKind.String
                ? thumbprint.GetString()
                : null);

    private static string ResolvePowerShell()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in pathDirectories)
        {
            var candidate = Path.Combine(directory.Trim('"'), "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(windowsPowerShell)
            ? windowsPowerShell
            : throw new PlatformNotSupportedException(
                "PowerShell is required for Authenticode verification.");
    }

    private static string Bound(string value)
    {
        const int maximumLength = 1000;
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : singleLine[..maximumLength];
    }
}
