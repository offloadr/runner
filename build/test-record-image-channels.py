"""Verify that rollback snapshots capture every channel before publication."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class RecordChannelsTests(unittest.TestCase):
    refs = [
        "ghcr.io/offloadr/runner-comfyui:latest",
        "ghcr.io/offloadr/runner-comfyui:master",
        "ghcr.io/offloadr/runner-forge-neo:latest",
        "ghcr.io/offloadr/runner-forge-neo:neo",
    ]

    def run_record(self, **extra_env):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            mock = root / "skopeo"
            mock.write_text('''#!/usr/bin/env python3
import hashlib, json, os, pathlib, sys
args = sys.argv[1:]
if args[:2] == ["inspect", "--raw"]:
    ref = args[-1].removeprefix("docker://")
    if ref == os.environ.get("MISSING_CHANNEL"):
        sys.exit(1)
    print(json.dumps({"mediaType": "application/vnd.oci.image.index.v1+json", "ref": ref,
                      "manifests": [{"digest": "sha256:" + "f" * 64}]}))
elif args[0] == "manifest-digest":
    print("invalid" if os.environ.get("INVALID_DIGEST") else
          "sha256:" + hashlib.sha256(pathlib.Path(args[1]).read_bytes()).hexdigest())
else:
    sys.exit(2)
''')
            mock.chmod(0o755)
            output = root / "channels.json"
            result = subprocess.run(
                ["bash", str(Path(__file__).with_name("record-image-channels.sh")), str(output)],
                env={**os.environ, "PATH": directory + os.pathsep + os.environ["PATH"], **extra_env},
                text=True, capture_output=True, check=False)
            return result, json.loads(output.read_text()) if output.exists() else None

    def test_captures_all_top_level_manifests_and_summary(self):
        result, snapshot = self.run_record()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(list(snapshot), self.refs)
        for ref in self.refs:
            manifest = json.dumps({"mediaType": "application/vnd.oci.image.index.v1+json", "ref": ref,
                                   "manifests": [{"digest": "sha256:" + "f" * 64}]}) + "\n"
            expected = "sha256:" + hashlib.sha256(manifest.encode()).hexdigest()
            self.assertEqual(snapshot[ref], expected)
            self.assertIn(f"| `{ref}` | `{expected}` |", result.stdout)

    def test_missing_final_channel_does_not_publish_partial_snapshot(self):
        result, snapshot = self.run_record(MISSING_CHANNEL=self.refs[-1])
        self.assertNotEqual(result.returncode, 0)
        self.assertIsNone(snapshot)
        self.assertEqual(result.stdout, "")

    def test_invalid_digest_does_not_publish_snapshot(self):
        result, snapshot = self.run_record(INVALID_DIGEST="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIsNone(snapshot)
        self.assertEqual(result.stdout, "")


if __name__ == "__main__":
    unittest.main()
