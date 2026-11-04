# T03 future contract test specification

Status: T07-T16 implemented; tests include WorkspaceReferenceTests, SessionResidueInventoryTests,
PreCleanupAuditTests, PostCleanupAuditTests, SessionOutcomeTests, WorkspaceMetadataProposalTests and Windows audit/report checks.
Evidence in artifacts/evidence/change-plan-2026-10-20/T07.txt through T16.txt. Remaining rows are NOT IMPLEMENTED /
NOT EXECUTED specifications, not passing tests. Future method names are not current APIs.
References: docs/change-plan-2026-10-20/03-清理完整性审计.md (T07–T11),
04-工作区元数据同步.md (T12–T16), docs/workspace-compatibility.md.

| Future scope | Required assertions before acceptance | Current status |
| --- | --- | --- |
| T07 full Workspace reference index | Archive, pin and member locations; Known/Unknown/Unsupported; duplicated fields, malformed JSON, uninitialized and pending mutation must not become no references | IMPLEMENTED: WorkspaceReferenceReader.Read, WorkspaceReferenceTests; no metadata writing |
| T08 relation inventory | Only proven associations; missing logs distinct from references; retained attachments out of scope; unknown relations stay unknown; no extra physical targets | IMPLEMENTED: SessionResidueInventory.Scan and SessionResidueInventoryTests |
| T09 pre-delete audit integration | Invoke inventory before confirmation, show proven relations and unknowns without adding physical targets | IMPLEMENTED: PreCleanupAuditor.Audit, MainForm/CleanupDialog, PreCleanupAuditTests and Windows audit checks |
| T10 post-delete rescan | Fresh target presence/log/metadata observations; physical success with references means metadata residue; read failure means unknown | IMPLEMENTED: PostCleanupAuditor.Audit, MainForm callback, PostCleanupAuditTests and Windows post-audit checks |
| T11 result window/log | Bind session status/residue/unknown facts independently of file-level execution metrics and sanitize logs | IMPLEMENTED: OutcomeReport, ResultsDialog, SessionOutcomeTests and extended Windows post-audit/report checks |
| T12 sync proposal/contract | Only selected sessions whose complete physical target set was deleted and rechecked absent; unchanged source and reference ordering; write gate separate | IMPLEMENTED: WorkspaceMetadataPlan/Proposal, WorkspaceMetadataProposalTests and workspace-sync-design.md; no writer |
| T13 mandatory backup | Original bytes, hash, identity and recoverability independently verified; optional session backup switch cannot disable metadata backup | IMPLEMENTED: WorkspaceBackupService.Create/Verify and WorkspaceBackupTests; synthetic recovery rehearsal, no production restore API |
| T14 writer | Empty version write allowlist refuses all writes until gates verified; active/unknown DSH, unsupported version, pending mutation, changed file, reparse and invalid backup refuse writes; controlled replacement/crash recovery | IMPLEMENTED: WorkspaceMetadataWriter.Prepare/Apply, 55 synthetic Windows cases; handle-bound no-overwrite renames, interruption evidence retained; T15 recovery coordination now implemented |
| T15 partial failure/retry | Separate physical and metadata outcomes; retained backup and verifiable restore; fresh checks before retry; no automatic log restoration | IMPLEMENTED: WorkspaceSyncCoordinator/WorkspaceRecoveryService and 44 T15 tests; NotRequested callback/report binding and Windows report checks; T16 opt-in/interaction now implemented |
| T16 optional UI | Default off; never write if unchecked; unmet gates disable operation; original cleanup path remains valid | IMPLEMENTED: CleanupDialog/MainForm/WorkspaceSyncReviewDialog; 13 availability tests and 72 dedicated GUI checks; production gate remains closed |

Current regression suite: `WorkspaceCompatibilityRegressionTests` targets only
`WorkspaceArchiveReader.Read`, `SessionLogDiscovery.Discover` and
`SessionCacheIndex.Build`. The archive-only reader currently does not validate
pending mutations or member arrays. Its non-null result cannot establish full
reference completeness or write safety. Characterization tests explicitly retain
that boundary; T07 must introduce independent full validation while preserving
archive-column compatibility. T07 now supplies this independent validation. The version write allowlist remains empty.

Synthetic fixture provenance is documented in Fixtures/WorkspaceFixture.md.
No actual release samples, UI acceptance, fault-injection writer tests or safe
metadata write capability are established by passing the current regressions.
