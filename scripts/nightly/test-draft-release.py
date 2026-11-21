"""Offline checks for the draft-only build transfer boundary."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("draft-release.sh").resolve()


class DraftReleaseTests(unittest.TestCase):
    def run_transfer(self, draft, *arguments):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = root / "calls.jsonl"
            fake = root / "gh"
            fake.write_text("""#!/usr/bin/env python3
import json, os, sys
with open(os.environ['CALL_LOG'], 'a') as log:
    log.write(json.dumps(sys.argv[1:]) + '\\n')
if sys.argv[1:3] == ['release', 'view']:
    print(json.dumps({'isDraft': json.loads(os.environ['DRAFT_STATE'])}))
""")
            fake.chmod(0o755)
            result = subprocess.run(["bash", str(SCRIPT), *arguments], capture_output=True, text=True,
                                    env={**os.environ, "PATH": str(root) + os.pathsep + os.environ["PATH"],
                                         "GITHUB_REPOSITORY": "example/repo", "NIGHTLY_TAG": "private-test",
                                         "GH_TOKEN": "offline-test-token", "CALL_LOG": str(log),
                                         "DRAFT_STATE": json.dumps(draft)})
            calls = [json.loads(line) for line in log.read_text().splitlines()]
            return result, calls

    def test_published_release_rejects_every_transfer_before_mutation(self):
        for operation in [("upload", "package.rpm"), ("download", "out", "*.rpm"),
                          ("notes", "notes.md"), ("remove-web",)]:
            with self.subTest(operation=operation):
                result, calls = self.run_transfer(False, *operation)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("not a draft", result.stderr)
                self.assertTrue(all(call[0:2] == ["release", "view"] for call in calls))

    def test_upload_is_bracketed_by_draft_checks(self):
        result, calls = self.run_transfer(True, "upload", "package.rpm")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["view", "upload", "view"], [call[1] for call in calls])
        self.assertIn("package.rpm", calls[1])

    def test_download_uses_authenticated_release_assets(self):
        result, calls = self.run_transfer(True, "download", "out", "*.rpm")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("download", calls[1][1])
        self.assertIn("--repo", calls[1])
        self.assertIn("*.rpm", calls[1])

    def test_notes_update_explicitly_keeps_draft(self):
        result, calls = self.run_transfer(True, "notes", "notes.md")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("--draft", calls[1])
        self.assertNotIn("--draft=false", calls[1])

    def test_unknown_operation_cannot_publish(self):
        result, calls = self.run_transfer(True, "publish")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(calls))


if __name__ == "__main__":
    unittest.main()
