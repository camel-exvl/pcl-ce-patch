"""Release orchestration. Only GitHub's API receives the workflow token.

All downloads are public; service credentials are never extracted from PCL.
"""
from __future__ import annotations

import argparse
import difflib
import hashlib
import io
import json
import os
from pathlib import Path
import re
import sys
import tarfile
import urllib.error
import urllib.parse
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
ARCHES = ("x64",)
API = "https://api.github.com"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def json_bytes(value):
    return (json.dumps(value, ensure_ascii=False, indent=2) + "\n").encode("utf-8")


def write_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(json_bytes(value))


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def fetch(url):
    require(urllib.parse.urlparse(url).scheme == "https", "Downloads must use HTTPS")
    request = urllib.request.Request(url, headers={"User-Agent": "pcl-ce-patch"})
    with urllib.request.urlopen(request, timeout=90) as response:
        return response.read()


class GitHub:
    def __init__(self, repository):
        require(re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository), "Invalid repository")
        self.repository = repository

    def api(self, path, *, method="GET", data=None, missing=False, content_type=None):
        url = path if path.startswith("https://") else f"{API}/repos/{self.repository}" + ("/" + path if path else "")
        require(urllib.parse.urlparse(url).hostname in ("api.github.com", "uploads.github.com"),
                "Refusing to send workflow credentials to a non-GitHub API host")
        headers = {"User-Agent": "pcl-ce-patch", "Accept": "application/vnd.github+json",
                   "X-GitHub-Api-Version": "2022-11-28"}
        if token := os.environ.get("GITHUB_TOKEN"):
            headers["Authorization"] = f"Bearer {token}"
        if data is not None:
            if not isinstance(data, bytes):
                data = json_bytes(data)
            headers["Content-Type"] = content_type or "application/json"
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                body = response.read()
                return json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            if missing and error.code == 404:
                return None
            raise ValueError(f"GitHub API {method} {urllib.parse.urlparse(url).path} failed with HTTP {error.code}") from None


def feed_base(repository):
    return f"https://raw.githubusercontent.com/{repository}/updates/"


def asset_names(arch):
    return f"PCL2_CE_Release_{arch}.exe"


def version_key(version):
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)-restored\.(\d+)", version)
    require(match is not None, "Invalid patch version")
    return tuple(map(int, match.groups()))


def discover(config, tag=None):
    upstream = GitHub(config["upstreamRepository"])
    release = upstream.api("releases/tags/" + urllib.parse.quote(tag, safe="") if tag else "releases/latest")
    require(not release["draft"] and not release["prerelease"], "Only published stable releases are supported")
    tag = release["tag_name"]
    require(re.fullmatch(r"v\d+\.\d+\.\d+", tag), "Unrecognized upstream stable tag")
    revision = config["patchRevision"]
    require(type(revision) is int and revision > 0, "patchRevision must be a positive integer")
    version = f"{tag[1:]}-restored.{revision}"
    own_tag = "v" + version
    assets = {asset["name"]: asset for asset in release["assets"]}
    selected = {}
    for arch in ARCHES:
        for name in (asset_names(arch), asset_names(arch) + ".sigstore.json"):
            require(name in assets, f"Official release is missing {name}")
            asset = assets[name]
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", asset.get("digest") or ""),
                    f"Missing official SHA-256 digest for {name}")
            require(asset["browser_download_url"].startswith(
                f"https://github.com/{config['upstreamRepository']}/releases/download/{tag}/"),
                "Unexpected official asset location")
            selected[name] = {"url": asset["browser_download_url"], "sha256": asset["digest"][7:]}
    reference = upstream.api("git/ref/tags/" + urllib.parse.quote(tag, safe=""))["object"]
    while reference["type"] == "tag":
        reference = upstream.api("git/tags/" + reference["sha"])["object"]
    require(reference["type"] == "commit", "Release tag does not resolve to a commit")
    own = GitHub(config["repository"])
    existing = own.api("releases/tags/" + own_tag, missing=True)
    state = own.api("contents/state.json?ref=updates", missing=True)
    current = None
    if state:
        import base64
        current = json.loads(base64.b64decode(state["content"]))
        require(version_key(current["version"]) <= version_key(version),
                "Refusing to downgrade the published update feed")
    reuse = bool(existing and not existing["draft"])
    if reuse:
        names = {asset["name"] for asset in existing["assets"]}
        require({"update-feed.zip", "release-state.json", "SHA256SUMS"}.issubset(names),
                "Existing published release is incomplete; manual review required")
        if current and current.get("version") == version:
            require(current.get("officialAssets") == {name: item["sha256"] for name, item in selected.items()},
                    "Official assets changed under an existing patch release; increment patchRevision after review")
    return {"repository": config["repository"], "upstreamRepository": config["upstreamRepository"],
            "upstreamTag": tag, "upstreamCommit": reference["sha"],
            "publishedAt": release["published_at"], "upstreamNotes": release.get("body") or "",
            "version": version, "releaseTag": own_tag, "revision": revision,
            "feedBase": feed_base(config["repository"]), "assets": selected,
            "needed": not (reuse and current and current.get("version") == version),
            "reuseRelease": reuse}


