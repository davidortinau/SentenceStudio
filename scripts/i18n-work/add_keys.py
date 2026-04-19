#!/usr/bin/env python3
"""Append localization keys to AppResources.resx and AppResources.ko.resx.

Usage: python add_keys.py <batch_json_file>

The JSON file format:
{
  "comment": "Batch name",
  "keys": [
    {"key": "Common_Save", "en": "Save", "ko": "저장", "comment": "Common: Save button"},
    ...
  ]
}
"""
import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
EN_PATH = REPO / "src/SentenceStudio.Shared/Resources/Strings/AppResources.resx"
KO_PATH = REPO / "src/SentenceStudio.Shared/Resources/Strings/AppResources.ko.resx"

XML_SPECIAL = {"&": "&amp;", "<": "&lt;", ">": "&gt;"}


def escape(s: str) -> str:
    out = []
    for ch in s:
        if ch in XML_SPECIAL:
            out.append(XML_SPECIAL[ch])
        else:
            out.append(ch)
    return "".join(out)


def build_block(key: str, value: str, comment: str) -> str:
    k = escape(key)
    v = escape(value)
    c = escape(comment)
    return (
        f'  <data name="{k}" xml:space="preserve">\n'
        f'    <value>{v}</value>\n'
        f'    <comment>{c}</comment>\n'
        f'  </data>\n'
    )


def existing_keys(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8")
    import re
    return set(re.findall(r'<data name="([^"]+)"', text))


def insert_before_root_close(path: Path, block: str):
    text = path.read_text(encoding="utf-8")
    idx = text.rfind("</root>")
    if idx < 0:
        raise RuntimeError(f"No </root> in {path}")
    new_text = text[:idx] + block + text[idx:]
    path.write_text(new_text, encoding="utf-8")


def main():
    if len(sys.argv) != 2:
        print("usage: add_keys.py <json>", file=sys.stderr)
        sys.exit(1)

    data = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8"))
    keys = data["keys"]
    batch = data.get("comment", "")

    en_existing = existing_keys(EN_PATH)
    ko_existing = existing_keys(KO_PATH)

    en_blocks = []
    ko_blocks = []
    skipped = []
    added = []

    for entry in keys:
        k = entry["key"]
        if k in en_existing:
            skipped.append(k)
            continue
        en_blocks.append(build_block(k, entry["en"], entry.get("comment", "")))
        ko_blocks.append(build_block(k, entry["ko"], entry.get("comment", "")))
        added.append(k)

    if en_blocks:
        insert_before_root_close(EN_PATH, "".join(en_blocks))
    if ko_blocks:
        insert_before_root_close(KO_PATH, "".join(ko_blocks))

    print(f"Batch: {batch}")
    print(f"  Added: {len(added)} keys")
    if skipped:
        print(f"  Skipped (already exist): {len(skipped)} keys: {skipped[:5]}{'...' if len(skipped) > 5 else ''}")


if __name__ == "__main__":
    main()
