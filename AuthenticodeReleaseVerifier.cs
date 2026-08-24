using System.Diagnostics;
using System.Text.Json;

namespace CodexContinuity;

internal sealed record AuthenticodeSignature(
    string Path,
    string Status,
    string? Thumbprint,
    string? Subject,
    string? Issuer,
    string? RootThumbprint,
    string? SubscriberIdentityEku,
    int SubscriberIdentityEkuCount,
    bool HasCodeSigningEku,
    bool HasPublicTrustMarker);

internal sealed record AuthenticodePublisherIdentity(
    string Thumbprint,
    string? SubscriberIdentityEku,
    string RootThumbprint,
    bool IsArtifactSigning);

internal static class AuthenticodeReleaseVerifier
{
    private const string SignatureScript =
        "& { $results = @($args | ForEach-Object { " +
        "$signature = Get-AuthenticodeSignature -LiteralPath $_; " +
        "$certificate = $signature.SignerCertificate; " +
        "$rootThumbprint = $null; " +
        "$subscriberIdentityEku = $null; " +
        "$subscriberIdentityEkuCount = 0; " +
        "$hasCodeSigningEku = $false; " +
        "$hasPublicTrustMarker = $false; " +
        "$durableIdentityPrefix = '1.3.6.1.4.1.311.97.'; " +
        "$publicTrustMarker = '1.3.6.1.4.1.311.97.1.0'; " +
        "$codeSigningEku = '1.3.6.1.5.5.7.3.3'; " +
        "if ($null -ne $certificate) { " +
        "$ekuValues = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' } | ForEach-Object { $_.EnhancedKeyUsages } | ForEach-Object { $_.Value }); " +
        "$subscriberIdentityEkus = @($ekuValues | Where-Object { $_ -like ($durableIdentityPrefix + '*') -and $_ -ne $publicTrustMarker }); " +
        "$subscriberIdentityEkuCount = $subscriberIdentityEkus.Count; " +
        "if ($subscriberIdentityEkuCount -eq 1) { $subscriberIdentityEku = $subscriberIdentityEkus[0] }; " +
        "$hasCodeSigningEku = $ekuValues -contains $codeSigningEku; " +
        "$hasPublicTrustMarker = $ekuValues -contains $publicTrustMarker; " +
        "$chain = [System.Security.Cryptography.X509Certificates.X509Chain]::new(); " +
        "try { " +
        "$chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck; " +
        "$chain.ChainPolicy.VerificationFlags = [System.Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid; " +
        "$chainBuilt = $chain.Build($certificate); " +
        "if ($chainBuilt -and $chain.ChainElements.Count -gt 0) { " +
        "$rootThumbprint = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint } " +
        "} finally { $chain.Dispose() } } " +
        "[pscustomobject]@{ path = $_; status = [string]$signature.Status; " +
        "thumbprint = if ($null -eq $certificate) { $null } else { $certificate.Thumbprint }; " +
        "subject = if ($null -eq $certificate) { $null } else { $certificate.Subject }; " +
        "issuer = if ($null -eq $certificate) { $null } else { $certificate.Issuer }; " +
        "rootThumbprint = $rootThumbprint; " +
        "subscriberIdentityEku = $subscriberIdentityEku; " +
        "subscriberIdentityEkuCount = $subscriberIdentityEkuCount; " +
        "hasCodeSigningEku = $hasCodeSigningEku; " +
        "hasPublicTrustMarker = $hasPublicTrustMarker } }); " +
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
        var trustedIdentity = GetPublisherIdentity(trusted);
        if (trustedIdentity is null)
        {
            throw new InvalidDataException(
                "The installed Continuity build does not have a valid Authenticode signature and complete publisher chain/trusted identity; automatic staging is disabled for unsigned, development, or ambiguous builds.");
        }

