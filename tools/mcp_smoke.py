"""MCP protocol smoke test and scenario runner for solidworks-mcp.

    python tools/mcp_smoke.py <path-to-solidworks-mcp.exe>              # protocol checks (no SOLIDWORKS needed)
    python tools/mcp_smoke.py <exe> --scenario plate                   # + a live modelling scenario (SOLIDWORKS running)
    python tools/mcp_smoke.py <exe> --scenario all                     # plate, enclosure, edit, assembly, drawing

Exits non-zero on any failed check. Protocol checks: initialize (2025-06-18) returns instructions and server info,
tools/list conventions (descriptions, all four annotations, camelCase parameters, size budget), argument validation
errors come back as isError with a code, unknown tools as JSON-RPC errors.

Scenarios are the golden workflows of DESIGN 6.4, each with a call budget (setup calls that only build input files,
such as the saved parts an assembly needs, are not counted). They create and close their own documents and delete
the files they saved.
"""
from __future__ import annotations

import json
import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mcp_client import McpClient, ToolError  # noqa: E402

failures: list[str] = []


def check(cond: bool, what: str):
    print(("  ok   " if cond else "  FAIL ") + what)
    if not cond:
        failures.append(what)


def protocol(c: McpClient):
    print("== protocol")
    info = c.server_info
    check(info.get("serverInfo", {}).get("name") == "solidworks-mcp", "serverInfo.name is solidworks-mcp")
    instr = info.get("instructions", "")
    check(500 < len(instr) <= 4096, f"instructions present and <= 4096 chars ({len(instr)})")
    tools = c.list_tools()
    names = [t["name"] for t in tools]
    print(f"  {len(tools)} tools: {', '.join(names)}")
    check(len(names) == len(set(names)), "tool names unique")
    size = len(json.dumps(tools, separators=(",", ":")))
    check(size / max(1, len(tools)) <= 1536, f"tools/list averages <= 1.5 KB per tool ({size // 1024} KB, {len(tools)} tools)")
    for t in tools:
        n = t["name"]
        check(bool(re.fullmatch(r"[a-z][a-z0-9_]*", n)), f"{n}: snake_case name")
        check(bool(t.get("description")), f"{n}: has a description")
        a = t.get("annotations") or {}
        check(all(k in a for k in ("readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint")), f"{n}: all four annotations")
        for p, s in (t.get("inputSchema", {}).get("properties") or {}).items():
            check(bool(re.fullmatch(r"[a-z][A-Za-z0-9]*", p)), f"{n}.{p}: camelCase parameter")
            check(bool(s.get("description")), f"{n}.{p}: has a description")
    # Error paths
    try:
        c.call("feature_extrude", {"depth_mm": 5})
        check(False, "misspelled argument is rejected")
    except ToolError as e:
        check(e.code == "INVALID_ARGUMENT" and "depthMm" in e.text, "misspelled argument -> INVALID_ARGUMENT with suggestion")
    resp = c.request("tools/call", {"name": "no_such_tool", "arguments": {}})
    check("error" in resp, "unknown tool -> JSON-RPC error")
    st = c.call("sw_status")
    check("serverVersion" in st, "sw_status answers (with or without SOLIDWORKS)")


class Budget:
    """Counts the calls of a scenario (setup calls go through c.call directly and are not counted)."""

    def __init__(self, c: McpClient, name: str, limit: int):
        self.c, self.name, self.limit, self.calls = c, name, limit, 0
        print(f"== scenario: {name} (budget {limit} calls)")

    def __call__(self, tool: str, args: dict | None = None, raw: bool = False):
        self.calls += 1
        r = self.c.call_raw(tool, args or {}) if raw else self.c.call(tool, args or {})
        print(f"  {tool}: {json.dumps(r)[:160]}")
        return r

    def done(self):
        check(self.calls <= self.limit, f"{self.name} scenario within {self.limit} calls ({self.calls})")


def near(actual, expected: float, rel: float = 1e-4) -> bool:
    return actual is not None and abs(actual - expected) <= rel * max(1.0, abs(expected))


def saved_part(c: McpClient, name: str, width: float, height: float, depth: float, centered: bool = True) -> str:
    """Setup (not counted): a saved block part, centered on the origin in x and z; returns its path."""
    c.call("doc_new", {"name": name})
    try:
        c.call("sketch_start", {"on": {"name": "top"}})
        c.call("sketch_add", {"entities": [{"kind": "rectangle", "center": [0, 0], "widthMm": width, "heightMm": height}]})
        c.call("feature_extrude", {"depthMm": depth, "end": "mid_plane"} if centered else {"depthMm": depth})
        r = c.call("doc_save", {"mode": "as", "path": f"{name}.sldprt", "overwrite": True})
        return r.get("savedTo") or r["document"]["path"]
    finally:
        c.call("doc_close", {})


