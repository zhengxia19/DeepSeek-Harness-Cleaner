# Observed Workspace v2 redacted sample

This is a redacted observation of local Workspace metadata, not a transcript or a synthetic source-contract example.
The matching provenance JSON records original file SHA, filesystem write time, separate observation time, redacted-file SHA and counts.
Source was read with shared read/write/delete access and verified unchanged. No real data was modified or raw JSON persisted.

IDs are consistently renumbered while array order and relationships remain. Paths and titles are replaced.
Timestamp values are replaced with `2000-01-01T00:00:00.000Z`, explicitly not original event/provider times.
Unknown fields, invalid ID types/duplicates or invalid required field shapes refuse sample generation.

Observed desktop/package version: `0.2.1-rc.2`; producer commit/backend/root mapping remains unverified.
A Known read-only index does not authorize production writing or prove the desktop equals the pinned upstream commit.
Tests copy only the redacted sample into synthetic fixtures; they never open the provenance's real source path token.
