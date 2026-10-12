"""Exercise rollback ordering and digest verification without registry writes."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


class RestoreChannelsTests(unittest.TestCase):
    def run_restore(self, digests=None, **extra_env):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            mock = root / "skopeo"
            mock.write_text('''#!/usr/bin/env python3
import json, os, pathlib, sys
root = pathlib.Path(os.environ["MOCK_REGISTRY"])
args = sys.argv[1:]
with (root / "calls").open("a") as log:
    log.write(json.dumps(args) + "\\n")
state_path = root / "state"
state = json.loads(state_path.read_text()) if state_path.exists() else {}
if args[0] == "copy":
    state[args[-1]] = args[-2].split("@", 1)[1]
    state_path.write_text(json.dumps(state))
elif args[0] == "inspect":
    ref = args[-1]
    if "@" in ref:
        digest = ref.split("@", 1)[1]
        if digest == os.environ.get("MISSING_DIGEST"):
            sys.exit(1)
        print(digest)
    else:
        print("sha256:" + "0" * 64 if os.environ.get("WRONG_CHANNEL") else state[ref])
else:
    sys.exit(2)
''')
            mock.chmod(0o755)
            result = subprocess.run(
                ["bash", str(Path(__file__).with_name("restore-image-channels.sh")),
                 *(digests if digests is not None else self.digests)],
                env={**os.environ, "PATH": directory + os.pathsep + os.environ["PATH"],
                     "MOCK_REGISTRY": directory, **extra_env},
                text=True, capture_output=True, check=False)
            calls = [json.loads(line) for line in (root / "calls").read_text().splitlines()] if (root / "calls").exists() else []
            return result, calls

    digests = ["sha256:" + digit * 64 for digit in "1234"]

    def test_requires_all_four_digests(self):
        result, calls = self.run_restore([])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [])

    def test_validates_all_inputs_before_registry_access(self):
        result, calls = self.run_restore(self.digests[:3] + ["invalid"])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(calls, [])

    def test_missing_source_prevents_every_channel_write(self):
        result, calls = self.run_restore(MISSING_DIGEST=self.digests[3])
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[0] == "copy" for call in calls))

    def test_restores_and_verifies_all_four_channels(self):
        result, calls = self.run_restore()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual([call[0] for call in calls], ["inspect"] * 4 + ["copy", "inspect"] * 4)
        self.assertEqual([call[-1] for call in calls if call[0] == "copy"], [
            "docker://ghcr.io/offloadr/runner-comfyui:latest",
            "docker://ghcr.io/offloadr/runner-comfyui:master",
            "docker://ghcr.io/offloadr/runner-forge-neo:latest",
            "docker://ghcr.io/offloadr/runner-forge-neo:neo",
        ])

    def test_registry_mismatch_stops_remaining_writes(self):
        result, calls = self.run_restore(WRONG_CHANNEL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sum(call[0] == "copy" for call in calls), 1)


if __name__ == "__main__":
    unittest.main()