def download(metadata, output):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    for name, asset in metadata["assets"].items():
        data = fetch(asset["url"])
        require(sha256(data) == asset["sha256"], f"Official asset digest mismatch: {name}")
        (output / name).write_bytes(data)
    print("Downloaded and SHA-256 checked the official x64 executable and Sigstore bundle")


def write_zip(path, files):
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data)


def build_feed(metadata, assets, announcements):
    require(isinstance(announcements.get("content"), list), "Invalid upstream announcement feed")
    files = {}
    for arch in ARCHES:
        item = assets[arch]
        for channel in ("sr", "fr"):
            # Both protocol channels deliberately track upstream stable releases.
            key = f"updates-{channel}{arch.lower()}"
            files[f"apiv2/updates/{key}.json"] = json_bytes({"assets": [{
                "file_name": item["zipName"],
                "version": {"channel": "stable", "name": metadata["version"], "code": item["versionCode"]},
                "upd_time": metadata["publishedAt"], "downloads": [item["url"]], "patches": [],
                "sha256": item["sha256"], "changelog": release_notes(metadata)}]})
    content = [{"title": f"PCL CE Restore {metadata['version']}",
                "detail": "第三方补丁版：恢复 Minecraft 安装、下载与修复；程序内更新仅使用补丁版。\n"
                          f"https://github.com/{metadata['repository']}/releases/tag/{metadata['releaseTag']}",
                "id": "pcl-ce-patch-" + metadata["version"], "date": metadata["publishedAt"]}]
    files["apiv2/announcement.json"] = json_bytes({"content": content + announcements["content"]})
    cache = {Path(path).stem: hashlib.md5(data, usedforsecurity=False).hexdigest()
             for path, data in files.items()}
    files["apiv2/cache.json"] = json_bytes(cache)
    state = {key: metadata[key] for key in ("version", "revision", "upstreamTag", "upstreamCommit", "repository")}
    state["officialAssets"] = {name: item["sha256"] for name, item in metadata["assets"].items()}
    files["state.json"] = json_bytes(state)
    return files


def release_notes(metadata):
    return (f"PCL CE Restore {metadata['version']}（第三方补丁版）\n\n"
            f"基于官方 {metadata['upstreamTag']}，提交 {metadata['upstreamCommit']}。\n"
            "恢复安装、下载、整合包和启动补全；保留官方核心库与服务配置。"
            "更新来源已切换为本仓库，稳定和 Beta 设置均跟踪稳定补丁版。\n\n"
            "修改后的文件使用本仓库的校验值和 Sigstore 证明；官方证明仅用于验证输入。\n\n"
            "上游更新记录：\n\n" + metadata["upstreamNotes"])


