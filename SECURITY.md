# Security

Please report vulnerabilities privately through GitHub's "Report a vulnerability" button on this repository's Security tab, not in a public issue.

Include what is affected, how to reproduce it, and what an attacker could do with it. You will get an answer within a week.

## What matters most here

- **Runner keys.** A key lets its holder register a runner for the key's owner. The agent sends it only as the `x-runner-secret` header over TLS, the desktop app stores it encrypted, and neither logs it. Anything that leaks a key is in scope.
- **Session isolation.** Each editor session runs as its own Linux user inside the runner container. Escapes between sessions, or from a session into the agent, are in scope.
- **The model VFS shim.** `liboffloadr_model_vfs.so` is preloaded into the editor process and talks to the agent over a Unix socket. Bugs that let the editor read or write outside its session through that socket are in scope.
- **The desktop supervisor.** It runs the docker or podman CLI. Anything that makes it run a different image, mount different folders, or expose the key on a command line is in scope.

Issues in ComfyUI, Forge Neo or their custom nodes belong with those projects. Report them here as well when the runner makes them worse.
