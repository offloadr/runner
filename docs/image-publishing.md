# Publishing runner images

`Publish runner images` builds the channels it is given, or all four, from its
exact source revision. It runs manually or when an automated dispatch requests
the channels whose editor image moved. Each channel is built on
its editor image's current digest, recorded in the `org.opencontainers.image.base.name`
and `org.opencontainers.image.base.digest` labels. A release is complete only after
registry verification confirms the build digests, channel and commit tags, the
source/revision/license labels, the recorded editor image, and the signature and
build provenance described below.

## Signatures and provenance

Every published digest is signed with [cosign](https://docs.sigstore.dev/) using
keyless signing: the certificate is issued to the publishing workflow run, names
the workflow, its Git ref and the commit, and the signature is recorded in the
public Rekor transparency log. No signing key exists. The same run adds a
[GitHub build provenance attestation](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations)
(SLSA v1) to the digest. Both are stored next to the image in the registry.

Releases are published from `main`. To check an image with cosign 3 or later and
the GitHub CLI, give its digest, or a tag, which checks the digest the tag points
at now:

```bash
cosign verify \
  --certificate-identity https://github.com/offloadr/runner/.github/workflows/publish-images.yml@refs/heads/main \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  ghcr.io/offloadr/runner-comfyui:latest

gh attestation verify oci://ghcr.io/offloadr/runner-comfyui:latest \
  --repo offloadr/runner \
  --signer-workflow offloadr/runner/.github/workflows/publish-images.yml \
  --source-ref refs/heads/main \
  --deny-self-hosted-runners
```

Add `--certificate-github-workflow-sha <commit>` to `cosign verify`, or
`--source-digest <commit>` to `gh attestation verify`, to require a particular
commit; the `org.opencontainers.image.revision` label names it. Signatures and
attestations belong to the digest, so a channel restored to an earlier digest
keeps that digest's own signature and provenance. Images published before
signing was introduced have neither.

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
