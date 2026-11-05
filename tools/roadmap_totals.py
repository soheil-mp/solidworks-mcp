"""Checks ROADMAP.md's item tables and recomputes its effort table.

    python tools/roadmap_totals.py [ROADMAP.md]           # rewrite the effort table
    python tools/roadmap_totals.py [ROADMAP.md] --check   # fail if a row is malformed or the table is out of date

Item rows look like `| T5.3 | item | approach | done when | hours | P0-P3 | state |` under a `## T5 · Name` heading.
"""
import re
import sys

HEADER = "| Track | Items | Hours | P0 | P1 | P2 | P3 |\n|---|---|---|---|---|---|---|\n"
PRIORITIES = ("P0", "P1", "P2", "P3")


def parse(text):
    tracks, current, ids, problems = [], None, set(), []
    for line in text.splitlines():
        m = re.match(r"^## (T\d+) · (.+)$", line)
        if m:
            current = {"id": m.group(1), "name": m.group(2), "rows": []}
            tracks.append(current)
            continue
        if not re.match(r"^\| T\d+\.\d+ \|", line):
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if len(cells) != 7:
            problems.append(f"{cells[0]}: {len(cells)} cells instead of 7 (a '|' inside a cell?)")
            continue
        iid, hours, pri = cells[0], cells[4], cells[5]
        if iid in ids:
            problems.append(f"{iid}: duplicate id")
        ids.add(iid)
        if current is None or not iid.startswith(current["id"] + "."):
            problems.append(f"{iid}: outside its track's section")
        if not hours.isdigit():
            problems.append(f"{iid}: hours '{hours}' is not a whole number")
        if pri not in PRIORITIES:
            problems.append(f"{iid}: priority '{pri}' is not P0-P3")
        if current is not None:
            current["rows"].append((int(hours) if hours.isdigit() else 0, pri))
    return tracks, problems


def table(tracks):
    lines, total = [], {"n": 0, "h": 0, **{p: 0 for p in PRIORITIES}}
    for t in tracks:
        by = {p: sum(h for h, pr in t["rows"] if pr == p) for p in PRIORITIES}
        hours = sum(by.values())
        # GitHub's heading anchors: lower case, punctuation dropped, spaces to hyphens.
        anchor = re.sub(r"[^a-z0-9 -]", "", f"{t['id']} · {t['name']}".lower()).replace(" ", "-")
        lines.append(f"| [{t['id']} {t['name']}](#{anchor}) | {len(t['rows'])} | {hours} | " + " | ".join(str(by[p]) for p in PRIORITIES) + " |")
        total["n"] += len(t["rows"])
        total["h"] += hours
        for p in PRIORITIES:
            total[p] += by[p]
    lines.append(f"| **Total** | **{total['n']}** | **{total['h']}** | " + " | ".join(f"**{total[p]}**" for p in PRIORITIES) + " |")
    return "\n".join(lines) + "\n", total


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    check = "--check" in sys.argv[1:]
    path = args[0] if args else "ROADMAP.md"
    text = open(path, encoding="utf-8", newline="").read()
    tracks, problems = parse(text)
    if problems:
        print("\n".join(problems))
        return 1
    body, total = table(tracks)
    i = text.find(HEADER)
    if i < 0:
        print("The effort table's header was not found.")
        return 1
    start = i + len(HEADER)
    end = start
    while end < len(text) and text.startswith("|", end):
        end = text.index("\n", end) + 1
    updated = text[:start] + body + text[end:]
    if check:
        if updated != text:
            print("The effort table is out of date: run python tools/roadmap_totals.py")
            return 1
        print(f"ok: {total['n']} items, {total['h']} hours")
        return 0
    open(path, "w", encoding="utf-8", newline="").write(updated)
    print(body + f"P0+P1: {total['P0'] + total['P1']} hours")
    return 0


if __name__ == "__main__":
    sys.exit(main())
