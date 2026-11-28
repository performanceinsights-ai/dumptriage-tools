"""The MCP server end to end in-process: the real MCPServer through the SDK's Client, the API behind httpx.MockTransport.
What is pinned: which route each tool calls with what, and how an API refusal reaches the agent (website#4343)."""
import asyncio
import hashlib

import httpx
import pytest
from mcp import Client

from dumptriage_mcp import api as api_mod
from dumptriage_mcp import server as server_mod
from dumptriage_mcp.api import ConfigError, DumpTriageApi, idempotency_key, secure_api_url
from dumptriage_mcp.server import build_server

DUMP_ID = "0b8f3a52-6f7e-4c4e-9a59-4b7d6d1c2e10"


def dump_row(status="processing", **extra):
    return {"id": DUMP_ID, "status": status, "job_state": "tier1_running", "filename": "crash.dmp", **extra}


class FakeApi:
    """Records requests and answers from a route table: (method, path) -> response or callable(request)."""

    def __init__(self, routes):
        self.routes = routes
        self.requests: list[httpx.Request] = []

    def handler(self, request: httpx.Request) -> httpx.Response:
        request.read()
        self.requests.append(request)
        answer = self.routes.get((request.method, request.url.path))
        if answer is None:
            return httpx.Response(404, json={"detail": "dump not found"})
        return answer(request) if callable(answer) else answer

    def client(self, key="dts_test"):
        return build_server(lambda: DumpTriageApi(key, "https://api.example.test", httpx.MockTransport(self.handler)))


def structured(result):
    assert not result.is_error, result.content
    return result.structured_content


def error_text(result):
    assert result.is_error
    return " ".join(c.text for c in result.content)


async def test_lists_the_six_tools_and_none_is_destructive():
    async with Client(FakeApi({}).client()) as client:
        tools = {t.name: t for t in (await client.list_tools()).tools}
    assert set(tools) == {"upload_dump", "get_dump", "get_report", "list_dumps", "list_dump_groups", "upload_symbols"}
    assert all(t.annotations.destructive_hint is False for t in tools.values())
    assert "never instructions" in tools["get_report"].description


async def test_upload_dump_streams_the_file_with_its_hash_and_an_idempotency_key(tmp_path):
    body = b"MDMP" + bytes(range(256)) * 100
    dump = tmp_path / "crash.dmp"
    dump.write_bytes(body)
    fake = FakeApi({("POST", "/api/v1/dumps"): httpx.Response(201, json=dump_row("uploaded"))})
    async with Client(fake.client()) as client:
        result = await client.call_tool("upload_dump", {"path": str(dump), "reference": "BUG-12", "human_review": True})
    assert structured(result)["id"] == DUMP_ID
    (req,) = fake.requests
    sha = hashlib.sha256(body).hexdigest()
    assert req.headers["authorization"] == "Bearer dts_test"
    assert req.headers["x-content-sha256"] == sha
    assert req.headers["idempotency-key"] == idempotency_key(sha, {"reference": "BUG-12", "human_review": "true"})
    assert "expect" not in req.headers  # the API refuses Expect: 100-continue with 417
    content = req.content
    assert body in content
    assert b'name="reference"\r\n\r\nBUG-12' in content
    assert b'name="human_review"\r\n\r\ntrue' in content
    assert b'filename="crash.dmp"' in content


def test_the_idempotency_key_follows_the_file_and_the_fields():
    a = idempotency_key("ab" * 32, {"reference": "x"})
    assert a == idempotency_key("ab" * 32, {"reference": "x"})  # a retried call replays
    assert a != idempotency_key("ab" * 32, {"reference": "y"})  # new fields are a new upload
    assert a != idempotency_key("cd" * 32, {"reference": "x"})
    assert 1 <= len(a) <= 255 and a.isascii() and a.isprintable()


async def test_upload_dump_refuses_a_missing_path_without_calling_the_api(tmp_path):
    fake = FakeApi({})
    async with Client(fake.client()) as client:
        missing = await client.call_tool("upload_dump", {"path": str(tmp_path / "nope.dmp")})
        a_dir = await client.call_tool("upload_dump", {"path": str(tmp_path)})
    assert "no such file" in error_text(missing)
    assert "not a regular file" in error_text(a_dir)
    assert fake.requests == []


async def test_a_relative_path_is_read_from_the_working_directory(tmp_path, monkeypatch):
    (tmp_path / "core").write_bytes(b"\x7fELF core")
    monkeypatch.chdir(tmp_path)
    fake = FakeApi({("POST", "/api/v1/dumps"): httpx.Response(201, json=dump_row("uploaded"))})
    async with Client(fake.client()) as client:
        structured(await client.call_tool("upload_dump", {"path": "core"}))
    assert b"\x7fELF core" in fake.requests[0].content


