#!/usr/bin/env python3
"""Refresh the CE About inventory from restored NuGet metadata and vendored notices.

Run after restoring Web and DesktopHelper. --check verifies the checked-in snapshot.
No network calls or local machine paths are included in the generated inventory.
"""
# Copyright (c) Richard D. Kiernan.
# Licensed under the Business Source License 1.1. See LICENSE for details.

import argparse
import json
from pathlib import Path
from urllib.parse import quote, urlparse
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "src/LinuxMadeSane.Web/Resources/about-packages.json"
PROJECTS = {"Web": "Server", "DesktopHelper": "Desktop Assistant"}


def safe_url(value, fallback):
    return value if urlparse(value).scheme in ("https", "http") else fallback


def inventory():
    packages = {}
    for project, group in PROJECTS.items():
        assets_path = ROOT / f"src/LinuxMadeSane.{project}/obj/project.assets.json"
        if not assets_path.exists():
            raise RuntimeError(f"Restore LinuxMadeSane.{project} before generating About credits.")
        assets = json.loads(assets_path.read_text())
        for key, library in assets["libraries"].items():
            if library["type"] != "package":
                continue
            if key in packages:
                packages[key]["Groups"].append(group)
                continue
            folder = next((Path(base) / library["path"] for base in assets["packageFolders"]
                           if (Path(base) / library["path"]).is_dir()), None)
            if folder is None:
                raise RuntimeError(f"Missing restored package: {key}")
            metadata = next(iter(ET.parse(next(folder.glob("*.nuspec"))).getroot()))
            fields = {node.tag.split("}")[-1]: node for node in metadata}
            value = lambda name: (fields[name].text or "").strip() if name in fields else ""
            name, version = key.rsplit("/", 1)
            package_url = f"https://www.nuget.org/packages/{quote(name)}/{quote(version)}"
            license_node = fields.get("license")
            expression = license_node is not None and license_node.get("type") == "expression"
            packages[key] = {
                "Name": name, "Version": version, "Groups": [group],
                "License": value("license") if expression else "Package license",
                "LicenseUrl": f"https://licenses.nuget.org/{quote(value('license'))}" if expression else package_url + "/License",
                "Url": safe_url(value("projectUrl"), package_url),
                "PackageUrl": package_url,
                "Authors": value("authors"),
                "Description": " ".join(value("description").split()),
            }
    return sorted(packages.values(), key=lambda package: (package["Name"].lower(), package["Version"]))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    content = json.dumps(inventory(), indent=2, ensure_ascii=False) + "\n"
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text() != content:
            raise SystemExit("About package inventory is stale. Run scripts/generate-about-credits.py.")
        print("About package inventory matches restored CE dependencies.")
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(content)
        print(f"Updated About credits: {len(json.loads(content))} NuGet packages.")


if __name__ == "__main__":
    main()
