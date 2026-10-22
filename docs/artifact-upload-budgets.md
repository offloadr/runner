# Artifact upload budgets

The artifact uploader uses one queue reader per session. A stalled upload must
release that reader without treating cancellation as evidence that the server
saved or rolled back a file.

Each pending entry starts with a bounded setup allowance. Its elapsed clock
starts at the first dequeue and continues through file checks, metadata, body,
completion, response observation, I/O retries, requeue delay and time waiting
behind other files after a retry. Initial queue wait is outside this delivery
clock. Pending admission and Stop have separate bounds.

| Boundary | Allowance | What renews it |
| --- | --- | --- |
| Metadata/setup for an attempt | Two minutes | A new attempt may start this phase; the elapsed ceiling stays fixed |
| Body without progress | Two minutes | Completion of a positive-byte body write |
| Stream completion and committed response | Two minutes together | Nothing within that phase |
| Entire pending entry after first dequeue | Four minutes plus wire bytes divided by 256 KiB/s, rounded up to whole seconds | Nothing, including retries or progress |

Size is bound once to the first securely opened snapshot, before its RPC. A
watcher's initial empty or growing file length cannot size that transfer. For
ordinary uploads, wire bytes are the snapshot length; sealed uploads add their
authentication-tag overhead. Binding keeps the original dequeue clock and cannot
revive an expired attempt. The same pending entry never receives a larger
allowance because a file changes during a retry. The ordinary 4 GiB file
limit therefore has an elapsed ceiling of four hours, 37 minutes and four seconds.
These are limits, not a throughput guarantee. The single reader cannot start a
second file while the first transfer is progressing within its allowance.

Only outgoing body progress renews the body clock. It proves that a write
completed in the client transport, not that the server committed it. Elapsed or
phase expiry cancels the actual gRPC call and the associated file reads and
sealing work. A local expired observation is distinguished from session shutdown.

The existing queued retry cap also applies when editor identity is absent.
Transient failures and changed-file retries retain their capped attempt count;
all nested I/O attempts share the original elapsed ceiling. A later refresh can
queue an unsaved file again, but an unchanged secure file snapshot retains its
original opaque upload ID.

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