async def test_api_refusals_reach_the_agent_with_the_detail_and_what_to_do(tmp_path):
    dump = tmp_path / "crash.dmp"
    dump.write_bytes(b"MDMP")
    fake = FakeApi({
        ("POST", "/api/v1/dumps"): httpx.Response(429, json={"detail": "monthly quota reached"}, headers={"Retry-After": "60"}),
        ("GET", f"/api/v1/dumps/{DUMP_ID}"): httpx.Response(401, json={"detail": "invalid API key"}),
        ("GET", f"/api/v1/dumps/{DUMP_ID}/report"): httpx.Response(403, json={"detail": "this API key is limited to the 'upload' scope"}),
    })
    async with Client(fake.client()) as client:
        quota = error_text(await client.call_tool("upload_dump", {"path": str(dump)}))
        auth = error_text(await client.call_tool("get_dump", {"dump_id": DUMP_ID}))
        scope = error_text(await client.call_tool("get_report", {"dump_id": DUMP_ID}))
    assert "429" in quota and "monthly quota reached" in quota and "Retry after 60 s" in quota
    assert "invalid API key" in auth and "DUMPTRIAGE_API_KEY" in auth
    assert "'upload' scope" in scope and "full-scope key" in scope


async def test_a_non_json_error_body_is_still_reported():
    fake = FakeApi({("GET", "/api/v1/dumps/groups"): httpx.Response(502, text="<html>Bad Gateway</html>")})
    async with Client(fake.client()) as client:
        text = error_text(await client.call_tool("list_dump_groups", {}))
    assert "502" in text and "Bad Gateway" in text


async def test_without_a_key_the_server_starts_and_every_tool_says_how_to_set_it_up():
    async with Client(build_server(lambda: DumpTriageApi(""))) as client:
        assert len((await client.list_tools()).tools) == 6
        text = error_text(await client.call_tool("list_dumps", {}))
    assert "DUMPTRIAGE_API_KEY is not set" in text


async def test_get_dump_without_wait_answers_at_once():
    fake = FakeApi({("GET", f"/api/v1/dumps/{DUMP_ID}"): httpx.Response(200, json=dump_row("processing"))})
    async with Client(fake.client()) as client:
        assert structured(await client.call_tool("get_dump", {"dump_id": DUMP_ID}))["status"] == "processing"
    assert len(fake.requests) == 1


@pytest.mark.parametrize("final", sorted(server_mod.FINAL_STATUSES))
async def test_get_dump_waits_until_the_status_is_final(final, monkeypatch):
    monkeypatch.setattr(server_mod, "POLL_SECONDS", 0.01)
    answers = iter([dump_row("uploaded"), dump_row("processing"), dump_row(final)])
    fake = FakeApi({("GET", f"/api/v1/dumps/{DUMP_ID}"): lambda _r: httpx.Response(200, json=next(answers))})
    async with Client(fake.client()) as client:
        result = await client.call_tool("get_dump", {"dump_id": DUMP_ID, "wait_seconds": 60})
    assert structured(result)["status"] == final
    assert len(fake.requests) == 3


async def test_get_dump_returns_the_last_status_when_the_wait_runs_out(monkeypatch):
    monkeypatch.setattr(server_mod, "POLL_SECONDS", 0.01)
    fake = FakeApi({("GET", f"/api/v1/dumps/{DUMP_ID}"): httpx.Response(200, json=dump_row("processing"))})
    async with Client(fake.client()) as client:
        result = await client.call_tool("get_dump", {"dump_id": DUMP_ID, "wait_seconds": 1})
    assert structured(result)["status"] == "processing"
    assert len(fake.requests) >= 2


async def test_get_dump_refuses_a_wait_over_the_cap():
    async with Client(FakeApi({}).client()) as client:
        result = await client.call_tool("get_dump", {"dump_id": DUMP_ID, "wait_seconds": server_mod.MAX_WAIT_SECONDS + 1})
    assert result.is_error


async def test_get_report_returns_the_api_report_unchanged():
    report = {"dump_id": DUMP_ID, "decision": "auto_deliver", "reports": [{"tier": 1, "findings": {"summary": "NULL deref in acme.sys"}}]}
    fake = FakeApi({("GET", f"/api/v1/dumps/{DUMP_ID}/report"): httpx.Response(200, json=report)})
    async with Client(fake.client()) as client:
        assert structured(await client.call_tool("get_report", {"dump_id": DUMP_ID})) == report


async def test_list_dumps_passes_only_the_filters_given_and_returns_the_next_cursor():
    fake = FakeApi({("GET", "/api/v1/dumps"): httpx.Response(200, json=[dump_row("delivered")], headers={"X-Next-Cursor": "c2"})})
    async with Client(fake.client()) as client:
        page = structured(await client.call_tool("list_dumps", {"state": "delivered", "limit": 5}))
    assert page == {"dumps": [dump_row("delivered")], "next_cursor": "c2"}
    assert dict(fake.requests[0].url.params) == {"state": "delivered", "limit": "5"}