def remove(*paths):
    for p in paths:
        try:
            if p and os.path.exists(p):
                os.remove(p)
        except OSError as e:
            print(f"  (could not delete {p}: {e})")


def scenario_enclosure(c: McpClient):
    """A 120x80x40 box with r8 vertical edges, shelled 2 mm open at the top, four 3.4 mm holes in the floor."""
    b = Budget(c, "enclosure", 16)
    b("doc_new", {"name": "smoke_enclosure"})
    try:
        b("sketch_start", {"on": {"name": "top"}})
        b("sketch_add", {"entities": [{"kind": "rectangle", "center": [0, 0], "widthMm": 120, "heightMm": 80}]})
        b("feature_extrude", {"depthMm": 40})
        b("feature_fillet", {"radiusMm": 8, "edges": [{"where": {"curve": "line", "parallelTo": "y"}, "expect": 4}]})
        b("feature_shell", {"removeFaces": [{"at": [0, 40, 0]}], "thicknessMm": 2})
        b("sketch_start", {"on": {"at": [0, 0, 0]}})
        b("sketch_add", {"entities": [{"kind": "circle", "center": [x, 0, z], "diameterMm": 3.4}
                                      for x in (-50, 50) for z in (-30, 30)]})
        b("feature_extrude", {"cut": True, "end": "through_all"})
        mass = b("model_mass")
        k = 4 - math.pi
        outer = (120 * 80 - k * 64) * 40
        inner = (116 * 76 - k * 36) * 38
        expected = outer - inner - 4 * math.pi * 1.7 ** 2 * 2
        check(near(mass.get("volumeMm3"), expected), f"enclosure volume {expected:.2f} mm3 (got {mass.get('volumeMm3')})")
        img = b("view_screenshot", {"view": "iso", "maxSizePx": 512}, raw=True)
        check(any(x.get("type") == "image" for x in img.get("content", [])), "screenshot returned an image")
    finally:
        b("doc_close", {"discard": True})
        b.done()


def scenario_edit(c: McpClient):
    """Open a saved 100x60x6 plate, change its thickness to 10, round the corners r5, save."""
    path = saved_part(c, "smoke_edit_plate", 100, 60, 6, centered=False)
    b = Budget(c, "edit", 7)
    try:
        b("doc_open", {"path": path})
        dims = b("dimension_list", {"feature": "Boss-Extrude1"})
        names = [d.get("name") for d in dims.get("dimensions", [])]
        check("D1@Boss-Extrude1" in names, f"the extrude depth is listed ({names})")
        b("dimension_set", {"values": [{"name": "D1@Boss-Extrude1", "value": 10}]})
        b("feature_fillet", {"radiusMm": 5, "edges": [{"where": {"curve": "line", "parallelTo": "y"}, "expect": 4}]})
        mass = b("model_mass")
        expected = (100 * 60 - (4 - math.pi) * 25) * 10
        check(near(mass.get("volumeMm3"), expected), f"edited plate volume {expected:.2f} mm3 (got {mass.get('volumeMm3')})")
        b("doc_save", {})
    finally:
        b("doc_close", {"discard": True})
        b.done()
        remove(path)


def scenario_assembly(c: McpClient):
    """Two 20 mm cubes, the second mated 10 mm along x: they interfere by 10x20x20 = 4000 mm3."""
    a_path = saved_part(c, "smoke_cube_a", 20, 20, 20)
    b_path = saved_part(c, "smoke_cube_b", 20, 20, 20)
    b = Budget(c, "assembly", 10)
    try:
        b("doc_new", {"type": "assembly", "name": "smoke_asm"})
        ca = b("asm_insert", {"path": a_path})["component"]["name"]
        cb = b("asm_insert", {"path": b_path, "originMm": [30, 0, 0], "fixed": False})["component"]["name"]
        b("asm_mate_add", {"type": "coincident", "a": {"component": cb, "name": "top"}, "b": {"component": ca, "name": "top"}})
        b("asm_mate_add", {"type": "coincident", "a": {"component": cb, "name": "front"}, "b": {"component": ca, "name": "front"}})
        b("asm_mate_add", {"type": "distance", "a": {"component": cb, "name": "right"}, "b": {"component": ca, "name": "right"},
                           "distanceMm": 10})
        inter = b("asm_interference")
        check(near(inter.get("totalVolumeMm3"), 4000, 1e-3), f"interference 4000 mm3 (got {inter.get('totalVolumeMm3')})")
        comps = b("asm_component_list")
        origins = {x["name"]: x.get("originMm") for x in comps.get("components", [])}
        check(origins.get(cb) is not None and near(abs(origins[cb][0]), 10), f"{cb} sits 10 mm along x ({origins.get(cb)})")
        img = b("view_screenshot", {"view": "iso", "maxSizePx": 512}, raw=True)
        check(any(x.get("type") == "image" for x in img.get("content", [])), "screenshot returned an image")
    finally:
        b("doc_close", {"discard": True})
        b.done()
        remove(a_path, b_path)


