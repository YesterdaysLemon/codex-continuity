namespace CodexContinuity.Contracts;

// These names are persisted in the update state files and are compiled into
// both the supervisor and the optional tray. Keep the check-state and
// activation-state vocabularies separate: they describe different ledgers.
internal static class ContinuityUpdateCheckStateNames
{
    internal const string Active = "active";
    internal const string Inactive = "inactive";
    internal const string Staged = "staged";
    internal const string Deferred = "deferred";
    internal const string Failed = "failed";
    internal const string Unknown = "unknown";
    internal const string Ahead = "ahead";
    internal const string Observed = "observed";

    internal static bool IsKnown(string state) => state is
        Active or Inactive or Staged or Deferred or Failed or Unknown or Ahead or Observed;
}

internal static class ContinuityUpdateApplyStateNames
{
    internal const string StagedOnly = "stagedOnly";
    internal const string Waiting = "waiting";
    internal const string Applying = "applying";
    internal const string Active = "active";
    internal const string RolledBack = "rolledBack";
    internal const string Failed = "failed";

    internal static bool IsKnown(string state) => state is
        StagedOnly or Waiting or Applying or Active or RolledBack or Failed;
}