def source_diff(metadata, archive_bytes):
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:gz") as archive:
        originals = {}
        wanted = ["Plain Craft Launcher 2/Modules/Minecraft/RestrictedFeaturePolicy.cs",
                  "Plain Craft Launcher 2/Modules/Updates/UpdateManager.cs", "Plain Craft Launcher 2/metadata.json"]
        for member in archive:
            path = member.name.split("/", 1)[-1]
            if member.isfile() and path in wanted:
                originals[path] = archive.extractfile(member).read().decode("utf-8-sig").replace("\r\n", "\n")
    require(set(originals) == set(wanted), "Upstream source layout changed")
    changed = dict(originals)
    policy = wanted[0]
    changed[policy] = ("using PCL.Core.App.Region;\n\nnamespace PCL;\n\n"
                      "internal static class RestrictedFeaturePolicy\n{\n"
                      "    internal static RegionAssessment Assess() => RegionPolicy.Evaluate(RegionPolicy.ReadWindowsSignals());\n"
                      "    internal static bool IsRestricted => false;\n"
                      "    internal static bool Check(string feature, bool showHint = true) => true;\n"
                      "    internal static void ThrowIfRestricted(string feature) { }\n}\n")
    updater = wanted[1]
    start = changed[updater].index("    public static UpdatesWrapperModel remoteServer =")
    end = changed[updater].index("    });", start) + len("    });")
    replacement = ("    public static UpdatesWrapperModel remoteServer = new(new List<IUpdateSource>\n    {\n"
                   f"        new UpdatesMinioModel({json.dumps(metadata['feedBase'])}, \"PCL CE Restore (GitHub)\")\n"
                   "    });")
    changed[updater] = changed[updater][:start] + replacement + changed[updater][end:]
    details = json.loads(changed[wanted[2]])
    details["name"] = "Plain Craft Launcher (PCL) CE Restore / 第三方补丁版"
    details["version"]["suffix"] = f"restored.{metadata['revision']}"
    changed[wanted[2]] = json_bytes(details).decode("utf-8")
    return "".join("".join(difflib.unified_diff(originals[path].splitlines(True), changed[path].splitlines(True),
                          fromfile="a/" + path, tofile="b/" + path)) for path in wanted).encode("utf-8")


def assemble(metadata, patched, output):
    output, patched = Path(output), Path(patched)
    output.mkdir(parents=True, exist_ok=True)
    release_url = f"https://github.com/{metadata['repository']}/releases/download/{metadata['releaseTag']}/"
    assets = {}
    for arch in ARCHES:
        directory = patched / arch
        report = read_json(directory / "patch-report.json")
        exe_name = f"PCL_CE_Restore_{arch}.exe"
        data = (directory / exe_name).read_bytes()
        require(report["originalSha256"] == metadata["assets"][asset_names(arch)]["sha256"] and
                report["patchedSha256"] == sha256(data) and report["version"] == metadata["version"] and
                report["feedBase"] == metadata["feedBase"] and "PCL.Core.dll" in report["preservedFiles"],
                f"Patch report does not match release: {arch}")
        (output / exe_name).write_bytes(data)
        write_json(output / f"patch-report-{arch}.json", report)
        zip_name = f"PCL_CE_Restore_{arch}.zip"
        write_zip(output / zip_name, {"Plain Craft Launcher Community Edition.exe": data})
        assets[arch] = {"sha256": sha256(data), "versionCode": report["versionCode"],
                        "zipName": zip_name, "url": release_url + zip_name}
    announcements = json.loads(fetch(
        "https://raw.githubusercontent.com/PCL-Community/PCL2_CE_Server/main/apiv2/announcement.json"))
    feed_files = build_feed(metadata, assets, announcements)
    write_zip(output / "update-feed.zip", feed_files)
    (output / "release-state.json").write_bytes(feed_files["state.json"])
    (output / "RELEASE.md").write_text(release_notes(metadata), encoding="utf-8")
    source_url = f"https://codeload.github.com/{metadata['upstreamRepository']}/tar.gz/{metadata['upstreamCommit']}"
    source = fetch(source_url)
    (output / "upstream-source.tar.gz").write_bytes(source)
    (output / "source-equivalent.patch").write_bytes(source_diff(metadata, source))
    write_checksums(output)


