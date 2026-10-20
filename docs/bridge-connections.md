# Physical WebSocket connection authority

Each ComfyUI connection is reserved with the control plane before native connect.
The reservation captures the registered runner process, session runtime, client,
fresh connection UUID and expected ordering fence. A lost reservation response
retries the same request. A replacement attempt reads a fresh fence and uses a
new UUID; it never rebinds an existing attempt.

The runner reports Active with the applied session sealing profile after connect.
Retained feature bytes are captured before reservation and sent only after an
accepted Active response matching that exact connection and runtime. Subsequent
renewal uses Heartbeat, with one increasing sequence per new observation; response
loss retries the same sequence and bytes. Client messages carry the selected
connection UUID and lease revision, which enter the immutable command fingerprint.
A client message arriving before the matching Active response is installed waits
for that captured connection to finish activation and its initial feature send.
The wait and send share one five-second budget and observe caller/session
cancellation; rejected activation or retirement releases waiters without sending.
The final native send lock rechecks those identities, the tracked runtime and the
local lease budget. A delayed command cannot select a replacement connection.
Session owner callbacks retain the received trimmed session key, including UUID
format, so ordinal runtime and sealing lookups use the same key as the session
manager.

Database observation and expiry timestamps provide a duration, not a host-clock
comparison. The runner subtracts the whole monotonic RPC duration before using a
lease. Reports have a two-second timeout, connects and sends have a five-second
budget capped by the remaining lease, and heartbeats run every ten seconds.
Transient renewal failures retain sendability only under the last accepted
monotonic deadline; they cannot extend it. The local RPC timer's gRPC Cancelled
status retries the same reservation/report identity while the owning lifetime
remains alive. A detached control sink is likewise transient: reattachment retries
under the same accepted deadline without opening another native connection.
Stop or shutdown cancellation does not retry. A definitive rejected or mismatched
response publishes irreversible revocation for that physical connection before
waiting for the native send lock; already queued sends check it before writing.
Authority expiry or runtime retirement also removes sendability and aborts the socket. A response's
absolute deadline is computed before waiting for the native send lock; lock
contention consumes that budget. Native I/O already in progress may have reached the peer before remote
revocation. Socket transport acceptance is not native applied-state evidence.

A relay owns at most 32 bridge objects, including connecting and retrying objects.
Client identifiers are exact UTF-8 strings of at most 128 bytes, with no NUL or
outer whitespace. Registry creation is serialized; there are no speculative
dictionary factories. Replacement joins the obsolete object before releasing its
slot. Before admission, completed or no-longer-tracked bridge actors are joined
and removed, releasing their slots. Definite reservation rejection ends the actor;
a stale or expired claim can retry a fresh UUID/cursor while its runtime remains
current. Healthy owned bridges remain counted against the cap. Stop and host
shutdown cancel and join owned tasks, with independent bounded
best-effort Inactive reports. Retained client feature frames can be replayed on a
new authorized connection; one-off client messages are never retained for replay.

These protocol changes require a coordinated control-plane/runner release and
regenerated consumers. Mixed old/new operation is unsupported. Controlled native
WebSocket tests cover ordering, response loss, exact retries, detached control
reattachment, queued sends after received rejection or mismatch, stale sends and
cleanup. They do not qualify actual GPU runtimes, fleet resource use, outages or
image deployment; those remain release checks.
