"""Adds (or replaces) one version in manifest.json, newest first.
Usage: add_manifest_entry.py VERSION TARGETABI URL MD5 CHANGELOG"""
import json
import sys
from datetime import datetime, timezone

version, abi, url, checksum, changelog = sys.argv[1:6]
with open("manifest.json", encoding="utf-8") as f:
    manifest = json.load(f)
plugin = manifest[0]
plugin["versions"] = [v for v in plugin["versions"] if v["version"] != version]
plugin["versions"].insert(0, {
    "version": version,
    "changelog": changelog,
    "targetAbi": abi,
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
})
with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")
