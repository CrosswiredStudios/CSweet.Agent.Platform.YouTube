#!/usr/bin/env python3
"""Pack and verify C-Sweet agent bundles (.csab).

Replicates the bundle layout produced by CSweet.Office.BuilderGuest
(CreateBundleAsync) and the validation enforced by
CSweet.ExecutionArtifacts.FileSystemAgentArtifactStore (ValidateBundleAsync):

  <bundle>.csab (tar, PAX format)
    artifact.json            {"formatVersion":"1.0","operatingSystem":"linux",
                              "architecture":"x64","entrypoint":["<name>"]}
    payload/<publish output files, ordinal-sorted>...

Usage:
  python3 scripts/pack-csab.py pack --manifest csweet-plugin.json \
      --publish-dir publish --os linux --arch x64 --out agent.csab
  python3 scripts/pack-csab.py verify --bundle agent.csab --os linux --arch x64
"""

import argparse
import hashlib
import io
import json
import os
import sys
import tarfile

FORMAT_VERSION = "1.0"
MAX_FILES = 10_000
MAX_UNCOMPRESSED_BYTES = 2 * 1024 * 1024 * 1024
MAX_MANIFEST_BYTES = 1024 * 1024
MAX_PATH_LENGTH = 512


def fail(message):
    print(f"pack-csab: error: {message}", file=sys.stderr)
    raise SystemExit(1)


def manifest_entrypoint(manifest_path):
    try:
        with open(manifest_path, encoding="utf-8") as handle:
            manifest = json.load(handle)
    except (OSError, ValueError) as error:
        fail(f"cannot read manifest '{manifest_path}': {error}")
    project_path = ((manifest.get("runtime") or {}).get("projectPath") or "").replace("\\", "/")
    entrypoint = os.path.splitext(os.path.basename(project_path))[0]
    if not entrypoint:
        fail(f"manifest '{manifest_path}' is missing runtime.projectPath")
    return manifest.get("version"), entrypoint


def collect_files(publish_dir):
    publish_dir = os.path.abspath(publish_dir)
    if not os.path.isdir(publish_dir):
        fail(f"publish directory '{publish_dir}' does not exist")
    collected = []
    for dirpath, dirnames, filenames in os.walk(publish_dir, followlinks=False):
        dirnames.sort()
        for name in sorted(filenames):
            full = os.path.join(dirpath, name)
            if os.path.islink(full):
                fail(f"publish output contains a symbolic link: {full}")
            if not os.path.isfile(full):
                fail(f"publish output contains a non-regular file: {full}")
            rel = os.path.relpath(full, publish_dir).replace(os.sep, "/")
            collected.append((rel, full))
    collected.sort(key=lambda item: item[0])
    if not 1 <= len(collected) <= MAX_FILES:
        fail(f"publish output file count {len(collected)} is outside 1..{MAX_FILES}")
    return collected


def add_entry(writer, name, data, mode):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    info.uid = 0
    info.gid = 0
    info.uname = ""
    info.gname = ""
    info.mtime = 0
    info.type = tarfile.REGTYPE
    writer.addfile(info, io.BytesIO(data))


