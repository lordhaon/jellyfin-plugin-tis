"""Adds the release being built to manifest.json (run by the Release workflow)."""
import json
import os
from datetime import datetime, timezone

PLUGIN_GUID = "78a1b40d-66ab-4e61-a42d-62d92f23df0b"
TARGET_ABI = "12.0.0.0"  # oldest Jellyfin version this build supports

repo = os.environ["GITHUB_REPOSITORY"]  # e.g. yourname/jellyfin-plugin-tis
version = os.environ["VERSION"]
tag = os.environ["TAG"]
checksum = os.environ["CHECKSUM"]
changelog = os.environ.get("CHANGELOG", f"Release {tag}")

path = os.path.join(os.path.dirname(__file__), "..", "manifest.json")
with open(path, encoding="utf-8") as f:
    manifest = json.load(f)

plugin = next(p for p in manifest if p["guid"] == PLUGIN_GUID)
plugin["owner"] = repo.split("/")[0]

entry = {
    "version": version,
    "changelog": changelog,
    "targetAbi": TARGET_ABI,
    "sourceUrl": f"https://github.com/{repo}/releases/download/{tag}/tis_{version}.zip",
    "checksum": checksum,
    "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

plugin["versions"] = [v for v in plugin.get("versions", []) if v["version"] != version]
plugin["versions"].insert(0, entry)

with open(path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print(f"manifest.json now lists {len(plugin['versions'])} version(s); newest {version}")
