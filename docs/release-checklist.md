# Release checklist: GPU checks

CI has no GPUs, so these checks are run by hand on a machine with an NVIDIA GPU for each release of the runner's own code. Everything else, including the model VFS shim and real aria2 transfers, is covered by CI.

GPU power limits are not on this list: changing them does not work on any host yet, so there is nothing to confirm.

Use a non-production Offloadr environment and a personal runner key created there. Record the commit, the image digests and anything that failed in the release notes or the pull request.

## What it is, and what it is not

The `Publish runner images` workflow moves the channel tags people download as soon as it runs, and it also runs on its own whenever an editor image updates upstream. This list is therefore no gate in front of every published image. An editor-triggered run also builds the runner code on `main` as it is at that moment, so runner changes merged to `main` go out with the next editor update even before anyone checked them: run this list soon after runner changes land on `main`, or before merging the larger ones. When a check fails, publish a fix; people who need to can meanwhile pick another editor channel.

## You need

- A Windows machine with an NVIDIA GPU, a current driver, and Docker Desktop on the WSL 2 backend. A Linux host with the NVIDIA Container Toolkit works for the container checks.
- The four images of the release commit. Preferably the published ones, by the digests in the summary of the `Publish runner images` run for that commit (`ghcr.io/offloadr/<repository>@sha256:<digest>`): exactly what personal and hosted runners get. To check before publishing, build them locally instead with `SOURCE_REVISION=$(git rev-parse HEAD) docker buildx bake --pull --load`; `--pull` builds on the current editor images, but it is still a rebuild, so check the published digests too once they exist.
- The desktop app built from the same commit: the folder from `build/publish-windows.ps1` (`artifacts/packages/windows-win-x64`), and for the installer steps the CI artifact or `build/publish-windows.ps1 -Pack` (`artifacts/releases/windows-win-x64`).

## 1. Container runner

Repeat for each editor you release: ComfyUI `latest` and `master`, Forge Neo `neo` and `latest`.

1. Start the runner from the image: a published one by its digest, which always names exactly that build, or a local build by its tag (`ghcr.io/offloadr/runner-comfyui:latest`, say) with `--pull never`, so Docker does not replace it with the published one. Check that its `org.opencontainers.image.revision` label (`docker image inspect`) names the release commit. The key is read without echo and passed by name only, so it stays out of the command line and the shell history:

   ```bash
   read -rs RUNNER_SECRET && export RUNNER_SECRET
   docker run --rm -it --gpus all -e OFFLOADR_API_GRPC=<environment-origin> -e RUNNER_SECRET -e RUNNER_MODEL_DOWNLOADS=on <image>
   ```

2. The log shows `Status update accepted=True` and `Runner commands stream (grpc) connected` within a few seconds, and the source notice for this repository.
3. In the web app, the runner shows as your personal runner and is offered for a workspace on the matching editor, and not for the other editor.
4. Start a GPU session on it. While the session runs, `docker exec <container> cat /run/offloadr/status.json` shows its `active_session_id`.
5. Run one prompt that needs a model the runner has not downloaded yet. It finishes, the output appears in the editor, and the log shows the download progressing.
6. Run a second prompt with the same model. It starts without downloading again.
7. Stop the session from the web app. The container keeps running, and the status file's `active_session_id` is empty again.
   Once, not in the sealed repeat below: restart the runner without `-e RUNNER_MODEL_DOWNLOADS=on`, start a session and run a prompt that needs a model the runner does not have. The log says model downloads are off, nothing is downloaded, and the prompt is refused with a message naming the model.
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
11. Updating. The installer always installs to the account's own `%LOCALAPPDATA%\app.offloadr.runner`, replacing an installed copy there, so run this step in the separate test account from step 10, not in yours. Pack an older and a newer version: `build/publish-windows.ps1 -Version 0.0.1 -Pack`, copy `artifacts/releases/windows-win-x64` to a scratch folder, then `build/publish-windows.ps1 -Version 0.0.2 -Pack`. In the same PowerShell window, also set `$env:OFFLOADR_RUNNER_UPDATE_FEED` to the full path of `artifacts\releases\windows-win-x64` and run the scratch folder's `app.offloadr.runner-win-x64-Setup.exe`. Start the runner and a session. About 15 seconds after the app opens, the banner downloads 0.0.2 and offers Restart to update; restart: the app comes back as 0.0.2 (the file properties of the installed `OffloadrRunner.exe` show it), reconnects to the runner, and the session goes on. Launch the app once more while it runs: the open copy's window comes up and nothing is replaced. Uninstall it from Installed apps in Windows Settings: the data folder stays.

## Signing the desktop app

The `Release desktop app` workflow signs the installer and the app's own files with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) when it is configured, and publishes an unsigned release, saying so in its notes, when it is not. Its `Check the signatures` step fails a signed release with any unsigned file, and an unsigned one with any signed file, and lists every file and its signer in the run summary. Signing needs no stored secret: the job signs in to Azure with GitHub's OpenID Connect token.

To set it up once:

1. In Azure, create an Artifact Signing account and a public-trust certificate profile, which needs the organisation's identity validation. The certificate's subject is the publisher Windows shows.
2. In Microsoft Entra ID, register an application, give it the *Artifact Signing Certificate Profile Signer* role on the certificate profile, and add a federated credential for GitHub Actions with audience `api://AzureADTokenExchange`, entity type *Environment* and environment `release`. The subject must be exactly what this repository's tokens carry; repositories created after 15 July 2026 use GitHub's immutable format with owner and repository IDs. If unsure, read the `sub` claim of one run with [actions-oidc-debugger](https://github.com/github/actions-oidc-debugger).
3. In the repository's `release` environment (created by the first release run, or under Settings > Environments), add the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` from the application, and `ARTIFACT_SIGNING_ENDPOINT` (such as `https://weu.codesigning.azure.net/`), `ARTIFACT_SIGNING_ACCOUNT` and `ARTIFACT_SIGNING_PROFILE`. Optionally add `ARTIFACT_SIGNING_SUBJECT`, part of the certificate subject such as the organisation name, which the check then also requires. Required reviewers on the environment make each release wait for an approval before it signs.

To sign or check locally, sign in with `az login`, write the same three values to a `metadata.json` (`Endpoint`, `CodeSigningAccountName`, `CertificateProfileName`) and run `build/publish-windows.ps1 -Pack -AzureTrustedSignFile metadata.json`, then `build/verify-windows-signatures.ps1 -ReleasesDir artifacts/releases/windows-win-x64 -Expect Signed`.

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
