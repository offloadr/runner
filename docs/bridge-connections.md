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
Failure to renew removes sendability; expiry or runtime retirement aborts the
socket. Native I/O already in progress may have reached the peer before remote
revocation. Socket transport acceptance is not native applied-state evidence.

A relay owns at most 32 bridge objects, including connecting and retrying objects.
Client identifiers are exact UTF-8 strings of at most 128 bytes, with no NUL or
outer whitespace. Registry creation is serialized; there are no speculative
dictionary factories. Replacement joins the obsolete object before releasing its
slot. Stop and host shutdown cancel and join owned tasks, with independent bounded
best-effort Inactive reports. Retained client feature frames can be replayed on a
new authorized connection; one-off client messages are never retained for replay.

These protocol changes require a coordinated control-plane/runner release and
regenerated consumers. Mixed old/new operation is unsupported. Controlled native
WebSocket tests cover ordering, response loss, exact retries, stale sends and
cleanup. They do not qualify actual GPU runtimes, fleet resource use, outages or
image deployment; those remain release checks.
