using System.Text.Json.Nodes;
using CodexContinuity;
using Xunit;

namespace CodexContinuity.Tests;

public sealed class SupervisorSuccessorHandoffTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");

    [Fact]
    public void RoundTripsTheExactSupervisorBackendAndRollbackContract()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            var expected = Handoff();

            store.Write(expected);

            Assert.Equal(
                new SupervisorSuccessorHandoffLoadResult(
                    SupervisorSuccessorHandoffLoadKind.Loaded,
                    expected),
                store.Load(Now));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExpiredHandoffsRemainInspectibleButCannotBeAdmitted()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            var handoff = Handoff();
            store.Write(handoff);

            Assert.Equal(
                new SupervisorSuccessorHandoffLoadResult(
                    SupervisorSuccessorHandoffLoadKind.Expired,
                    handoff),
                store.Load(handoff.ExpiresAtUtc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FutureDatedHandoffsFailClosedOutsideBoundedClockSkew()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            var future = Handoff() with
            {
                CreatedAtUtc = Now + SupervisorSuccessorHandoff.MaximumClockSkew +
                    TimeSpan.FromTicks(1),
                ExpiresAtUtc = Now + SupervisorSuccessorHandoff.MaximumClockSkew +
                    TimeSpan.FromMinutes(1),
            };
            store.Write(future);

            Assert.Equal(
                SupervisorSuccessorHandoffLoadKind.Invalid,
                store.Load(Now).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BackendMustMatchThePreviousSupervisorEndpointAndCodexHome()
    {
        var handoff = Handoff();

        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            Backend = handoff.Backend with { OwnerSupervisorProcessId = 12 },
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            Backend = handoff.Backend with { PublicPort = handoff.PublicPort + 1 },
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            Backend = handoff.Backend with { CodexHome = Path.GetTempPath() },
        }).Validate());
    }

    [Fact]
    public void EveryExecutableIdentityAndLifetimeIsBounded()
    {
        var handoff = Handoff();

        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            SelectedBuild = handoff.SelectedBuild with { ExecutableSha256 = "abc" },
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            RollbackBuild = handoff.RollbackBuild with { Executable = "relative.exe" },
        }).Validate());
        Assert.Throws<InvalidDataException>(() => (handoff with
        {
            HandoffId = handoff.HandoffId.ToUpperInvariant(),
        }).Validate());
    }

    [Theory]
    [InlineData("maximumLifetime")]
    [InlineData("nonPositiveLifetime")]
    [InlineData("futureSupervisorStart")]
    [InlineData("futureBackendStart")]
    public void EveryTemporalCoordinateFailsClosedIndependently(string coordinate)
    {
        var handoff = Handoff();
        var invalid = coordinate switch
        {
            "maximumLifetime" => handoff with
            {
                ExpiresAtUtc = handoff.CreatedAtUtc +
                    SupervisorSuccessorHandoff.MaximumLifetime + TimeSpan.FromTicks(1),
            },
            "nonPositiveLifetime" => handoff with { ExpiresAtUtc = handoff.CreatedAtUtc },
            "futureSupervisorStart" => handoff with
            {
                PreviousSupervisorStartedAtUtc = handoff.CreatedAtUtc + TimeSpan.FromTicks(1),
            },
            "futureBackendStart" => handoff with
            {
                Backend = handoff.Backend with
                {
                    BackendStartedAtUtc = handoff.CreatedAtUtc + TimeSpan.FromTicks(1),
                },
            },
            _ => throw new InvalidOperationException($"Unknown temporal coordinate {coordinate}."),
        };

        Assert.Throws<InvalidDataException>(invalid.Validate);
    }

    [Theory]
    [InlineData("{}", nameof(SupervisorSuccessorHandoffLoadKind.Invalid))]
    [InlineData(
        "{\"schemaVersion\":99}",
        nameof(SupervisorSuccessorHandoffLoadKind.UnsupportedSchema))]
    public void MalformedOrUnsupportedStateFailsClosed(
        string json,
        string expectedKindName)
    {
        var root = TemporaryDirectory();
        try
        {
            File.WriteAllText(ContinuityPaths.SupervisorHandoffFile(root), json);

            Assert.Equal(
                Enum.Parse<SupervisorSuccessorHandoffLoadKind>(expectedKindName),
                Store(root).Load(Now).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OversizedStateFailsClosedWithoutParsing()
    {
        var root = TemporaryDirectory();
        try
        {
            File.WriteAllText(
                ContinuityPaths.SupervisorHandoffFile(root),
                new string('x', 64 * 1024 + 1));

            Assert.Equal(
                SupervisorSuccessorHandoffLoadKind.Invalid,
                Store(root).Load(Now).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OversizedWritesCannotReplaceAnExistingHandoff()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            var original = Handoff();
            store.Write(original);
            var oversized = original with
            {
                SelectedBuild = original.SelectedBuild with
                {
                    Executable = $"C:\\{new string('x', 64 * 1024)}",
                },
            };

            Assert.Throws<InvalidDataException>(() => store.Write(oversized));
            Assert.Equal(original, store.Load(Now).Handoff);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MissingAndUnreadableStateHaveDistinctFailClosedResults()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            Assert.Equal(SupervisorSuccessorHandoffLoadKind.Missing, store.Load(Now).Kind);

            store.Write(Handoff());
            using var exclusive = new FileStream(
                ContinuityPaths.SupervisorHandoffFile(root),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.Equal(SupervisorSuccessorHandoffLoadKind.Unreadable, store.Load(Now).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ANewHandoffAtomicallyReplacesThePreviousManifest()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = Store(root);
            var original = Handoff();
            store.Write(original);
            var path = ContinuityPaths.SupervisorHandoffFile(root);
            using var originalReader = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var replacement = Handoff() with
            {
                HandoffId = Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210").ToString("N"),
            };

            store.Write(replacement);

            var originalJson = JsonNode.Parse(originalReader)!.AsObject();
            Assert.Equal(original.HandoffId, originalJson["handoffId"]!.GetValue<string>());
            Assert.Equal(replacement, store.Load(Now).Handoff);
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("runningBuild")]
    [InlineData("selectedBuild")]
    [InlineData("rollbackBuild")]
    [InlineData("backend")]
    public void EachMissingNestedIdentityFailsClosedWithoutEscapingTheLoader(string propertyName)
    {
        var root = TemporaryDirectory();
        try
        {
            var path = ContinuityPaths.SupervisorHandoffFile(root);
            Store(root).Write(Handoff());
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            manifest[propertyName] = null;
            File.WriteAllText(path, manifest.ToJsonString());

            Assert.Equal(
                SupervisorSuccessorHandoffLoadKind.Invalid,
                Store(root).Load(Now).Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SupervisorSuccessorHandoff Handoff()
    {
        var codexHome = Path.Combine(Path.GetTempPath(), "continuity-handoff-codex-home");
        var running = Build("0.3.0", "running.exe", 'a');
        return new SupervisorSuccessorHandoff(
            SupervisorSuccessorHandoff.CurrentSchemaVersion,
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef").ToString("N"),
            PreviousSupervisorProcessId: 42,
            PreviousSupervisorStartedAtUtc: Now - TimeSpan.FromHours(1),
            PublicPort: 45123,
            codexHome,
            running,
            SelectedBuild: Build("0.4.0", "selected.exe", 'b'),
            RollbackBuild: running,
            new BackendLease(
                BackendLease.CurrentSchemaVersion,
                OwnerSupervisorProcessId: 42,
                BackendProcessId: 43,
                PublicPort: 45123,
                BackendPort: 45124,
                BackendExecutable: Path.Combine(Path.GetTempPath(), "codex.exe"),
                codexHome,
                BackendStartedAtUtc: Now - TimeSpan.FromMinutes(30)),
            CreatedAtUtc: Now,
            ExpiresAtUtc: Now + TimeSpan.FromMinutes(1));
    }

    private static SupervisorExecutableIdentity Build(
        string version,
        string fileName,
        char sha256Character) => new(
            version,
            Path.Combine(Path.GetTempPath(), fileName),
            new string(sha256Character, 64));

    private static SupervisorSuccessorHandoffStore Store(string root) => new(
        ContinuityPaths.SupervisorHandoffFile(root));

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"codex-continuity-successor-handoff-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