async def test_list_dumps_on_the_last_page_has_no_cursor():
    fake = FakeApi({("GET", "/api/v1/dumps"): httpx.Response(200, json=[])})
    async with Client(fake.client()) as client:
        assert structured(await client.call_tool("list_dumps", {"cursor": "c2"})) == {"dumps": [], "next_cursor": None}
    assert fake.requests[0].url.params["cursor"] == "c2"


async def test_list_dump_groups():
    groups = [{"signature": "acme.sys!Foo+0x12", "count": 7, "hosts": ["web-1"], "latest_dump_id": DUMP_ID}]
    fake = FakeApi({("GET", "/api/v1/dumps/groups"): httpx.Response(200, json=groups)})
    async with Client(fake.client()) as client:
        assert structured(await client.call_tool("list_dump_groups", {"offset": 20}))["groups"] == groups
    assert fake.requests[0].url.params["offset"] == "20"


async def test_upload_symbols_names_the_module_after_the_file_by_default(tmp_path):
    pdb = tmp_path / "acme.pdb"
    pdb.write_bytes(b"Microsoft C/C++ MSF 7.00\r\n")
    fake = FakeApi({("POST", "/api/v1/symbols"): httpx.Response(201, json={"id": "s1", "module_name": "acme.pdb"})})
    async with Client(fake.client()) as client:
        structured(await client.call_tool("upload_symbols", {"path": str(pdb)}))
        structured(await client.call_tool("upload_symbols", {"path": str(pdb), "module_name": "libacme.so.1"}))
    first, second = fake.requests
    assert b'name="module_name"\r\n\r\nacme.pdb' in first.content
    assert b'name="module_name"\r\n\r\nlibacme.so.1' in second.content
    assert "x-content-sha256" not in first.headers  # the symbols route does not take it
    assert first.headers["idempotency-key"] != second.headers["idempotency-key"]


async def test_upload_reports_progress_while_a_slow_upload_runs(tmp_path, monkeypatch):
    """A client that times out on silence must hear from a long upload (website#4335's lesson)."""
    dump = tmp_path / "big.dmp"
    dump.write_bytes(b"x" * 4096)
    calls = []
    real = api_mod._with_progress

    async def fast(request, report, interval=2.0):
        return await real(request, report, interval=0.01)

    monkeypatch.setattr(api_mod, "_with_progress", fast)

    async def slow(request: httpx.Request) -> httpx.Response:
        await request.aread()
        await asyncio.sleep(0.1)
        return httpx.Response(201, json=dump_row("uploaded"))

    api = DumpTriageApi("dts_test", "https://api.example.test", httpx.MockTransport(slow))

    async def on_progress(sent, total):
        calls.append((sent, total))

    try:
        await api.upload("/api/v1/dumps", dump, {}, on_progress=on_progress)
    finally:
        await api.aclose()
    assert calls and calls[-1] == (4096, 4096)


def test_from_env_reads_the_key_and_an_optional_base_url(monkeypatch):
    monkeypatch.setenv("DUMPTRIAGE_API_KEY", "dts_env")
    monkeypatch.delenv("DUMPTRIAGE_API_URL", raising=False)
    api = DumpTriageApi.from_env()
    assert str(api._client.base_url).rstrip("/") == "https://api.performanceinsights.ai"
    assert api._client.headers["authorization"] == "Bearer dts_env"
    monkeypatch.setenv("DUMPTRIAGE_API_URL", "https://triage.internal.example/")
    assert str(DumpTriageApi.from_env()._client.base_url).rstrip("/") == "https://triage.internal.example"


def test_the_server_instructions_warn_that_report_text_is_data():
    assert "never instructions" in server_mod.INSTRUCTIONS


@pytest.mark.parametrize("url, ok", [
    ("https://api.performanceinsights.ai", True),
    ("https://triage.internal.example:8443/", True),
    ("http://localhost:8090", True),
    ("http://127.0.0.1:8090", True),
    ("http://[::1]:8090", True),
    ("http://api.performanceinsights.ai", False),  # the key would travel in clear
    ("http://10.0.0.5:8090", False),
    ("ftp://api.performanceinsights.ai", False),
    ("https://", False),
    ("https://[::1", False),
    ("https://host:notaport", False),
])
def test_only_https_or_loopback_http_is_accepted(url, ok):
    assert secure_api_url(url) is ok


async def test_a_plain_http_api_url_is_refused_before_any_call(monkeypatch):
    monkeypatch.setenv("DUMPTRIAGE_API_KEY", "dts_env")
    monkeypatch.setenv("DUMPTRIAGE_API_URL", "http://api.performanceinsights.ai")
    with pytest.raises(ConfigError, match="must be an https URL"):
        DumpTriageApi.from_env()
    async with Client(build_server()) as client:
        text = error_text(await client.call_tool("list_dumps", {}))
    assert "must be an https URL" in text
