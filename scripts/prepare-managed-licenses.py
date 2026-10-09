#!/usr/bin/env python3
"""Copy package license/notices verbatim; record pinned metadata, never invent source pins."""
import argparse
import hashlib
import json
import pathlib
import re
import shutil
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def request(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "LaunchPad-license-packaging"}), timeout=30) as response:
        data = response.read(2 * 1024 * 1024 + 1)
    if len(data) > 2 * 1024 * 1024:
        raise ValueError("License metadata exceeds limit")
    return data


def metadata(directory):
    paths = list(directory.glob("*.nuspec"))
    if len(paths) != 1:
        raise ValueError(f"Missing package metadata: {directory.name}")
    root = ET.parse(paths[0]).getroot()
    meta = next(child for child in root if child.tag.split("}")[-1] == "metadata")
    nodes = {child.tag.split("}")[-1]: child for child in meta}
    license_node = nodes.get("license")
    repository = nodes.get("repository")
    return {
        "id": nodes["id"].text, "version": nodes["version"].text,
        "license": license_node.text if license_node is not None else None,
        "licenseType": license_node.get("type") if license_node is not None else None,
        "repository": repository.get("url") if repository is not None else None,
        "commit": repository.get("commit") if repository is not None else None,
        "copyright": nodes.get("copyright").text if nodes.get("copyright") is not None else None,
    }


def upstream_files(row):
    repository = row["repository"].removesuffix(".git").rstrip("/")
    match = re.fullmatch(r"https://github\.com/([^/]+/[^/]+)", repository)
    if not match:
        raise ValueError(f"No exact license location for {row['id']}")
    repo = match.group(1)
    revision = row["commit"]
    if not revision:
        tags = json.loads(request(f"https://api.github.com/repos/{repo}/tags?per_page=100"))
        selected = [tag for tag in tags if tag["name"] in (row["version"], "v" + row["version"])]
        if len(selected) == 1:
            revision = selected[0]["commit"]["sha"]
            row["licenseTag"] = selected[0]["name"]
        elif row["id"] == "MicroCom.Runtime" and row["version"] == "0.11.0":
            # This package publishes MIT + the 2021 Nikita Tsukanov copyright,
            # but no repository revision or version tag. Pin the LICENSE file's
            # own revision and disclose that it is NOT a package source pin.
            history = json.loads(request(f"https://api.github.com/repos/{repo}/commits?path=LICENSE&per_page=1"))
            revision = history[0]["sha"]
            row["licenseProvenance"] = "Upstream LICENSE revision; copyright matches nuspec; package source revision not supplied"
        else:
            raise ValueError(f"No unique upstream version tag for {row['id']} {row['version']}")
    # A license tag is a license lookup, not a claim that NuGet metadata pinned the binary.
    row["licenseRevision"] = revision
    entries = json.loads(request(f"https://api.github.com/repos/{repo}/contents?ref={revision}"))
    licenses = [entry for entry in entries if entry["type"] == "file" and re.match(r"(?i)^(?:licen[cs]e|copying)(?:\.|$)", entry["name"])]
    notices = [entry for entry in entries if entry["type"] == "file" and re.match(r"(?i)^(?:notice|third.party.notices)(?:\.|$)", entry["name"])]
    if not licenses:
        raise ValueError(f"No upstream full license text for {row['id']}")
    texts = [(entry["name"], request(entry["download_url"]), entry["download_url"]) for entry in licenses + notices]
    if row["id"] == "MicroCom.Runtime":
        body = b"\n".join(text[1] for text in texts).decode("utf-8")
        if "MIT License" not in body or "2021 Nikita Tsukanov" not in body:
            raise ValueError("MicroCom license does not match package metadata")
    return texts


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--cache", type=pathlib.Path, required=True)
    parser.add_argument("--runtime-version", required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    dist = root / "dist"
    lock = json.loads((root / "src/LaunchPad/packages.publish.lock.json").read_text())
    packages = {(name.lower(), value["resolved"]) for group in lock["dependencies"].values()
                for name, value in group.items() if "resolved" in value}
    packages.add(("microsoft.netcore.app.runtime.win-x64", args.runtime_version))
    files, rows, upstream_cache = [], [], {}
    for name, version in sorted(packages):
        source = args.cache / name / version
        row = metadata(source)
        if row["version"] != version or not row["license"] or not row["repository"]:
            raise ValueError(f"Incomplete license metadata: {name}/{version}")
        row["kind"] = "dotnet-runtime" if name == "microsoft.netcore.app.runtime.win-x64" else "nuget"
        texts = [(item.name, item.read_bytes(), f"NuGet package {row['id']} {version}")
                 for item in source.iterdir() if item.is_file() and re.match(r"(?i)^(?:licen[cs]e|copying|notice|third.party.notices)(?:\.|$)", item.name)]
        if not any(re.match(r"(?i)^(?:licen[cs]e|copying)(?:\.|$)", item[0]) for item in texts):
            key = (row["repository"], row["commit"], version)
            if key not in upstream_cache:
                upstream_cache[key] = upstream_files(row)
            texts = upstream_cache[key]
        row["files"] = []
        for filename, data, origin in texts:
            if not data or b"<html" in data[:300].lower():
                raise ValueError(f"Invalid full text for {name}")
            destination = dist / "licenses/nuget" / f"{name}-{version}" / filename
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(data)
            relative = destination.relative_to(dist).as_posix()
            row["files"].append(relative)
            files.append({"path": relative, "sha256": sha(destination), "origin": origin})
        repository = row["repository"].removesuffix(".git").rstrip("/")
        row["source"] = repository.rstrip("/") + "/tree/" + row["commit"] if row["commit"] and repository.startswith("https://github.com/") else repository
        rows.append(row)
    notices = ["LaunchPad — third-party notices (Native Windows edition)", "",
               "Full license texts and supplied composite notices are in licenses/.",
               "Repository commits are package metadata where supplied; version tags used for license lookup are identified separately.", ""]
    for row in rows:
        notices += [f"{row['id']} {row['version']}", f"License: {row['license']}",
                    f"Source: {row['source']}", f"Full text: {', '.join(row['files'])}"]
        if row["copyright"]:
            notices.append(row["copyright"])
        if row.get("licenseProvenance"):
            notices.append(row["licenseProvenance"])
        notices.append("")
    notice_path = dist / "THIRD-PARTY-NOTICES.txt"
    notice_path.write_text("\n".join(notices) + "\n", encoding="utf-8")
    files.append({"path": "THIRD-PARTY-NOTICES.txt", "sha256": sha(notice_path), "origin": "Generated from pinned NuGet metadata and retained license texts"})
    manifest = {"schema": 1, "profile": "Native", "components": rows, "files": files,
                "packageLockSha256": sha(root / "src/LaunchPad/packages.publish.lock.json")}
    (dist / "licenses/manifest-native.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"profile": "Native", "components": len(rows), "files": len(files)}))


if __name__ == "__main__":
    main()
