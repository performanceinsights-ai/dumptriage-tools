"""A stand-in for the service's POST /api/v1/traces, for running dumptriage-collect.sh with its real tools on a macOS
runner (the macOS trace capture workflow): saves each upload's manifest and file where the workflow can read them, and
answers like the service does. Not a test of the service; the contract is website's /api/v1/traces."""
import email.parser
import email.policy
import json
import pathlib
import sys
import uuid
from http.server import BaseHTTPRequestHandler, HTTPServer

OUT = pathlib.Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)


class Handler(BaseHTTPRequestHandler):
    def _reply(self, status, body):
        data = json.dumps(body).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._reply(200, {"signup_enabled": False}) if self.path == "/api/v1/config" else self._reply(404, {"detail": "no"})

    def do_POST(self):
        if self.path != "/api/v1/traces" or not self.headers.get("Authorization", "").startswith("Bearer "):
            return self._reply(404, {"detail": "no"})
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        message = email.parser.BytesParser(policy=email.policy.default).parsebytes(
            b"Content-Type: " + self.headers["Content-Type"].encode() + b"\r\n\r\n" + raw
        )
        trace_id = str(uuid.uuid4())
        for part in message.iter_parts():
            name = part.get_param("name", header="content-disposition")
            (OUT / f"{trace_id}.{name}").write_bytes(part.get_payload(decode=True))
        self._reply(201, {"id": trace_id})

    def log_message(self, *args):
        pass


HTTPServer(("127.0.0.1", int(sys.argv[2])), Handler).serve_forever()