def scenario_drawing(c: McpClient):
    """A drawing of a saved 100x60x20 block: standard views, model dimensions, a PDF."""
    path = saved_part(c, "smoke_drawing_block", 100, 60, 20, centered=False)
    b = Budget(c, "drawing", 10)
    pdf = None
    try:
        b("doc_new", {"type": "drawing", "name": "smoke_drawing"})
        views = b("drw_views_standard", {"model": path})
        check(len(views.get("views", [])) >= 3, f"standard views created ({len(views.get('views', []))})")
        dims = b("drw_dimensions_auto")
        check((dims.get("added") or 0) >= 1, f"model dimensions inserted ({dims.get('added')})")
        info = b("drw_info")
        check(bool(info.get("sheets")), "drw_info lists the sheet")
        exp = b("model_export", {"path": "smoke_drawing.pdf", "overwrite": True})
        pdf = exp.get("path")
        check(bool(pdf) and os.path.exists(pdf), f"PDF written ({pdf})")
    finally:
        b("doc_close", {"discard": True})
        b.done()
        remove(path, pdf)


def scenario_plate(c: McpClient):
    """A 100x60x6 plate on the top plane, 4 vertical edges rounded r5, two Ø6 holes, a screenshot."""
    print("== scenario: plate")
    calls = 0

    def call(name, args=None):
        nonlocal calls
        calls += 1
        r = c.call(name, args or {})
        print(f"  {name}: {json.dumps(r)[:160]}")
        return r

    call("doc_new", {"name": "smoke_plate"})
    try:
        call("sketch_start", {"on": {"name": "top"}})
        call("sketch_add", {"entities": [
            {"kind": "rectangle", "center": [0, 0], "widthMm": 100, "heightMm": 60},
            {"kind": "circle", "center": [-35, 0], "diameterMm": 6},
            {"kind": "circle", "center": [35, 0], "diameterMm": 6}]})
        ext = call("feature_extrude", {"depthMm": 6})
        expected = 100 * 60 * 6 - 2 * 3.141592653589793 * 9 * 6
        check(abs(ext["verified"]["volumeDeltaMm3"] - expected) < 0.01, f"plate volume {expected:.3f} mm3")
        fil = call("feature_fillet", {"radiusMm": 5, "edges": [{"where": {"curve": "line", "parallelTo": "y"}, "expect": 4}]})
        check(fil["verified"]["material"] == "removed", "fillet removed material")
        res = c.call_raw("view_screenshot", {"maxSizePx": 512})
        calls += 1
        check(any(b.get("type") == "image" for b in res.get("content", [])), "screenshot returned an image")
        check(calls <= 8, f"plate scenario within 8 calls ({calls})")
    finally:
        c.call("doc_close", {"discard": True})


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    exe = sys.argv[1]
    scenario = sys.argv[sys.argv.index("--scenario") + 1] if "--scenario" in sys.argv else None
    with McpClient(exe) as c:
        protocol(c)
        scenarios = {"plate": scenario_plate, "enclosure": scenario_enclosure, "edit": scenario_edit,
                     "assembly": scenario_assembly, "drawing": scenario_drawing}
        for name in (list(scenarios) if scenario == "all" else (scenario.split(",") if scenario else [])):
            try:
                scenarios[name](c)
            except ToolError as e:
                check(False, f"{name} scenario: {e}")
        noise = [l for l in c.stderr if l.startswith("NON-JSON")]
        check(not noise, "stdout carries only JSON-RPC")
    print("smoke:", "ok" if not failures else f"{len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
