# Artifact upload identity and retirement

Before sending a new securely opened output snapshot, the runner calls
`RunnerArtifactService.IssueUploadIdentity` with its captured process/session/runtime
identity. Issuance has no artifact effect. If its response is lost before an upload
starts, an unused sequence may be skipped. The returned ID is correlation, not a
capability, content digest, successful save or execution grant. The runner keeps
this non-whitespace opaque string verbatim; it does not parse its representation.

The uploader stores that issued ID with a local fingerprint of the secure inode,
length, modification/change times and immutable upload metadata. The fingerprint
stays local. Retries, rescans, fresh sealing envelopes and removal from the retry
queue preserve the ID. A changed file version or target obtains a new ID.

Unacknowledged snapshots retain their bindings in a bounded set, with a default
limit of 4,096 and no eviction of live uncertainty to admit new files. At capacity,
new snapshots wait for rescans. A deleted local file can release its binding;
inspection errors do not prove deletion. Acknowledged bindings use the existing
one-entry-per-current-file catalog, rather than retaining version history. Remote
catalog refresh/deletion cannot erase that local binding while the exact
acknowledged local version survives. An evicted placeholder is not that version;
if its remote artifact disappears, its catalog entry is removed and Stop cannot
count the placeholder as saved. A retained binding for live local bytes also
counts as unsaved after remote deletion; retaining identity is not proof that the
control plane still holds those bytes.
A remotely deleted artifact is not eligible for eviction into a download placeholder.
The existing catalog's total size is a separate resource constraint; this does not
establish a global runner memory bound.

The pending queue and retained uncertainty have separate limits. Lowering the
pending queue does not invalidate the default uncertainty allowance; retained
bindings may outlive their queue entries. If uncertainty fills after a file was
admitted to the pending queue, that file releases its pending entry immediately
and leaves an owed rescan. Capacity is not retried as a file-lock failure on the
single upload reader. The uncertainty policy may lower its
own limit but cannot raise it above 4,096.

An owed rescan still retries uncertain paths at that limit, so successful replies
can free capacity for waiting files. Unchanged retired versions stay out of the
queue; rescanning them cannot save them or release their binding. A file rewritten
while a retired reply is pending is rechecked and queued under its new version.
New versions receive their own retry count even after the old snapshot exhausted
its retries. Requeues within a pending entry retain its original elapsed allowance.
After the old entry finishes, including a terminal timeout, the runner rechecks
the secure version after releasing it. Only a changed current file starts a new
pending entry with its own allowance; an unchanged failed snapshot does not
automatically renew its exhausted budget.
Committed receipt paths follow the server catalog's case-insensitive key identity;
the catalog may retain an older spelling. Upload ID, editor and size remain exact.
Saved accounting always uses the request's captured local path and secure version,
never a path selected by the response. Acknowledged entries retain the committed
metadata alongside that binding. A refresh containing identical committed metadata
preserves the binding even when catalog spelling differs; deletion or changed
metadata cannot mark that alias saved. No per-version catalog history is retained.
This also preserves readback for the exact empty placeholder left by eviction.
If the remote metadata disappears or changes, that placeholder remains recognizable
as unsaved; its old upload binding is cleared. A late watcher event or another
output scan cannot upload the agent's empty placeholder as new user data. A
replaced local file does not inherit that placeholder's saving status.
The uploader rechecks the securely opened version before issuing an upload ID,
under the same gate that publishes and records a completed download, so a watcher
event queued during readback cannot republish the downloaded file.

The control plane may collect resolved receipts from an active assignment only
with a durable sequence retirement fence. `RejectedRetiredUpload` is distinct from
runtime retirement and commitment. The same local snapshot keeps its retired ID,
is not saved or evictable, and contributes to Stop loss reporting. It cannot acquire
another issued ID merely because its original receipt is gone. Actual saving still
requires a matching committed receipt and secure post-upload file-version check.

The original pending-upload timing allowances remain in effect. These protocol
and bookkeeping changes require a coordinated server release; declarations alone
do not establish server enforcement or runtime qualification.
