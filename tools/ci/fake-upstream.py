#!/usr/bin/env python3
"""Fake OpenAI-compatible inference engine for CI smoke tests.

Speaks just enough of the engine surface for the router to run end to end:

  GET  /v1/models          one loaded model, shaped like the engines we route to
  GET  /props              capacity signals the router reads for admission
  POST /v1/chat/completions   streaming (SSE) and non-streaming
  POST /v1/completions        streaming (SSE) and non-streaming

Environment:
  FIXTURE_PORT   listen port (default 18123)
  FIXTURE_MODEL  model id reported by /v1/models (default fixture-model)
  FIXTURE_FAIL   when set, every completion returns 503 (error-path checks)

Single-threaded on purpose: smoke tests issue a handful of requests.
"""
import json
import os
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ.get("FIXTURE_PORT", "18123"))
MODEL = os.environ.get("FIXTURE_MODEL", "fixture-model")
FAIL = bool(os.environ.get("FIXTURE_FAIL"))
REPLY = "fixture-ok"


def completion_body(model, text, stream_id):
    return {
        "id": stream_id,
        "object": "chat.completion",
        "created": int(time.time()),
        "model": model,
        "choices": [
            {
                "index": 0,
                "message": {"role": "assistant", "content": text},
                "finish_reason": "stop",
            }
        ],
        "usage": {"prompt_tokens": 8, "completion_tokens": 4, "total_tokens": 12},
    }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # keep CI logs readable
        print(f"[fixture] {fmt % args}", flush=True)

    def _send(self, code, payload, content_type="application/json"):
        body = payload.encode() if isinstance(payload, str) else json.dumps(payload).encode()
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _fail_if_requested(self):
        if FAIL:
            self._send(503, {"error": {"message": "fixture unavailable", "code": "unavailable"}})
            return True
        return False

    def do_GET(self):
        # The router's catalogue probe asks for /models (llama.cpp convention) while the
        # inference endpoint lives under /v1 — serve both until the probe path is configurable.
        if self.path.startswith("/v1/models") or self.path.startswith("/models"):
            self._send(
                200,
                {
                    "object": "list",
                    "data": [
                        {
                            "id": MODEL,
                            "object": "model",
                            "status": {"value": "loaded"},
                            "meta": {"n_ctx": 8192},
                            "architecture": {"input_modalities": ["text"], "output_modalities": ["text"]},
                        }
                    ],
                },
            )
        elif self.path.startswith("/props"):
            self._send(200, {"total_slots": 2, "default_generation_settings": {"n_ctx": 8192}})
        elif self.path.startswith("/health"):
            self._send(200, "ok", "text/plain")
        else:
            self._send(404, {"error": {"message": "not found"}})

    def _read_request(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b"{}"
        try:
            data = json.loads(raw or b"{}")
        except json.JSONDecodeError:
            data = {}
        return data.get("model") or MODEL, bool(data.get("stream"))

    def _sse(self, model, chunks):
        # Chunked framing without an explicit terminator would leave a plain `curl` waiting for
        # the connection to close; close it so a caller reading the whole body can finish.
        self.close_connection = True
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        for i, part in enumerate(chunks):
            frame = {
                "id": f"chatcmpl-fixture-{i}",
                "object": "chat.completion.chunk",
                "created": int(time.time()),
                "model": model,
                "choices": [{"index": 0, "delta": {"content": part}, "finish_reason": None}],
            }
            self.wfile.write(f"data: {json.dumps(frame)}\n\n".encode())
            self.wfile.flush()
        final = {
            "id": f"chatcmpl-fixture-{len(chunks)}",
            "object": "chat.completion.chunk",
            "created": int(time.time()),
            "model": model,
            "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
        }
        self.wfile.write(f"data: {json.dumps(final)}\n\n".encode())
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def do_POST(self):
        if self.path.startswith("/__fail"):  # CI control switch: start returning 503s
            global FAIL
            FAIL = True
            self._send(200, {"fail": True})
            return
        model, stream = self._read_request()
        if self._fail_if_requested():
            return
        if stream:
            # Several frames, so a relay that buffers would show up in the smoke test.
            self._sse(model, ["fixture", "-", "ok"])
            return
        if self.path.startswith("/v1/completions"):
            self._send(
                200,
                {
                    "id": "cmpl-fixture",
                    "object": "text_completion",
                    "model": model,
                    "choices": [{"index": 0, "text": REPLY, "finish_reason": "stop"}],
                },
            )
        else:
            self._send(200, completion_body(model, REPLY, "chatcmpl-fixture"))


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"[fixture] listening on 127.0.0.1:{PORT} model={MODEL} fail={FAIL}", flush=True)
    server.serve_forever()
