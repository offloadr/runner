# Security

Please report vulnerabilities privately through GitHub's "Report a vulnerability" button on this repository's Security tab, not in a public issue.

Include what is affected, how to reproduce it, and what an attacker could do with it. You will get an answer within a week.

## What matters most here

- **Runner keys.** A key lets its holder register a runner for the key's owner. The agent sends it only as the `x-runner-secret` header over TLS, the desktop app stores it encrypted, and neither logs it. Anything that leaks a key is in scope.
- **Session isolation.** Each editor session runs as its own Linux user inside the runner container. Escapes between sessions, or from a session into the agent, are in scope. Process-only quiescence retains the account and data until final cleanup. Cleanup revalidates the account uid before acting on a remembered identity; this does not establish complete reader joining or protection against concurrent host-root account changes.
- **Runtime activity.** Required activity delivery must retain the original process identity and sealing flags through retries, without copying diagnostic or prompt content into the fact. The current transport primitives still need native lifetime integration and qualification; they do not establish diagnostic retention or deletion.
- **The model VFS shim.** `liboffloadr_model_vfs.so` is preloaded into the editor process and talks to the agent over a Unix socket. Bugs that let the editor read or write outside its session through that socket are in scope.
- **Sealed storage.** In sealed mode a session's inputs, outputs, temp files and workspace state are meant to stay in memory-backed storage and out of logs and core dumps. Anything that makes the runner write them to disk, or keep them after the session ends, is in scope, and so is anything that makes a sealed session accept a transfer that is not sealed to it or use a reader key beyond its session. A host that reads the machine's memory is not.
- **The desktop supervisor.** It runs the docker or podman CLI. Anything that makes it run a different image, mount different folders, or expose the key on a command line is in scope.

Issues in ComfyUI, Forge Neo or their custom nodes belong with those projects. Report them here as well when the runner makes them worse.
