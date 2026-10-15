#!/usr/bin/env python3
"""Converts a yearly Singapore prayer timetable PDF into Resources/timetable.json.

    tools/parse_timetable.py ~/Downloads/"Prayer timetable 2027.pdf"

New years are merged into the existing JSON, so older years keep working.
Needs pdftotext (brew install poppler).
"""
import calendar
import datetime
import json
import re
import subprocess
import sys
from pathlib import Path

NAMES = ["Subuh", "Syuruk", "Zohor", "Asar", "Maghrib", "Isyak"]
# e.g. "15/9/2026   Tues   5 41   6 59   1 04   4 13   7 08   8 17"
ROW = re.compile(r"^\s*(\d{1,2})/(\d{1,2})/(\d{4})\s+\w+" + r"\s+(\d{1,2})\s+(\d{2})" * 6 + r"\s*$")
# The PDF prints 12-hour times without AM/PM.
PM = {"Asar", "Maghrib", "Isyak"}
OUTPUT = Path(__file__).resolve().parent.parent / "Resources" / "timetable.json"


def to_minutes(name, hour, minute):
    if name in PM or (name == "Zohor" and hour != 12):
        hour += 12
    return hour * 60 + minute


def parse(pdf):
    text = subprocess.run(["pdftotext", "-layout", pdf, "-"], capture_output=True, text=True, check=True).stdout
    days = {}
    for line in text.splitlines():
        m = ROW.match(line)
        if not m:
            continue
        date = datetime.date(int(m[3]), int(m[2]), int(m[1]))
        nums = [int(n) for n in m.groups()[3:]]
        minutes = [to_minutes(name, nums[2 * i], nums[2 * i + 1]) for i, name in enumerate(NAMES)]
        if minutes != sorted(set(minutes)):
            sys.exit(f"Times out of order on {line.strip()!r} — check the AM/PM rules")
        days[date.isoformat()] = {name: f"{t // 60:02d}:{t % 60:02d}" for name, t in zip(NAMES, minutes)}
    return days


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    new = parse(sys.argv[1])
    years = sorted({key[:4] for key in new})
    if not years:
        sys.exit("No timetable rows found in that PDF")
    for year in years:
        expected = 366 if calendar.isleap(int(year)) else 365
        found = sum(key.startswith(year) for key in new)
        if found != expected:
            sys.exit(f"{year}: found {found} days, expected {expected}")

    merged = json.loads(OUTPUT.read_text()) if OUTPUT.exists() else {}
    merged.update(new)
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(dict(sorted(merged.items())), indent=1) + "\n")
    print(f"Added {len(new)} days ({', '.join(years)}); timetable now covers {min(merged)} to {max(merged)}")


if __name__ == "__main__":
    main()
