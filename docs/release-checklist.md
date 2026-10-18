# Release checklist: GPU checks

CI has no GPUs, so these checks are run by hand on a machine with an NVIDIA GPU for each release of the runner's own code. Everything else, including the model VFS shim and real aria2 transfers, is covered by CI.

GPU power limits are not on this list: changing them does not work on any host yet, so there is nothing to confirm.

Use a non-production Offloadr environment and a personal runner key created there. Record the commit, the image digests and anything that failed in the release notes or the pull request.

## What it is, and what it is not

The `Publish runner images` workflow moves the channel tags people download as soon as it runs, and it also runs on its own whenever an editor image updates upstream. This list is therefore no gate in front of every published image. An editor-triggered run also builds the runner code on `main` as it is at that moment, so runner changes merged to `main` go out with the next editor update even before anyone checked them: run this list soon after runner changes land on `main`, or before merging the larger ones. When a check fails, publish a fix; people who need to can meanwhile pick another editor channel.

## You need

- A Windows machine with an NVIDIA GPU, a current driver, and Docker Desktop on the WSL 2 backend. A Linux host with the NVIDIA Container Toolkit works for the container checks.
- The four images of the release commit. Preferably the published ones, by the digests in the summary of the `Publish runner images` run for that commit (`ghcr.io/offloadr/<repository>@sha256:<digest>`): exactly what personal and hosted runners get. To check before publishing, build them locally instead with `SOURCE_REVISION=$(git rev-parse HEAD) docker buildx bake --pull --load`; `--pull` builds on the current editor images, but it is still a rebuild, so check the published digests too once they exist.
- The desktop app built from the same commit: the CI artifact, or `build/publish-windows.ps1 -Unpackaged`.

## 1. Container runner

Repeat for each editor you release: ComfyUI `latest` and `master`, Forge Neo `neo` and `latest`.

1. Start the runner from the image: a published one by its digest, which always names exactly that build, or a local build by its tag (`ghcr.io/offloadr/runner-comfyui:latest`, say) with `--pull never`, so Docker does not replace it with the published one. Check that its `org.opencontainers.image.revision` label (`docker image inspect`) names the release commit. The key is read without echo and passed by name only, so it stays out of the command line and the shell history:

   ```bash
   read -rs RUNNER_SECRET && export RUNNER_SECRET
   docker run --rm -it --gpus all -e OFFLOADR_API_GRPC=<environment-origin> -e RUNNER_SECRET <image>
   ```

2. The log shows `Status update accepted=True` and `Runner commands stream (grpc) connected` within a few seconds, and the source notice for this repository.
3. In the web app, the runner shows as your personal runner and is offered for a workspace on the matching editor, and not for the other editor.
4. Start a GPU session on it. While the session runs, `docker exec <container> cat /run/offloadr/status.json` shows its `active_session_id`.
5. Run one prompt that needs a model the runner has not downloaded yet. It finishes, the output appears in the editor, and the log shows the download progressing.
6. Run a second prompt with the same model. It starts without downloading again.
7. Stop the session from the web app. The container keeps running, and the status file's `active_session_id` is empty again.
8. Repeat steps 4 to 7 with sealed storage, on a GPU pod with a few GiB of `/dev/shm` (a rental provider, or `--shm-size=8g` locally) and `-e RUNNER_SEALED_STORAGE=required`:
   1. Before starting the session, `touch /tmp/sealed-marker` in the container.
   2. Use a distinctive prompt and upload an input image. Run one prompt that succeeds, one that fails in a node, and one you cancel, then restart the editor from the Manager if it offers that.
   3. For each process of the session user (`docker exec <container> ps -eo pid,user,args`, user `sess_…`), `docker exec <container> stat -c %U /proc/<pid>` prints `root`: the `/proc` entries of non-dumpable processes belong to root. Only static binaries that do not load the VFS shim may show the session user. `docker exec <container> grep 'Max core file size' /proc/<pid>/limits` shows `1` for both limits.
   4. `docker exec <container> find / -xdev -newer /tmp/sealed-marker -type f` lists only the virtualenv, models, custom nodes and caches under `/sessions/<id>`, and none of your prompt, input or output names. `docker logs <container>` contains no prompt text or output filenames.
   5. `docker exec <container> du -sh /dev/shm/offloadr` grows while you generate, `status.json` shows `sealed_storage` with a budget, and after you stop the session `/dev/shm/offloadr` is empty.
