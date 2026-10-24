# Artifact upload budgets

The artifact uploader uses one queue reader per session. A stalled upload must
release that reader without treating cancellation as evidence that the server
saved or rolled back a file.

Each pending entry starts with a bounded setup allowance. Its elapsed clock
starts at the first dequeue and continues through file checks, metadata, body,
completion, response observation, I/O retries, requeue delay and time waiting
behind other files after a retry. Automatic rescans of the unchanged version keep
that same budget after its pending entry is removed. Initial queue wait is outside
this delivery clock. Pending admission and Stop have separate bounds.

| Boundary | Allowance | What renews it |
| --- | --- | --- |
| Metadata/setup for an attempt | Two minutes | A new attempt may start this phase; the elapsed ceiling stays fixed |
| Body without progress | Two minutes | Completion of a positive-byte body write |
| Stream completion and committed response | Two minutes together | Nothing within that phase |
| Pending entry and automatic rescans of its unchanged version after first dequeue | Four minutes plus wire bytes divided by 256 KiB/s, rounded up to whole seconds | Nothing, including retries or progress |

Size is bound once to the first securely opened snapshot, before its RPC. A
watcher's initial empty or growing file length cannot size that transfer. For
ordinary uploads, wire bytes are the snapshot length; sealed uploads add their
authentication-tag overhead. Binding keeps the original dequeue clock and cannot
revive an expired attempt. A genuinely changed secure version leaves the old pending entry before
admission, so its new snapshot receives its own size-based allowance and retry
count. This applies after a committed or retired first response; it does not renew
the allowance or issued ID for an unchanged failed snapshot. The ordinary 4 GiB file
limit therefore has an elapsed ceiling of four hours, 37 minutes and four seconds.
These are limits, not a throughput guarantee. The single reader cannot start a
second file while the first transfer is progressing within its allowance.

Only outgoing body progress renews the body clock. It proves that a write
completed in the client transport, not that the server committed it. Elapsed or
phase expiry cancels the actual gRPC call and the associated file reads and
sealing work. A local expired observation is distinguished from session shutdown.

The existing queued retry cap also applies when editor identity is absent.
Transient failures of an unchanged snapshot retain their capped attempt count;
all its nested I/O attempts share the original elapsed ceiling. A later refresh can
queue an unsaved file again, but an unchanged secure file snapshot retains its
original opaque upload ID.

Retained budgets have a separate bounded set of one entry per current path,
including a timeout before upload-ID issuance. Its limit is the pending allowance
plus the unacknowledged-snapshot allowance: 8,192 by default. New paths wait when
that set is full; live failed versions are not evicted to admit them. Committed
unchanged files and catalog readbacks or placeholders without an uncertain
binding release their budget, including inactive budgets exhausted before ID
issuance. A catalog hash match preserves same-ID receipt recovery for an
uncertain unchanged snapshot. Catalog application reclaims recognized inactive
budgets before scanning output and temp, so folder order cannot strand waiting
files behind capacity that the same refresh can release. Deletion or rename notifications securely
inspect only bindings at or beneath the affected old path and wake an owed scan.
Replacing an output or temp root also reclaims missing bindings, including when
the root no longer opens. Inspection failures retain the binding. An explicit refresh may start a new elapsed allowance, while preserving
an unchanged snapshot's issued ID and any permanent retirement result.

An upload is recorded as saved only after a matching committed receipt. Expiry
does not prove rollback: retry can recover an existing server publication. Stop
still counts files whose acknowledgement was not observed and reports each loss
once. Its five-second drain, ten-second join and 25-second loss-recording bounds
remain independent. Server receipt retention and resource admission are separate
policies.

Deterministic timer tests and token-aware controlled clients cover local policy
semantics. Real TLS cancellation/backpressure, large ordinary/sealed transfer
measurements, native runtime behavior and the complete release checklist require
their own evidence.

A failed attempt rechecks the secure file version before requeueing. A replacement
gets a fresh pending delivery with its own retry count and elapsed allowance.
The opened version is checked again before upload identity issuance, covering
replacement after that queue check and during an inner I/O retry. Unchanged
versions retain their original upload ID, retry count and elapsed deadline.
