#!/usr/bin/env python3
"""Apply one approved online-catalogue issue to the current working tree."""

import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path

body = os.environ.get("ISSUE_BODY", "")
label = os.environ["APPROVAL_LABEL"]
action = {"approved-add": "add", "approved-replace": "replace", "approved-remove": "remove"}[label]

def metadata(name):
    match = re.search(r"<!--\s*" + re.escape(name) + r"\s*:\s*([^>]+?)\s*-->", body, re.I)
    return match.group(1).strip() if match else ""

def normalize(value):
    if isinstance(value, list):
        return [normalize(item) for item in value]
    if isinstance(value, dict):
        return {key[:1].lower() + key[1:]: normalize(item) for key, item in value.items()}
    return value

target_id = metadata("target-profile-id")
fence = chr(96) * 3
json_match = re.search(fence + r"json\s*(\{.*?\})\s*" + fence, body, re.DOTALL)
profile = None

if action != "remove":
    if not json_match:
        raise SystemExit("No JSON profile was found in the approved issue.")
    profile = normalize(json.loads(json_match.group(1)))
    config = profile.get("configuration", {})
    if not isinstance(profile.get("id"), str) or not profile["id"].strip():
        raise SystemExit("The profile needs a non-empty id.")
    if not isinstance(profile.get("name"), str) or not profile["name"].strip():
        raise SystemExit("The profile needs a non-empty name.")
    if not isinstance(config.get("folder"), str) or not config["folder"].strip():
        raise SystemExit("The profile configuration needs a folder.")
    for value, field in ((config.get("iconUrl", ""), "iconUrl"), (config.get("sourceUrl", ""), "sourceUrl")):
        if value and (not isinstance(value, str) or not value.startswith("https://")):
            raise SystemExit(f"{field} must use HTTPS.")

if not target_id and profile is not None:
    target_id = profile["id"]
if not target_id:
    raise SystemExit("Add the target-profile-id metadata comment before approving this change.")

path = Path("OnlineCatalog/catalog.json")
catalogue = json.loads(path.read_text(encoding="utf-8"))
profiles = catalogue.setdefault("profiles", [])
target = target_id.casefold()
indexes = [i for i, item in enumerate(profiles) if item.get("id", "").casefold() == target]

if action == "add":
    if indexes:
        raise SystemExit("A published profile already has this id. Use approved-replace to update it.")
    profiles.append(profile)
    result = f"Added profile {target_id}."
elif action == "replace":
    if len(indexes) != 1:
        raise SystemExit("The target profile does not exist or is ambiguous.")
    if profile["id"].casefold() != target:
        raise SystemExit("The proposed profile id must match target-profile-id for a replacement.")
    profiles[indexes[0]] = profile
    result = f"Replaced profile {target_id}."
else:
    if len(indexes) != 1:
        raise SystemExit("The target profile does not exist or is ambiguous.")
    profiles.pop(indexes[0])
    result = f"Removed profile {target_id}."

profiles.sort(key=lambda item: item["name"].casefold())
catalogue["updatedUtc"] = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
path.write_text(json.dumps(catalogue, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
Path("catalogue-result.txt").write_text(result, encoding="utf-8")
if profile is not None:
    Path("catalogue-news.env").write_text(
        f"NEWS_SOFTWARE_ID={profile.get('id', target_id)}\n"
        f"NEWS_SOFTWARE_NAME={profile.get('name', target_id)}\n",
        encoding="utf-8",
    )