def write_checksums(output):
    output = Path(output)
    lines = [f"{sha256(path.read_bytes())}  {path.name}\n" for path in sorted(output.iterdir())
             if path.is_file() and path.name not in ("SHA256SUMS", "RELEASE.md")]
    (output / "SHA256SUMS").write_text("".join(lines), encoding="ascii")


def publish_feed(github, files):
    expected = json.loads(files["state.json"])
    ref = github.api("git/ref/heads/updates", missing=True)
    if ref:
        import base64
        current = github.api("contents/state.json?ref=updates", missing=True)
        if current:
            current = json.loads(base64.b64decode(current["content"]))
            require(version_key(current["version"]) <= version_key(expected["version"]),
                    "Refusing to downgrade the update feed")
        parent = ref["object"]["sha"]
    else:
        default = github.api("")["default_branch"]
        parent = github.api("git/ref/heads/" + default)["object"]["sha"]
    tree = github.api("git/trees", method="POST", data={"tree": [
        {"path": path, "mode": "100644", "type": "blob", "content": data.decode("utf-8")}
        for path, data in sorted(files.items())]})
    commit = github.api("git/commits", method="POST", data={
        "message": "Publish update feed " + expected["version"], "tree": tree["sha"], "parents": [parent],
        "author": {"name": "github-actions[bot]", "email": "41898282+github-actions[bot]@users.noreply.github.com"}})
    if ref:
        github.api("git/refs/heads/updates", method="PATCH", data={"sha": commit["sha"], "force": False})
    else:
        github.api("git/refs", method="POST", data={"ref": "refs/heads/updates", "sha": commit["sha"]})


def validate_feed_archive(data, metadata):
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        require(len(archive.namelist()) == len(set(archive.namelist())), "Duplicate update feed entries")
        files = {name: archive.read(name) for name in archive.namelist()}
    allowed = {"state.json", "apiv2/cache.json", "apiv2/announcement.json"} | {
        f"apiv2/updates/updates-{channel}{arch.lower()}.json" for arch in ARCHES for channel in ("sr", "fr")}
    require(set(files) == allowed, "Unexpected files in update feed archive")
    state = json.loads(files["state.json"])
    require(all(state[key] == metadata[key] for key in
                ("version", "revision", "upstreamTag", "upstreamCommit", "repository")),
            "Update feed provenance mismatch")
    require(state["officialAssets"] == {name: item["sha256"] for name, item in metadata["assets"].items()},
            "Official asset provenance changed for an existing patch release")
    cache = json.loads(files["apiv2/cache.json"])
    for path, content in files.items():
        if path not in ("state.json", "apiv2/cache.json"):
            require(cache.get(Path(path).stem) == hashlib.md5(content, usedforsecurity=False).hexdigest(),
                    "Update feed cache digest mismatch")
    for arch in ARCHES:
        for channel in ("sr", "fr"):
            asset = json.loads(files[f"apiv2/updates/updates-{channel}{arch.lower()}.json"])["assets"][0]
            require(asset["version"]["name"] == metadata["version"] and asset["patches"] == [] and
                    re.fullmatch(r"[0-9a-f]{64}", asset["sha256"]) and asset["downloads"] == [
                        f"https://github.com/{metadata['repository']}/releases/download/"
                        f"{metadata['releaseTag']}/PCL_CE_Restore_{arch}.zip"],
                    "Update feed must point exclusively to this patched release and architecture")
    return files


