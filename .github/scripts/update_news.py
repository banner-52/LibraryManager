#!/usr/bin/env python3
"""Add or update a Library Manager news item and keep each section compact."""

import json
import os
from datetime import datetime, timezone
from pathlib import Path

MAX_ITEMS_PER_SECTION = 20
path = Path("OnlineCatalog/news.json")
feed = json.loads(path.read_text(encoding="utf-8"))
sections = feed.setdefault("sections", {"catalogue": [], "features": [], "fixes": []})

section = os.environ["NEWS_SECTION"].strip().lower()
if section not in ("catalogue", "features", "fixes"):
    raise SystemExit(f"Unsupported news section: {section}")

item = {
    "id": os.environ["NEWS_ID"].strip(),
    "date": os.environ.get("NEWS_DATE") or datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
    "title": os.environ["NEWS_TITLE"].strip(),
    "description": os.environ.get("NEWS_DESCRIPTION", "").strip(),
}
software_id = os.environ.get("NEWS_SOFTWARE_ID", "").strip()
if software_id:
    item["softwareId"] = software_id

items = [existing for existing in sections.setdefault(section, []) if existing.get("id", "").casefold() != item["id"].casefold()]
items.append(item)
items.sort(key=lambda value: value.get("date", ""), reverse=True)
sections[section] = items[:MAX_ITEMS_PER_SECTION]
feed["schemaVersion"] = 1
feed["updatedUtc"] = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
path.write_text(json.dumps(feed, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
