"""MCP protocol smoke test and scenario runner for solidworks-mcp.

    python tools/mcp_smoke.py <path-to-solidworks-mcp.exe>              # protocol checks (no SOLIDWORKS needed)
    python tools/mcp_smoke.py <exe> --scenario plate                   # + a live modelling scenario (SOLIDWORKS running)

Exits non-zero on any failed check. Protocol checks: initialize (2025-06-18) returns instructions and server info,
tools/list conventions (descriptions, all four annotations, camelCase parameters, size budget), argument validation
errors come back as isError with a code, unknown tools as JSON-RPC errors.
"""
from __future__ import annotations

import json
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
    size = len(json.dumps(tools))
    check(size < 120 * 1024, f"tools/list under 120 KB ({size // 1024} KB)")
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
        if scenario == "plate":
            scenario_plate(c)
        noise = [l for l in c.stderr if l.startswith("NON-JSON")]
        check(not noise, "stdout carries only JSON-RPC")
    print("smoke:", "ok" if not failures else f"{len(failures)} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
