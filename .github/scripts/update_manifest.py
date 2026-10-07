"""Ajoute une version en tête de manifest.json (appelé par le workflow de release)."""
import json
import sys

version, abi, url, checksum, timestamp, changelog = sys.argv[1:7]

with open("manifest.json", encoding="utf-8") as f:
    manifest = json.load(f)

versions = [v for v in manifest[0]["versions"] if v["version"] != version]
versions.insert(0, {
    "version": version,
    "changelog": changelog,
    "targetAbi": abi,
    "sourceUrl": url,
    "checksum": checksum,
    "timestamp": timestamp,
})
manifest[0]["versions"] = versions

with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")
