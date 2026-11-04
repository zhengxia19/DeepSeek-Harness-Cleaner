namespace DeepSeekHarnessCleaner.Infrastructure;

public enum RootCandidateKind
{
    DshHome,
    DesktopHome,
}

public enum RootCandidateSource
{
    Default,
    EnvironmentVariable,
    Saved,
    UserSelected,
}

public enum RootCandidateStatus
{
    Valid,
    Missing,
    Invalid,
    Inaccessible,
}

/// <summary>
/// A read-only root proposal, not permission to clean. NormalizedPath is null if normalization failed.
/// Sources retains every origin when multiple spellings identify the same ordinary directory.
/// Reparse paths are rejected rather than followed to establish identity.
/// </summary>
public sealed record RootCandidate(
    RootCandidateKind Kind,
    string InputPath,
    string? NormalizedPath,
    IReadOnlyList<RootCandidateSource> Sources,
    RootCandidateStatus Status,
    string? Reason);

/// <summary>Independent DSH and desktop proposals; never implicitly selects or merges roots.</summary>
public sealed record RootDiscoveryResult(IReadOnlyList<RootCandidate> Candidates);
