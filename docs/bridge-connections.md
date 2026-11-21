# Native WebSocket connections

For ComfyUI, the runner opens one native WebSocket per browser client to the editor on
its own machine, with the browser's client id. The runner owns these sockets; the
control plane holds no lease on them.

- **Who gets one.** Only the session's tracked runtime, and only once its data
  protection (sealing) was applied. A socket belongs to exactly one session, client and
  runtime; a newer runtime for the same client replaces it, and a lookup for another
  runtime never reaches it.
- **Connecting.** Each connection waits up to five seconds. After a drop it reconnects
  after one second, doubling to ten, while the runtime stays current. It is aborted as
  soon as the runtime retires or stops.
- **Handshake.** The latest connection handshake the browser sent with a prompt (its
  feature flags) is retained and sent first on every connection, before any client
  message.
- **Bridge state.** Once the handshake is sent, the runner reports a session event with
  `bridge_state` set and a new `connection_id` (a UUID per native connection), then
  repeats it every ten seconds while the socket stays open. The event has no payload.
  Only the runner sets this field; an editor frame is never turned into one, whatever its
  `type`. A new `connection_id` tells the control plane that per-client editor state,
  such as the feature flags and the log subscription, must be sent again: the editor
  forgets a log subscription when a log line is written while the client is away.
- **Client messages.** A client message goes to the open connection of its client under
  its exact runtime, after that connection's handshake. If none is open it waits up to
  five seconds; then the send fails and its request is acknowledged as failed. It is
  never retained or replayed on a later connection.
- **Limits.** A relay owns at most 32 sockets, including connecting ones. Client ids
  are exact UTF-8 strings of at most 128 bytes, with no NUL or outer whitespace.
  Completed or untracked sockets are joined before admission; Stop and shutdown cancel
  and join every socket of the session.

A bridge state queued just before its runtime retires can still be delivered, as can
any editor frame already in flight. What it triggers on the control plane passes the
runtime's expected-target check and Ready fence, which refuse a stopping or replaced
runtime.

Socket transport acceptance is not evidence that the editor applied anything.
Controlled native WebSocket tests cover ordering, reconnects, bridge state, waiting
client messages and cleanup. They do not qualify actual GPU runtimes; those remain
release checks.
