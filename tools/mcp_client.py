"""Small MCP stdio client used by the smoke and scenario scripts.

    from mcp_client import McpClient
    with McpClient(exe, env={"SWMCP_VBA": "on"}) as c:
        tools = c.list_tools()
        r = c.call("sw_status")            # -> dict (parsed JSON text) or raises ToolError
        img = c.call_raw("view_screenshot")  # -> full result dict (content blocks)
"""
from __future__ import annotations

import json
import os
import queue
import subprocess
import threading
import time


class ToolError(Exception):
    """A tool returned isError: the text starts with an error code."""

    def __init__(self, tool: str, text: str):
        super().__init__(f"{tool}: {text}")
        self.tool = tool
        self.text = text
        self.code = text.split(":", 1)[0].strip()


class McpClient:
    def __init__(self, exe: str, env: dict | None = None, protocol: str = "2025-06-18", timeout: float = 300.0):
        e = dict(os.environ)
        if env:
            e.update(env)
        self.timeout = timeout
        self.proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     text=True, encoding="utf-8", bufsize=1, env=e)
        self._out: "queue.Queue[dict]" = queue.Queue()
        self.notifications: list[dict] = []
        self.stderr: list[str] = []
        threading.Thread(target=self._read_stdout, daemon=True).start()
        threading.Thread(target=lambda: [self.stderr.append(l) for l in self.proc.stderr], daemon=True).start()
        self._id = 0
        init = self.request("initialize", {"protocolVersion": protocol, "capabilities": {},
                                           "clientInfo": {"name": "swmcp-tests", "version": "0.1"}})
        self.server_info = init.get("result", {})
        self.notify("notifications/initialized")

    def _read_stdout(self):
        for line in self.proc.stdout:
            line = line.strip()
            if not line:
                continue
            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                self.stderr.append("NON-JSON ON STDOUT: " + line)
                continue
            if "id" not in msg:
                self.notifications.append(msg)
            else:
                self._out.put(msg)

    def notify(self, method: str, params: dict | None = None):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def request(self, method: str, params: dict | None = None, timeout: float | None = None) -> dict:
        self._id += 1
        rid = self._id
        msg = {"jsonrpc": "2.0", "id": rid, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        deadline = time.time() + (timeout or self.timeout)
        while True:
            left = deadline - time.time()
            if left <= 0:
                raise TimeoutError(f"no response to {method} within {timeout or self.timeout} s")
            resp = self._out.get(timeout=left)
            if resp.get("id") == rid:
                return resp

    def list_tools(self) -> list[dict]:
        return self.request("tools/list")["result"]["tools"]

    def call_raw(self, name: str, args: dict | None = None, progress: bool = False, timeout: float | None = None) -> dict:
        params = {"name": name, "arguments": args or {}}
        if progress:
            params["_meta"] = {"progressToken": f"p{self._id + 1}"}
        resp = self.request("tools/call", params, timeout)
        if "error" in resp:
            raise ToolError(name, f"JSONRPC {resp['error'].get('code')}: {resp['error'].get('message')}")
        return resp["result"]

    def call(self, name: str, args: dict | None = None, **kw) -> dict:
        res = self.call_raw(name, args, **kw)
        text = next((b["text"] for b in res.get("content", []) if b.get("type") == "text"), "")
        if res.get("isError"):
            raise ToolError(name, text)
        return json.loads(text) if text.startswith("{") else {"text": text}

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.proc.kill()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
