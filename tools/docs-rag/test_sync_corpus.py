import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import sync_corpus


class UploadTests(unittest.TestCase):
    def test_upload_uses_metadata_helper(self):
        with patch.object(sync_corpus.subprocess, "run", return_value=subprocess.CompletedProcess([], 0)) as run:
            self.assertEqual(sync_corpus.upload(Path("staging"), "corpus", Path("api"), workers=3), 0)
        run.assert_called_once()
        command = run.call_args.args[0]
        self.assertEqual(command[0], "node")
        self.assertEqual(Path(command[1]).name, "upload_corpus.mjs")
        self.assertEqual(command[2:], ["api", "staging", "corpus", "3"])

    def test_upload_reindexes_only_after_success(self):
        self.run_main(upload_status=0, reindex_status=0, expected=0)

    def test_failed_upload_does_not_reindex(self):
        self.run_main(upload_status=1, reindex_status=0, expected=1)

    def test_failed_reindex_fails_command(self):
        self.run_main(upload_status=0, reindex_status=1, expected=1)

    def test_failed_reindex_checks_job_state_before_retry(self):
        with patch.object(sync_corpus.subprocess, "run", return_value=subprocess.CompletedProcess([], 1)), \
             patch("builtins.print") as output:
            self.assertEqual(sync_corpus.reindex(Path("api"), "corpus"), 1)
        output.assert_called_once_with(
            "Upload succeeded but reindex request failed; check ai-search jobs list before retrying because the job may have started.",
            flush=True,
        )

    def run_main(self, upload_status, reindex_status, expected):
        with tempfile.TemporaryDirectory() as root:
            staging = Path(root)
            (staging / "doc.md").write_text("doc", encoding="utf-8")
            manifest = {"bucket": "corpus", "aiSearchInstance": "instance-from-manifest"}
            with patch.object(sync_corpus, "STAGING", staging), \
                 patch.object(sync_corpus, "load_manifest", return_value=manifest), \
                 patch.object(sync_corpus, "upload", return_value=upload_status), \
                 patch.object(sync_corpus.subprocess, "run", return_value=subprocess.CompletedProcess([], reindex_status)) as run, \
                 patch("sys.argv", ["sync_corpus.py", "--reuse-staging", "--upload"]):
                with self.assertRaises(SystemExit) as stopped:
                    sync_corpus.main()
                self.assertEqual(stopped.exception.code, expected)
            if upload_status:
                run.assert_not_called()
            else:
                run.assert_called_once()
                command = run.call_args.args[0]
                self.assertIn("ai-search", command)
                self.assertEqual(command[-4:], ["jobs", "create", "instance-from-manifest", "--json"])


if __name__ == "__main__":
    unittest.main()