def cmd_pack(args):
    _, entrypoint = manifest_entrypoint(args.manifest)
    files = collect_files(args.publish_dir)
    manifest_bytes = json.dumps(
        {
            "formatVersion": FORMAT_VERSION,
            "operatingSystem": args.os,
            "architecture": args.arch,
            "entrypoint": [entrypoint],
        },
        separators=(",", ":"),
    ).encode("utf-8")
    total = len(manifest_bytes)
    with tarfile.open(args.out, "w", format=tarfile.PAX_FORMAT) as writer:
        add_entry(writer, "artifact.json", manifest_bytes, 0o644)
        for rel, full in files:
            size = os.path.getsize(full)
            total = total + size
            if total > args.max_bytes:
                fail("agent bundle exceeds its approved byte limit")
            executable = rel == entrypoint or rel.endswith(".sh")
            with open(full, "rb") as handle:
                data = handle.read()
            if len(data) != size:
                fail(f"file changed while packing: {rel}")
            add_entry(writer, "payload/" + rel, data, 0o755 if executable else 0o644)
    if os.path.getsize(args.out) > args.max_bytes:
        fail("agent bundle exceeds its approved byte limit")
    digest = hashlib.sha256()
    with open(args.out, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    print(f"sha256:{digest.hexdigest()}  {args.out}")


def check_name(name):
    normalized = name.replace("\\", "/").rstrip("/")
    if (
        not normalized
        or len(normalized) > MAX_PATH_LENGTH
        or normalized.startswith("/")
        or os.path.isabs(normalized)
        or any(segment in ("", ".", "..") for segment in normalized.split("/"))
        or any(char.isspace() is False and ord(char) < 0x20 for char in normalized)
    ):
        fail(f"artifact contains an invalid path: {name!r}")
    return normalized


def cmd_verify(args):
    try:
        reader = tarfile.open(args.bundle, "r")
    except (OSError, tarfile.TarError) as error:
        fail(f"cannot open bundle '{args.bundle}': {error}")
    with reader:
        members = reader.getmembers()
        if len(members) > MAX_FILES:
            fail("artifact contains too many entries")
        names = set()
        modes = {}
        total = 0
        manifest = None
        for member in members:
            name = check_name(member.name)
            lowered = name.lower()
            if lowered in names:
                fail("artifact contains duplicate or case-colliding paths")
            names.add(lowered)
            if member.uid != 0 or member.gid != 0:
                fail("artifact ownership metadata must be normalized")
            if member.mode & 0o7000:
                fail("artifact entries cannot contain privileged mode bits")
            if member.isdir():
                continue
            if not member.isfile():
                fail("artifact contains a link or special file")
            modes[name] = member.mode
            if name != "artifact.json" and not name.startswith("payload/"):
                fail("artifact files must be contained beneath payload/")
            total += member.size
            if total > args.max_uncompressed_bytes:
                fail("artifact exceeds the uncompressed size limit")
            if name == "artifact.json":
                if manifest is not None or member.size > MAX_MANIFEST_BYTES:
                    fail("artifact manifest is missing, duplicated, or oversized")
                extracted = reader.extractfile(member)
                if extracted is None:
                    fail("artifact manifest is empty")
                try:
                    manifest = json.load(extracted)
                except ValueError:
                    fail("artifact manifest is not valid JSON")
        if manifest is None:
            fail("artifact does not contain artifact.json")
        if (
            manifest.get("formatVersion") != FORMAT_VERSION
            or manifest.get("operatingSystem") != args.os
            or manifest.get("architecture") != args.arch
        ):
            fail("artifact manifest does not match its import descriptor")
        entrypoint = manifest.get("entrypoint")
        if (
            not isinstance(entrypoint, list)
            or not 1 <= len(entrypoint) <= 32
            or any(not isinstance(item, str) or not item or len(item) > MAX_PATH_LENGTH for item in entrypoint)
        ):
            fail("artifact entrypoint is invalid")
        executable = entrypoint[0].replace("\\", "/")
        if (
            executable.startswith("/")
            or os.path.isabs(executable)
            or any(segment in ("", ".", "..") for segment in executable.split("/"))
            or any(ord(char) < 0x20 for char in executable)
            or modes.get("payload/" + executable, 0) & 0o100 == 0
        ):
            fail("artifact entrypoint is missing or is not executable")
    digest = hashlib.sha256()
    with open(args.bundle, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    print(f"verify: OK sha256:{digest.hexdigest()}  {args.bundle}")


def main():
    parser = argparse.ArgumentParser(description="Pack and verify C-Sweet agent bundles (.csab).")
    sub = parser.add_subparsers(dest="command", required=True)
    pack = sub.add_parser("pack", help="Build a .csab bundle from dotnet publish output.")
    pack.add_argument("--manifest", required=True, help="Path to csweet-plugin.json.")
    pack.add_argument("--publish-dir", required=True, help="dotnet publish output directory.")
    pack.add_argument("--os", default="linux", help="Guest operating system (default: linux).")
    pack.add_argument("--arch", default="x64", help="Guest architecture (default: x64).")
    pack.add_argument("--out", required=True, help="Output bundle path.")
    pack.add_argument("--max-bytes", type=int, default=MAX_UNCOMPRESSED_BYTES)
    pack.set_defaults(func=cmd_pack)
    verify = sub.add_parser("verify", help="Validate a .csab bundle against artifact store rules.")
    verify.add_argument("--bundle", required=True, help="Bundle path to validate.")
    verify.add_argument("--os", default="linux")
    verify.add_argument("--arch", default="x64")
    verify.add_argument("--max-uncompressed-bytes", type=int, default=MAX_UNCOMPRESSED_BYTES)
    verify.set_defaults(func=cmd_verify)
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
