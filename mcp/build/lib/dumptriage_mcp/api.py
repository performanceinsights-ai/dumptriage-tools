"""A thin client for the DumpTriage customer API (/api/v1, service/backend/openapi.customer.json).

Every call goes through the public routes, so auth, key scopes, quotas, rate limits, idempotency and the terms check
stay in the API (website#4343). This module only turns a non-2xx answer into an ApiError an agent can act on.
"""
from __future__ import annotations

import asyncio
import hashlib
import ipaddress
import json
import os
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import Any, BinaryIO, Callable

import httpx

DEFAULT_BASE_URL = "https://api.performanceinsights.ai"
USER_AGENT = "dumptriage-mcp/0.1.0"

# A dump can be many GB: no read or write timeout on the transfer itself, only on connecting.
UPLOAD_TIMEOUT = httpx.Timeout(connect=30.0, read=None, write=None, pool=30.0)
CALL_TIMEOUT = httpx.Timeout(60.0, connect=30.0)

_HASH_CHUNK = 4 * 1024 * 1024


class ConfigError(Exception):
    """The server was started without what it needs (an API key)."""


@dataclass
class ApiError(Exception):
    status: int
    detail: str
    retry_after: str | None = None

    def __str__(self) -> str:
        hint = _HINTS.get(self.status, "")
        retry = f" Retry after {self.retry_after} s." if self.retry_after else ""
        return f"DumpTriage API answered {self.status}: {self.detail}.{retry}{hint}"


_HINTS = {
    401: " Check DUMPTRIAGE_API_KEY in this MCP server's configuration: the key is missing, revoked or replaced.",
    403: (
        " An upload-only key cannot read dumps or reports: use a full-scope key. A closed account, or terms not yet"
        " accepted in the portal, also answer 403."
    ),
}


def _detail(response: httpx.Response) -> str:
    try:
        body = response.json()
    except ValueError:
        return response.text.strip()[:500] or response.reason_phrase
    detail = body.get("detail") if isinstance(body, dict) else None
    if isinstance(detail, str):
        return detail
    return json.dumps(detail if detail is not None else body)[:500]


def _check(response: httpx.Response) -> httpx.Response:
    if response.is_success:
        return response
    raise ApiError(response.status_code, _detail(response), response.headers.get("retry-after"))


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        while chunk := f.read(_HASH_CHUNK):
            digest.update(chunk)
    return digest.hexdigest()


def idempotency_key(file_sha256: str, fields: dict[str, str]) -> str:
    """The same file with the same fields is the same upload: a retried tool call replays the first answer instead of
    uploading twice. Different fields make a new key, so the API's own rule for an identical dump applies, not a 422 for a
    reused key (services/idempotency.py)."""
    material = file_sha256 + "\n" + json.dumps(fields, sort_keys=True)
    return "mcp-" + hashlib.sha256(material.encode()).hexdigest()[:60]


class _CountingReader:
    """The file as httpx reads it for the multipart body, counting bytes so the tool can report progress."""

    def __init__(self, f: BinaryIO):
        self._f = f
        self.sent = 0

    def read(self, size: int = -1) -> bytes:
        data = self._f.read(size)
        self.sent += len(data)
        return data

    def __getattr__(self, name: str) -> Any:  # seek/tell, for httpx's Content-Length
        return getattr(self._f, name)


def local_file(path: str) -> Path:
    """A path the agent named, resolved against this server's working directory (the agent's project, for a stdio
    server). Refuses anything but a regular file, so the error is ours and nothing is sent."""
    p = Path(path).expanduser()
    if not p.is_absolute():
        p = Path.cwd() / p
    if not p.exists():
        raise FileNotFoundError(f"no such file: {p}")
    if not p.is_file():
        raise IsADirectoryError(f"not a regular file: {p}")
    return p


def secure_api_url(url: str) -> bool:
    """https, or plain http to this machine only (a local test backend): the API key travels in every request and a dump
    in every upload, so anything else would send them in clear. The same rule as the Linux agent's secure_api_url and
    Invoke-DumpTriageCollect.ps1's Test-ApiBaseUrl (#60)."""
    try:
        u = urllib.parse.urlsplit(url)  # a malformed [host] raises here
        u.port  # and a malformed port here, not on the first call
    except ValueError:
        return False
    if u.scheme == "https" and u.hostname:
        return True
    if u.scheme != "http" or not u.hostname:
        return False
    if u.hostname == "localhost":
        return True
    try:
        return ipaddress.ip_address(u.hostname).is_loopback
    except ValueError:
        return False


class DumpTriageApi:
    def __init__(self, api_key: str, base_url: str = DEFAULT_BASE_URL, transport: httpx.AsyncBaseTransport | None = None):
        if not secure_api_url(base_url):
            raise ConfigError(f"DUMPTRIAGE_API_URL must be an https URL (plain http only to this machine), got {base_url!r}")
        if not api_key:
            raise ConfigError(
                "DUMPTRIAGE_API_KEY is not set. Create a key in the DumpTriage portal (Account, API keys) and put it in "
                "this server's env in your agent's MCP configuration."
            )
        self._client = httpx.AsyncClient(
            base_url=base_url.rstrip("/"),
            headers={"Authorization": f"Bearer {api_key}", "User-Agent": USER_AGENT, "Accept": "application/json"},
            timeout=CALL_TIMEOUT,
            transport=transport,
        )

    @classmethod
    def from_env(cls) -> "DumpTriageApi":
        return cls(os.environ.get("DUMPTRIAGE_API_KEY", ""), os.environ.get("DUMPTRIAGE_API_URL") or DEFAULT_BASE_URL)

    async def aclose(self) -> None:
        await self._client.aclose()

    async def get_json(self, path: str, params: dict[str, Any] | None = None) -> tuple[Any, httpx.Headers]:
        clean = {k: v for k, v in (params or {}).items() if v is not None}
        response = _check(await self._client.get(path, params=clean))
        return response.json(), response.headers

    async def upload(
        self,
        route: str,
        path: Path,
        fields: dict[str, str],
        on_progress: Callable[[int, int], Any] | None = None,
        content_sha256_header: bool = True,
    ) -> Any:
        """Stream a local file to a multipart upload route, with an Idempotency-Key (and, for dumps, X-Content-SHA256 so
        bytes cut or changed on the way are refused, not analysed)."""
        digest = await asyncio.to_thread(sha256_file, path)
        size = path.stat().st_size
        headers = {"Idempotency-Key": idempotency_key(digest, fields)}
        if content_sha256_header:
            headers["X-Content-SHA256"] = digest
        with path.open("rb") as raw:
            reader = _CountingReader(raw)
            request = self._client.post(
                route,
                files={"file": (path.name, reader, "application/octet-stream")},
                data=fields,
                headers=headers,
                timeout=UPLOAD_TIMEOUT,
            )
            if on_progress is None:
                response = await request
            else:
                response = await _with_progress(request, lambda: on_progress(reader.sent, size))
        return _check(response).json()


async def _with_progress(request, report: Callable[[], Any], interval: float = 2.0):
    """Await the request, calling report() every interval while it runs: a client that times out on silence keeps the
    call alive while a large file streams (the lesson of website#4335)."""
    task = asyncio.ensure_future(request)
    try:
        while True:
            done, _ = await asyncio.wait({task}, timeout=interval)
            if done:
                return task.result()
            await report()
    finally:
        if not task.done():
            task.cancel()