        foreach (var candidate in signatures.Skip(1))
        {
            var candidateIdentity = GetPublisherIdentity(candidate);
            if (candidateIdentity is null)
            {
                throw new InvalidDataException(
                    $"Automatic update candidate {Path.GetFileName(candidate.Path)} does not have a valid Authenticode signature and complete publisher chain/trusted identity.");
            }
            if (!PublisherIdentitiesMatch(trustedIdentity, candidateIdentity))
            {
                throw new InvalidDataException(
                    $"Automatic update candidate {Path.GetFileName(candidate.Path)} is signed by a different publisher identity or certificate chain.");
            }
        }
    }

    private static bool PublisherIdentitiesMatch(
        AuthenticodePublisherIdentity trusted,
        AuthenticodePublisherIdentity candidate) =>
        trusted.IsArtifactSigning && candidate.IsArtifactSigning
            ? string.Equals(
                trusted.SubscriberIdentityEku,
                candidate.SubscriberIdentityEku,
                StringComparison.Ordinal) &&
                string.Equals(
                    trusted.RootThumbprint,
                    candidate.RootThumbprint,
                    StringComparison.Ordinal)
            : !trusted.IsArtifactSigning &&
                !candidate.IsArtifactSigning &&
                string.Equals(trusted.Thumbprint, candidate.Thumbprint, StringComparison.Ordinal) &&
                string.Equals(trusted.RootThumbprint, candidate.RootThumbprint, StringComparison.Ordinal);

    private static AuthenticodePublisherIdentity? GetPublisherIdentity(
        AuthenticodeSignature signature)
    {
        if (!string.Equals(signature.Status, "Valid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var thumbprint = NormalizeThumbprint(signature.Thumbprint);
        var rootThumbprint = NormalizeThumbprint(signature.RootThumbprint);
        if (thumbprint is null || rootThumbprint is null)
        {
            return null;
        }

        var hasArtifactIdentityEvidence =
            signature.SubscriberIdentityEkuCount > 0 ||
            !string.IsNullOrWhiteSpace(signature.SubscriberIdentityEku) ||
            signature.HasPublicTrustMarker;
        if (!hasArtifactIdentityEvidence)
        {
            // Legacy PFX mode remains intentionally leaf-pinned. It cannot
            // rotate automatically, but it does not weaken Artifact Signing
            // identity checks or permit a cross-mode publisher transition.
            return new AuthenticodePublisherIdentity(thumbprint, null, rootThumbprint, false);
        }

        var subscriberIdentityEku = NormalizeSubscriberIdentityEku(signature.SubscriberIdentityEku);
        if (signature.SubscriberIdentityEkuCount != 1 ||
            subscriberIdentityEku is null ||
            !signature.HasCodeSigningEku ||
            !signature.HasPublicTrustMarker)
        {
            return null;
        }

        return new AuthenticodePublisherIdentity(thumbprint, subscriberIdentityEku, rootThumbprint, true);
    }

    private static string? NormalizeSubscriberIdentityEku(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        const string durableIdentityPrefix = "1.3.6.1.4.1.311.97.";
        const string publicTrustMarker = "1.3.6.1.4.1.311.97.1.0";
        var suffixSegments = normalized.StartsWith(durableIdentityPrefix, StringComparison.Ordinal)
            ? normalized[durableIdentityPrefix.Length..].Split('.')
            : Array.Empty<string>();
        return normalized.StartsWith(durableIdentityPrefix, StringComparison.Ordinal) &&
            !string.Equals(normalized, publicTrustMarker, StringComparison.Ordinal) &&
            suffixSegments.Length > 0 &&
            suffixSegments.All(segment =>
                segment.Length > 0 && segment.All(character => character is >= '0' and <= '9'))
            ? normalized
            : null;
    }

    private static string? NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = string.Concat(value.Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();
        return normalized.Length == 40 && normalized.All(IsHexCharacter)
            ? normalized
            : null;
    }

    private static bool IsHexCharacter(char value) =>
        (value is >= '0' and <= '9') ||
        (value is >= 'A' and <= 'F') ||
        (value is >= 'a' and <= 'f');

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
                : null,
        element.TryGetProperty("subject", out var subject) &&
            subject.ValueKind == JsonValueKind.String
                ? subject.GetString()
                : null,
        element.TryGetProperty("issuer", out var issuer) &&
            issuer.ValueKind == JsonValueKind.String
                ? issuer.GetString()
                : null,
        element.TryGetProperty("rootThumbprint", out var rootThumbprint) &&
            rootThumbprint.ValueKind == JsonValueKind.String
                ? rootThumbprint.GetString()
                : null,
        element.TryGetProperty("subscriberIdentityEku", out var subscriberIdentityEku) &&
            subscriberIdentityEku.ValueKind == JsonValueKind.String
                ? subscriberIdentityEku.GetString()
                : null,
        element.TryGetProperty("subscriberIdentityEkuCount", out var subscriberIdentityEkuCount) &&
            subscriberIdentityEkuCount.TryGetInt32(out var identityCount)
                ? identityCount
                : 0,
        element.TryGetProperty("hasCodeSigningEku", out var hasCodeSigningEku) &&
            hasCodeSigningEku.ValueKind == JsonValueKind.True,
        element.TryGetProperty("hasPublicTrustMarker", out var hasPublicTrustMarker) &&
            hasPublicTrustMarker.ValueKind == JsonValueKind.True);

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
