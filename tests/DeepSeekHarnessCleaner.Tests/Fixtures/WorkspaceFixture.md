# Workspace T02 fixtures

`WorkspaceFixture` constructs synthetic single-file Workspace v2 JSON using the
source contract in `docs/workspace-compatibility.md`. No fixture was copied from
user data. Workspace paths point to the owned temporary fixture root. Session
v4 logs contain only a synthetic native header, with no chat text or credentials.

Cases: archive, pin, two workspaces, missing log, duplicate archive ID, duplicate
member ID, unknown version 999, uninitialized state, pending create/delete,
malformed JSON and a whitespace file change that changes the SHA-256 fingerprint.
The link test creates a real file symbolic link to a separate synthetic root;
it requires OS permission and must not be replaced by a fake link or a passing
assertion when that permission is absent. It removes the link before root cleanup.

Provenance: all cases are constructed test data. Workspace v2 and session v4
real release samples remain UNVERIFIED by this task. Unknown version 999 is an
intentional unsupported input, not a compatibility claim. Per-record and SQLite
samples are UNVERIFIED. These fixtures do not establish a metadata write allowlist.

Fixture tests validate data construction only. Reader/discovery/cache behavioral
regressions belong to T03; future audit/write contracts are not implemented here.