def publish(metadata, directory):
    github = GitHub(metadata["repository"])
    release = github.api("releases/tags/" + metadata["releaseTag"], missing=True)
    if release and not release["draft"]:
        # Repair a failed feed publication without rebuilding or overwriting a published release.
        asset = next(item for item in release["assets"] if item["name"] == "update-feed.zip")
        data = fetch(asset["browser_download_url"])
        require(asset.get("digest") == "sha256:" + sha256(data), "Published update feed digest mismatch")
        files = validate_feed_archive(data, metadata)
        publish_feed(github, files)
        return
    directory = Path(directory)
    required = {"upstream-source.tar.gz", "source-equivalent.patch", "release-state.json",
                "SHA256SUMS", "update-feed.zip", "update-feed.zip.sigstore.json"}
    for arch in ARCHES:
        required.add(f"patch-report-{arch}.json")
        for extension in ("exe", "zip"):
            required.update({f"PCL_CE_Restore_{arch}.{extension}", f"PCL_CE_Restore_{arch}.{extension}.sigstore.json"})
    require(required.issubset({path.name for path in directory.iterdir() if path.is_file()}),
            "Release assets or signatures are incomplete")
    checksums = {}
    for line in (directory / "SHA256SUMS").read_text(encoding="ascii").splitlines():
        digest, name = line.split("  ", 1)
        require(name == Path(name).name and re.fullmatch(r"[0-9a-f]{64}", digest), "Invalid checksum entry")
        checksums[name] = digest
    for name in required - {"SHA256SUMS"}:
        require(checksums.get(name) == sha256((directory / name).read_bytes()), "Release checksum mismatch")
    files = validate_feed_archive((directory / "update-feed.zip").read_bytes(), metadata)
    if release is None:
        release = github.api("releases", method="POST", data={
            "tag_name": metadata["releaseTag"], "target_commitish": os.environ["GITHUB_SHA"],
            "name": "PCL CE Restore " + metadata["version"], "body": release_notes(metadata),
            "draft": True, "prerelease": False})
    existing = {asset["name"]: asset for asset in github.api(f"releases/{release['id']}/assets?per_page=100")}
    for path in sorted(directory.iterdir()):
        if not path.is_file() or path.name == "RELEASE.md":
            continue
        if path.name in existing:
            github.api(f"releases/assets/{existing[path.name]['id']}", method="DELETE")
        upload = release["upload_url"].split("{", 1)[0] + "?name=" + urllib.parse.quote(path.name, safe="")
        github.api(upload, method="POST", data=path.read_bytes(), content_type="application/octet-stream")
    github.api(f"releases/{release['id']}", method="PATCH", data={"draft": False, "make_latest": "true"})
    publish_feed(github, files)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("discover")
    p.add_argument("--tag")
    p.add_argument("--output", required=True)
    p.add_argument("--github-output")
    for name in ("download", "assemble", "publish"):
        p = sub.add_parser(name)
        p.add_argument("--metadata", required=True)
        p.add_argument("--output", required=name != "publish")
        if name == "assemble":
            p.add_argument("--patched", required=True)
        if name == "publish":
            p.add_argument("--directory")
    p = sub.add_parser("checksums")
    p.add_argument("--directory", required=True)
    args = parser.parse_args()
    if args.command == "discover":
        metadata = discover(read_json(ROOT / "patch-config.json"), args.tag)
        write_json(args.output, metadata)
        if args.github_output:
            with open(args.github_output, "a", encoding="utf-8") as output:
                output.write(f"needed={str(metadata['needed']).lower()}\n")
                output.write(f"reuse_release={str(metadata['reuseRelease']).lower()}\n")
                output.write(f"release_tag={metadata['releaseTag']}\n")
    elif args.command == "download":
        download(read_json(args.metadata), args.output)
    elif args.command == "assemble":
        assemble(read_json(args.metadata), args.patched, args.output)
    elif args.command == "checksums":
        write_checksums(args.directory)
    else:
        publish(read_json(args.metadata), args.directory)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, StopIteration, urllib.error.URLError) as error:
        # No request headers, program secrets, or upstream IL are logged.
        message = f"Release failed: {error}"
        print(message, file=sys.stderr)
        if os.environ.get("GITHUB_ACTIONS") == "true":
            print("::error::" + message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A"))
        sys.exit(1)
