"""Async DAP (Debug Adapter Protocol) client that communicates with netcoredbg."""

import asyncio
import json
import logging
import os
import shutil
from collections import defaultdict
from pathlib import Path

logger = logging.getLogger(__name__)

_NETCOREDBG_SEARCH_PATHS = [
    "/usr/local/bin/netcoredbg",
    "/usr/local/lib/netcoredbg/netcoredbg",
    str(Path.home() / ".local" / "bin" / "netcoredbg"),
    str(Path.home() / ".dotnet" / "tools" / "netcoredbg"),
]


def find_netcoredbg() -> str:
    """Find the netcoredbg binary. Checks NETCOREDBG_PATH env, PATH, then common locations."""
    env_path = os.environ.get("NETCOREDBG_PATH")
    if env_path and Path(env_path).is_file():
        return env_path

    found = shutil.which("netcoredbg")
    if found:
        return found

    for p in _NETCOREDBG_SEARCH_PATHS:
        if Path(p).is_file():
            return p

    raise FileNotFoundError(
        "netcoredbg not found. Install it from https://github.com/Samsung/netcoredbg/releases "
        "or set NETCOREDBG_PATH environment variable."
    )


class DapClient:
    """Async client that manages a netcoredbg subprocess and speaks DAP over stdio."""

    def __init__(self):
        self._process: asyncio.subprocess.Process | None = None
        self._seq = 1
        self._pending: dict[int, asyncio.Future] = {}
        self._event_queues: dict[str, asyncio.Queue] = defaultdict(asyncio.Queue)
        self._reader_task: asyncio.Task | None = None
        self._buffer = b""
        self._closed = False

    @property
    def is_running(self) -> bool:
        return self._process is not None and self._process.returncode is None

    async def start(self) -> None:
        """Start the netcoredbg subprocess in DAP mode."""
        exe = find_netcoredbg()
        self._process = await asyncio.create_subprocess_exec(
            exe, "--interpreter=vscode",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
        )
        self._closed = False
        self._buffer = b""
        self._reader_task = asyncio.create_task(self._reader_loop())

    async def stop(self) -> None:
        """Stop the netcoredbg subprocess."""
        self._closed = True
        if self._reader_task:
            self._reader_task.cancel()
            try:
                await self._reader_task
            except asyncio.CancelledError:
                pass
            self._reader_task = None

        if self._process and self._process.returncode is None:
            self._process.terminate()
            try:
                await asyncio.wait_for(self._process.wait(), timeout=5)
            except asyncio.TimeoutError:
                self._process.kill()
                await self._process.wait()

        # Fail all pending requests
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(ConnectionError("DAP client stopped"))
        self._pending.clear()
        self._process = None

    async def send_request(self, command: str, arguments: dict | None = None) -> dict:
        """Send a DAP request and wait for the response."""
        if not self.is_running:
            raise ConnectionError("netcoredbg is not running")

        seq = self._seq
        self._seq += 1

        msg = {
            "seq": seq,
            "type": "request",
            "command": command,
        }
        if arguments:
            msg["arguments"] = arguments

        future: asyncio.Future[dict] = asyncio.get_event_loop().create_future()
        self._pending[seq] = future

        self._write_message(msg)
        return await future

    async def wait_for_event(self, event_name: str, timeout: float = 30.0) -> dict | None:
        """Wait for a specific DAP event. Returns None on timeout."""
        queue = self._event_queues[event_name]
        try:
            return await asyncio.wait_for(queue.get(), timeout=timeout)
        except asyncio.TimeoutError:
            return None

    def drain_events(self, event_name: str) -> list[dict]:
        """Drain all queued events of a given type without waiting."""
        queue = self._event_queues[event_name]
        events = []
        while not queue.empty():
            try:
                events.append(queue.get_nowait())
            except asyncio.QueueEmpty:
                break
        return events

    def _write_message(self, msg: dict) -> None:
        """Write a DAP message with Content-Length framing."""
        body = json.dumps(msg).encode("utf-8")
        header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
        assert self._process and self._process.stdin
        self._process.stdin.write(header + body)

    async def _reader_loop(self) -> None:
        """Background task that reads DAP messages from netcoredbg stdout."""
        assert self._process and self._process.stdout
        try:
            while not self._closed:
                msg = await self._read_message()
                if msg is None:
                    break
                self._dispatch(msg)
        except asyncio.CancelledError:
            raise
        except Exception:
            if not self._closed:
                logger.exception("DAP reader loop error")

    async def _read_message(self) -> dict | None:
        """Read one DAP message (Content-Length framed) from stdout."""
        assert self._process and self._process.stdout

        # Read headers until we find Content-Length
        content_length = None
        while True:
            line = await self._process.stdout.readline()
            if not line:
                return None  # EOF
            line_str = line.decode("ascii", errors="replace").strip()
            if not line_str:
                # Empty line = end of headers
                if content_length is not None:
                    break
                continue
            if line_str.startswith("Content-Length:"):
                content_length = int(line_str.split(":")[1].strip())

        # Read body
        body = await self._process.stdout.readexactly(content_length)
        return json.loads(body)

    def _dispatch(self, msg: dict) -> None:
        """Route incoming DAP message to the right handler."""
        msg_type = msg.get("type")

        if msg_type == "response":
            req_seq = msg.get("request_seq")
            future = self._pending.pop(req_seq, None)
            if future and not future.done():
                if msg.get("success", True):
                    future.set_result(msg)
                else:
                    error_msg = msg.get("message", "Unknown DAP error")
                    body = msg.get("body", {})
                    if isinstance(body, dict) and "error" in body:
                        detail = body["error"].get("format", "")
                        if detail:
                            error_msg = f"{error_msg}: {detail}"
                    future.set_exception(RuntimeError(f"DAP error: {error_msg}"))

        elif msg_type == "event":
            event_name = msg.get("event", "")
            self._event_queues[event_name].put_nowait(msg)
