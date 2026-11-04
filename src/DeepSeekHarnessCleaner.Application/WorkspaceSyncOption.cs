namespace DeepSeekHarnessCleaner.Application;

/// <summary>Read-only UI availability, never a write permit. Counts are potential references before physical deletion.</summary>
public sealed record WorkspaceSyncOption(Guid CleanupPlanId, int? ReferenceCount, string BackupRoot, bool Available, string? Reason);
