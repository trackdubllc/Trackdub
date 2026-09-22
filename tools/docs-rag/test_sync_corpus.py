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


class RefreshTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        (self.repo / "guide.md").write_text("new guide", encoding="utf-8")
        self.staging = self.root / ".staging"
        self.staging.mkdir()
        (self.staging / "old.md").write_text("last complete snapshot", encoding="utf-8")
        self.manifest = {
            "bucket": "corpus", "aiSearchInstance": "docs", "maxBytes": 3500000,
            "repos": [{"id": "core", "rootEnv": "TEST_DOCS_ROOT",
                       "defaultFromTrackdub": str(self.repo),
                       "prefix": "first-party/core", "include": ["*.md"]}],
            "vendors": [{"id": "vendor", "prefix": "vendor/example",
                         "sources": [["guide", "https://example.com/guide"]]}],
        }

    def run_refresh(self, *args, fetch_result="vendor guide", fetch_error=None):
        with patch.object(sync_corpus, "STAGING", self.staging), \
             patch.object(sync_corpus, "load_manifest", return_value=self.manifest), \
             patch.dict(sync_corpus.os.environ, {"TEST_DOCS_ROOT": str(self.repo)}), \
             patch.object(sync_corpus, "fetch_url", return_value=fetch_result, side_effect=fetch_error) as fetch, \
             patch.object(sync_corpus, "upload", return_value=0) as upload, \
             patch.object(sync_corpus, "reindex", return_value=0) as reindex, \
             patch("sys.argv", ["sync_corpus.py", *args]):
            try:
                sync_corpus.main()
                code = 0
            except SystemExit as stopped:
                code = stopped.code
        return code, fetch, upload, reindex

    def assert_failed_refresh(self, **kwargs):
        code, _, upload, reindex = self.run_refresh("--upload", **kwargs)
        self.assertNotEqual(code, 0)
        upload.assert_not_called()
        reindex.assert_not_called()
        self.assertEqual(sorted(p.name for p in self.staging.iterdir()), ["old.md"])
        self.assertEqual((self.staging / "old.md").read_text(encoding="utf-8"), "last complete snapshot")

    def test_missing_repo_preserves_cache_and_prevents_upload(self):
        self.repo = self.root / "missing"
        self.assert_failed_refresh()

    def test_repo_without_matching_docs_prevents_partial_refresh(self):
        self.manifest["repos"][0]["include"] = ["*.missing"]
        self.assert_failed_refresh()

    def test_failed_vendor_fetch_preserves_cache_and_prevents_upload(self):
        self.assert_failed_refresh(fetch_error=TimeoutError("fetch timed out"))

    def test_empty_vendor_document_preserves_cache_and_prevents_upload(self):
        self.assert_failed_refresh(fetch_result="")

    def test_failed_first_refresh_leaves_no_reusable_partial_cache(self):
        self.staging = self.root / "fresh-cache"
        code, _, upload, reindex = self.run_refresh("--upload", fetch_error=TimeoutError())
        self.assertNotEqual(code, 0)
        self.assertFalse(self.staging.exists())
        upload.assert_not_called()
        reindex.assert_not_called()

    def test_successful_refresh_replaces_snapshot(self):
        code, _, upload, reindex = self.run_refresh("--upload")
        self.assertEqual(code, 0)
        self.assertFalse((self.staging / "old.md").exists())
        self.assertEqual((self.staging / "first-party/core/guide.md").read_text(), "new guide")
        self.assertEqual((self.staging / "vendor/example/guide.md").read_text(),
                         "Source: https://example.com/guide\n\nvendor guide\n")
        self.assertTrue((self.staging / "first-party/trackdub/docs/reference/docs-rag-pin.md").is_file())
        upload.assert_called_once()
        self.assertEqual(upload.call_args.args[0], self.staging)
        reindex.assert_called_once()

    def test_skip_fetch_intentionally_promotes_repo_only_snapshot(self):
        code, fetch, upload, reindex = self.run_refresh("--skip-fetch", "--upload")
        self.assertEqual(code, 0)
        fetch.assert_not_called()
        self.assertTrue((self.staging / "first-party/core/guide.md").is_file())
        self.assertFalse((self.staging / "vendor").exists())
        upload.assert_called_once()
        reindex.assert_called_once()

    def test_failed_promotion_restores_previous_snapshot(self):
        original = Path.rename

        def rename(path, target):
            if path.name == "candidate":
                raise OSError("promotion failed")
            return original(path, target)

        with patch.object(Path, "rename", rename):
            self.assert_failed_refresh()

    def test_failed_rollback_does_not_delete_previous_snapshot(self):
        original = Path.rename

        def rename(path, target):
            if Path(target) == self.staging:
                raise OSError("target unavailable")
            return original(path, target)

        with patch.object(Path, "rename", rename):
            code, _, upload, reindex = self.run_refresh("--upload")
        self.assertNotEqual(code, 0)
        preserved = list(self.root.rglob("old.md"))
        self.assertEqual(len(preserved), 1)
        self.assertEqual(preserved[0].read_text(), "last complete snapshot")
        upload.assert_not_called()
        reindex.assert_not_called()

    def test_staging_only_refresh_also_fails_for_missing_sources(self):
        code, _, upload, reindex = self.run_refresh(fetch_error=TimeoutError())
        self.assertNotEqual(code, 0)
        self.assertEqual((self.staging / "old.md").read_text(), "last complete snapshot")
        upload.assert_not_called()
        reindex.assert_not_called()


if __name__ == "__main__":
    unittest.main()