9. Stop the container (Ctrl+C in its window, or `docker stop <container>`) before the next image or section 2. Runners started with the same key are the same runner to Offloadr, so two running at once would take turns answering for it and spoil the remaining checks.

## 2. Desktop app

In a PowerShell window, set `$env:OFFLOADR_RUNNER_DATA_DIRECTORY` to an empty folder and start every copy of the app from that window (`& <folder>\OffloadrRunner.exe`), so your own settings and key stay untouched. A copy started from the Start menu does not get the variable: it would use your own settings and would not find the test copy.

This section checks the app itself, whichever image it runs. That image is the one the environment's launch plan names when it provides one (the Status page's "Chosen by" row says which), otherwise the published image for the channel. Only when it is the release image does the digest on the Status page have to match the publish run's.

1. On the Status page, Docker and NVIDIA GPU are green.
2. In Settings, enter the environment's address under Advanced, your runner key and an editor, then save. Start the runner. The image downloads, the state becomes Running, and the tray icon shows a green dot.
3. Press Check next to GPU inside containers. It turns green and reports the GPUs Docker can see.
4. Run a session and one prompt from the web app, as in steps 3 to 5 of section 1. The Status page shows the session as in use.
5. Change the editor while the session runs: in Settings, or, when the environment provides launch plans, by switching your active workspace to the other editor. The app waits, says a restart is pending, and switches images only after you stop the session.
6. Close the window. The app stays in the notification area, and the runner keeps working.
7. With the app still in the notification area, start it again from the same PowerShell window. The open copy's window comes to the front, and the second launch quits without a second tray icon.
8. With a session running, choose Quit from the tray menu. The app warns that the session will be interrupted; after you confirm, `docker ps` no longer lists `offloadr-runner`.
9. Open the app again from the same window, start the runner, and end the app from Task Manager. Started again from that window, the app reconnects to the running container instead of starting a second one. Then choose Quit from the tray menu, so no test runner keeps running with the shared key.
10. Starting at sign-in is offered only without `OFFLOADR_RUNNER_DATA_DIRECTORY`, so a test build cannot take over your own sign-in entry. Check it in a separate Windows account kept for testing, whose app settings and sign-in entries are its own, not in yours: there, start the app normally with the test environment's address and key, turn on starting at sign-in, sign out and back in: the app opens in the notification area. Turn it off under Startup apps in Task Manager: the switch in Settings shows it off. Turn the switch on and save: Task Manager shows the app enabled again. Turn it off and save: Startup apps no longer lists the app. Quit the app.

## 3. Automated checks worth running here

Two opt-in tests need a real Docker engine or a real control plane, which CI does not have.

The real-Docker test runs the supervisor, which downloads its image before starting it. Give it a published digest; a local build only under a name no registry can serve, such as `docker tag <local-build> offloadr.invalid/runner-comfyui:candidate` (`.invalid` never resolves), since a channel tag would be replaced by the published image and a name without a registry means Docker Hub. The supervisor runs the local copy when the download fails:

```bash
RUNNER_REAL_DOCKER_IMAGE=<image> dotnet test tests/Offloadr.Runner.Supervisor.Tests --filter TestCategory=RealDocker
```

```bash
read -rs RUNNER_REAL_KEY && export RUNNER_REAL_KEY
RUNNER_REAL_API_ORIGIN=<environment-origin> dotnet test tests/Offloadr.Runner.Supervisor.Tests --filter TestCategory=RealControlPlane
```
