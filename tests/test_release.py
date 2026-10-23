import copy
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location("release", Path(__file__).resolve().parents[1] / "scripts/release.py")
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


def metadata():
    return {"repository": "camel-exvl/pcl-ce-patch", "upstreamRepository": "PCL-Community/PCL-CE",
            "upstreamTag": "v2.15.1", "upstreamCommit": "a" * 40, "version": "2.15.1-restored.1",
            "releaseTag": "v2.15.1-restored.1", "revision": 1, "publishedAt": "2026-10-20T12:47:40Z",
            "upstreamNotes": "Upstream release notes", "feedBase": release.feed_base("camel-exvl/pcl-ce-patch"),
            "assets": {"official.exe": {"sha256": "b" * 64}}}


def assets():
    return {arch: {"zipName": f"PCL_CE_Restore_{arch}.zip", "versionCode": 524,
                   "sha256": "1" * 64,
                   "url": f"https://github.com/camel-exvl/pcl-ce-patch/releases/download/"
                          f"v2.15.1-restored.1/PCL_CE_Restore_{arch}.zip"} for arch in release.ARCHES}


def zip_bytes(files):
    data = io.BytesIO()
    release.write_zip(data, files)
    return data.getvalue()


class ReleaseTests(unittest.TestCase):
    def test_both_protocol_channels_use_correct_stable_architecture(self):
        files = release.build_feed(metadata(), assets(), {"content": [{"id": "official-notice"}]})
        self.assertEqual(len(files), 5)
        for arch in release.ARCHES:
            for channel in ("sr", "fr"):
                item = json.loads(files[f"apiv2/updates/updates-{channel}{arch.lower()}.json"])["assets"][0]
                self.assertEqual(item["downloads"], [assets()[arch]["url"]])
                self.assertEqual(item["sha256"], assets()[arch]["sha256"])
                self.assertEqual(item["patches"], [])
        self.assertEqual(json.loads(files["apiv2/announcement.json"])["content"][1]["id"], "official-notice")
        self.assertEqual(release.validate_feed_archive(zip_bytes(files), metadata()), files)

    def test_archive_preserves_exact_executable_and_is_reproducible(self):
        content = {"Plain Craft Launcher Community Edition.exe": b"MZ-test-patched-executable"}
        first, second = zip_bytes(content), zip_bytes(content)
        self.assertEqual(first, second)
        with zipfile.ZipFile(io.BytesIO(first)) as archive:
            self.assertEqual(archive.read(next(iter(content))), next(iter(content.values())))
        self.assertNotEqual(release.sha256(first), release.sha256(next(iter(content.values()))))

    def test_patched_revision_order_uses_numeric_identifiers(self):
        self.assertLess(release.version_key("2.15.1-restored.9"), release.version_key("2.15.1-restored.10"))
        self.assertLess(release.version_key("2.15.1-restored.99"), release.version_key("2.15.2-restored.1"))
        with self.assertRaises(ValueError):
            release.version_key("2.15.1+restored.1")

    def test_feed_rejects_wrong_provenance_and_paths(self):
        files = release.build_feed(metadata(), assets(), {"content": []})
        wrong = copy.deepcopy(metadata())
        wrong["upstreamCommit"] = "c" * 40
        with self.assertRaisesRegex(ValueError, "provenance"):
            release.validate_feed_archive(zip_bytes(files), wrong)
        files["../escape.json"] = b"{}"
        with self.assertRaisesRegex(ValueError, "Unexpected files"):
            release.validate_feed_archive(zip_bytes(files), metadata())

    def test_feed_rejects_stale_cache_and_upstream_fallback(self):
        files = release.build_feed(metadata(), assets(), {"content": []})
        files["apiv2/announcement.json"] = b'{"content":[]}'
        with self.assertRaisesRegex(ValueError, "cache digest"):
            release.validate_feed_archive(zip_bytes(files), metadata())
        altered = assets()
        altered["x64"]["url"] = "https://github.com/PCL-Community/PCL-CE/releases/latest/download/official.exe"
        files = release.build_feed(metadata(), altered, {"content": []})
        with self.assertRaisesRegex(ValueError, "exclusively"):
            release.validate_feed_archive(zip_bytes(files), metadata())

    def test_published_release_repair_does_not_rebuild_or_overwrite_assets(self):
        data = zip_bytes(release.build_feed(metadata(), assets(), {"content": []}))
        existing = {"draft": False, "assets": [{"name": "update-feed.zip", "browser_download_url":
                    "https://github.com/example/feed.zip", "digest": "sha256:" + release.sha256(data)}]}
        with patch.object(release.GitHub, "api", return_value=existing) as api, \
                patch.object(release, "fetch", return_value=data), patch.object(release, "publish_feed") as advance:
            release.publish(metadata(), None)
            self.assertEqual(api.call_count, 1)
            advance.assert_called_once()

    def test_credentials_cannot_be_sent_to_arbitrary_api_hosts(self):
        with self.assertRaisesRegex(ValueError, "non-GitHub"):
            release.GitHub("owner/repo").api("https://example.invalid/steal")

    def test_update_feed_is_a_single_non_forced_branch_advance(self):
        calls = []
        def api(path, **kwargs):
            calls.append((path, kwargs))
            return {"git/ref/heads/updates": {"object": {"sha": "parent"}},
                    "contents/state.json?ref=updates": None, "git/trees": {"sha": "tree"},
                    "git/commits": {"sha": "commit"}, "git/refs/heads/updates": {}}[path]
        client = release.GitHub("owner/repo")
        with patch.object(client, "api", side_effect=api):
            release.publish_feed(client, release.build_feed(metadata(), assets(), {"content": []}))
        self.assertEqual(calls[-1], ("git/refs/heads/updates", {"method": "PATCH", "data": {"sha": "commit", "force": False}}))
        self.assertEqual(calls[-2][1]["data"]["parents"], ["parent"])

    def test_equivalent_source_diff_preserves_other_updater_initializers(self):
        source = {
            "Plain Craft Launcher 2/Modules/Minecraft/RestrictedFeaturePolicy.cs": "original policy\n",
            "Plain Craft Launcher 2/Modules/Updates/UpdateManager.cs":
                "    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>\n"
                "    {\n        originalSource\n    });\n    public static object serverLoader = KeepMe();\n",
            "Plain Craft Launcher 2/metadata.json": json.dumps({"name": "original", "version": {"base": "2.15.1", "suffix": ""}})}
        stream = io.BytesIO()
        with tarfile.open(fileobj=stream, mode="w:gz") as archive:
            for path, content in source.items():
                entry = tarfile.TarInfo("root/" + path)
                entry.size = len(content.encode())
                archive.addfile(entry, io.BytesIO(content.encode()))
        diff = release.source_diff(metadata(), stream.getvalue()).decode()
        self.assertIn("serverLoader = KeepMe()", diff)
        self.assertNotIn("-    public static object serverLoader", diff)
        self.assertIn("+    internal static bool IsRestricted => false;", diff)


if __name__ == "__main__":
    unittest.main()
