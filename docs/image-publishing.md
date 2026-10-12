# Publishing runner images

`Publish runner images` builds all four channels from its exact source revision.
It runs manually or after an `editor-image-updated` repository dispatch. A release
is complete only after registry verification confirms the build digests, channel
and commit tags, and source/revision/license labels.

Every publication, including automatic dispatches, first records the current
digest of every channel in the **Record previous channel digests** job summary
and the `previous-runner-channels` workflow artifact. All four reads and the
artifact upload must succeed before any image can be pushed:

- `ghcr.io/offloadr/runner-comfyui:latest`
- `ghcr.io/offloadr/runner-comfyui:master`
- `ghcr.io/offloadr/runner-forge-neo:latest`
- `ghcr.io/offloadr/runner-forge-neo:neo`

If qualification fails, stop further publication and wait for active publishing
jobs to finish or cancel them before restoring the channels. Download the snapshot
from the publication being reverted. Run `Restore runner image channels` from
`main`, supplying all four recorded `sha256:` digests. It
checks that every source manifest exists before writing, preserves the manifests,
and verifies each restored channel. Publication and restoration share a
concurrency group with `queue: max`, so a later dispatch does not replace a
pending rollback. Check queued runs too: a later publication can change the tags
again after restoration finishes.

If restoration fails partway through, inspect the channels and rerun with the same
recorded digests. The operation is repeatable. Commit tags remain available for
diagnosis. Workloads already pinned to a digest retain that digest; restoring a
channel does not restart or replace them.
