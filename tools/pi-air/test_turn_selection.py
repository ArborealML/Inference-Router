"""Check AIR turn selection with the installed Pi and a loopback router fixture."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
from queue import Empty, Queue
import subprocess
import tempfile
import threading
import time
import unittest


ROOT = Path(__file__).resolve().parent
WINDOWS = {"narrow": 32768, "standard": 65536, "wide": 131072}
MODELS = ["hy4.fixture", "qwen.fixture", "child.fixture"]
HELD_PROMPTS = {"start Hy4", "interrupt Hy4", "independent child"}


def message_text(content):
    if isinstance(content, str):
        return content
    return "".join(part["text"] for part in content if part["type"] == "text")


class FakeRouter(ThreadingHTTPServer):
    def __init__(self):
        super().__init__(("127.0.0.1", 0), RouterHandler)
        self.posts = []
        self.lock = threading.Lock()
        self.stop_feed = threading.Event()

    @contextmanager
    def running(self):
        with self:
            worker = threading.Thread(target=self.serve_forever)
            worker.start()
            try:
                yield self
            finally:
                self.stop_feed.set()
                self.shutdown()
                worker.join()

    def record(self, request):
        with self.lock:
            self.posts.append(request)
            return sum(post["thread"] == request["thread"] and post["user"] == request["user"]
                       for post in self.posts)

    def snapshot(self):
        with self.lock:
            return list(self.posts)


class RouterHandler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path.startswith("/admin/events?"):
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            self.server.stop_feed.wait()
            return
        if self.path == "/admin/catalog":
            profiles = [{"Name": name, "MaxContext": window, "MaxSlots": 1}
                        for name, window in WINDOWS.items()]
            models = [{"Id": model, "DefaultProfile": "standard", "Aliases": [], "Profiles": profiles}
                      for model in MODELS]
            self.write_response(200, "application/json", json.dumps({
                "Instance": "fixture", "Generation": 1, "DefaultModel": MODELS[0],
                "Engines": [{"Name": "fake", "Models": models}],
            }).encode())
        elif self.path == "/admin/status":
            self.write_response(200, "application/json", b'{"Engines":[]}')
        else:
            self.send_error(404)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        messages = body.get("messages", [])
        users = [message_text(message["content"]) for message in messages if message["role"] == "user"]
        last_user = users[-1] if users else None
        count = self.server.record({
            "path": self.path, "model": body.get("model"), "user": last_user, "users": users,
            "thread": self.headers["X-Inference-Thread"], "profile": self.headers["X-Inference-Profile"],
            "context": self.headers["X-Inference-Required-Context"],
            "has_tool_result": any(message["role"] == "tool" for message in messages),
        })
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        if last_user == "start Hy4" and count == 2:
            self.write_response(503, "application/json", b'{"error":{"message":"fixture overloaded"}}')
            return
        if last_user in HELD_PROMPTS and count == 1:
            delta = {"role": "assistant", "tool_calls": [{
                "index": 0, "id": "call_fixture", "type": "function",
                "function": {"name": "fixture_hold", "arguments": "{}"},
            }]}
            finish = "tool_calls"
        else:
            delta = {"role": "assistant", "content": "ok"}
            finish = "stop"
        chunk = json.dumps({"choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}).encode()
        self.write_response(200, "text/event-stream", b"data: " + chunk + b"\n\ndata: [DONE]\n\n")

    def write_response(self, status, mime, body):
        self.send_response(status)
        self.send_header("Content-Type", mime)
        self.end_headers()
        self.wfile.write(body)


class Pi:
    def __init__(self, process):
        self.process = process
        self.events = Queue()
        self.seen = []
        self.counter = 0
        self.reader = threading.Thread(target=self.read_events)
        self.reader.start()

    def read_events(self):
        for line in self.process.stdout:
            self.events.put(json.loads(line))
        self.events.put({"type": "fixture_eof"})

    def wait(self, predicate):
        deadline = time.monotonic() + 12
        try:
            while True:
                event = self.events.get(timeout=max(0, deadline - time.monotonic()))
                if event["type"] in {"extension_error", "fixture_eof"}:
                    raise AssertionError(event)
                self.seen.append(event)
                if predicate(event):
                    return event
                if time.monotonic() >= deadline:
                    raise Empty
        except Empty:
            raise AssertionError(f"Pi event timed out. Recent event types: "
                                 f"{[event['type'] for event in self.seen[-20:]]}") from None

    def send(self, command, **kwargs):
        self.counter += 1
        request_id = str(self.counter)
        self.process.stdin.write(json.dumps({"type": command, "id": request_id, **kwargs}) + "\n")
        self.process.stdin.flush()
        response = self.wait(lambda event: event["type"] == "response" and event["id"] == request_id)
        if not response["success"]:
            raise AssertionError(response)
        return response.get("data")

    def prompt(self, message):
        return self.send("prompt", message=message)

    def hold(self, message):
        self.prompt(message)
        self.wait(lambda event: event.get("message") == "Tool held")

    def model(self, model):
        self.send("set_model", provider="air", modelId=model)

    def settled(self):
        return self.wait(lambda event: event["type"] == "agent_settled")

    def close(self):
        self.process.terminate()
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.reader.join()


@contextmanager
def pi_session(router, compaction=None):
    with tempfile.TemporaryDirectory() as directory:
        (Path(directory) / "settings.json").write_text(json.dumps({
            "retry": {"enabled": True, "maxRetries": 1, "baseDelayMs": 10, "provider": {"maxRetries": 0}},
            "compaction": compaction or {"enabled": False},
        }))
        entry = str(ROOT / "index.mjs")
        env = {**os.environ, "PI_CODING_AGENT_DIR": directory, "PI_OFFLINE": "1",
               "PI_AIR_BASE_URL": f"http://127.0.0.1:{router.server_port}/v1",
               "PI_AIR_FIXTURE_EXTENSION": entry}
        env.pop("AIR_API_KEY", None)
        args = ["pi", "--no-extensions", "-e", entry, "-e", str(ROOT / "turn-fixture.mjs"),
                "--mode", "rpc", "--tools", "fixture_hold", "--no-session", "--provider", "air",
                "--model", MODELS[0]]
        with subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              text=True, env=env, cwd=directory) as process:
            pi = Pi(process)
            try:
                yield pi
            finally:
                pi.close()


class TurnSelectionTest(unittest.TestCase):
    def check_requests(self, requests, thread, expected):
        self.assertTrue(thread)
        self.assertEqual([
            ("/v1/chat/completions", thread, model, profile, str(WINDOWS[profile]), user, has_tool)
            for model, profile, user, has_tool in expected
        ], [
            (post["path"], post["thread"], post["model"], post["profile"], post["context"],
             post["user"], post["has_tool_result"])
            for post in requests
        ])

    def test_held_tool_retry_and_native_follow_up(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.prompt("/context narrow")
            pi.hold("start Hy4")
            pi.prompt("/context wide")
            pi.model("qwen.fixture")
            self.assertEqual("queued", pi.send("follow_up", message="next user")["disposition"])
            pi.prompt("/context wide")
            self.check_requests(router.snapshot(), thread, [("hy4.fixture", "narrow", "start Hy4", False)])
            pi.prompt("/fixture-release")
            self.assertFalse(pi.settled()["aborted"])
            requests = router.snapshot()
            self.check_requests(requests, thread, [
                ("hy4.fixture", "narrow", "start Hy4", False),
                ("hy4.fixture", "narrow", "start Hy4", True),
                ("hy4.fixture", "narrow", "start Hy4", True),
                ("qwen.fixture", "wide", "next user", True),
            ])
            self.assertEqual(["start Hy4", "next user"], requests[-1]["users"])
            self.assertEqual(1, sum(event["type"] == "auto_retry_start" for event in pi.seen))

    def test_abort_releases_selection_and_keeps_other_session_independent(self):
        with FakeRouter().running() as router, pi_session(router) as pi, pi_session(router) as other:
            thread = pi.send("get_state")["sessionId"]
            other_thread = other.send("get_state")["sessionId"]
            self.assertNotEqual(thread, other_thread)
            pi.hold("interrupt Hy4")
            pi.model("qwen.fixture")
            pi.prompt("/context wide")
            other.model("child.fixture")
            other.prompt("/context narrow")
            other.prompt("parallel user")
            other.settled()
            pi.send("abort")
            self.assertTrue(any(event["type"] == "agent_settled" and event["aborted"] for event in pi.seen))
            self.assertEqual(2, len(router.snapshot()))
            pi.prompt("/fixture-custom")
            pi.settled()
            requests = router.snapshot()
            self.check_requests(requests[1:2], other_thread, [("child.fixture", "narrow", "parallel user", False)])
            self.check_requests(requests[:1] + requests[2:], thread, [
                ("hy4.fixture", "standard", "interrupt Hy4", False),
                ("qwen.fixture", "wide", "Run a custom turn", True),
            ])

    def test_pending_model_defers_automatic_compaction(self):
        settings = {"enabled": True, "reserveTokens": 66000, "keepRecentTokens": 1}
        with FakeRouter().running() as router, pi_session(router, settings) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.prompt("/context wide")
            pi.prompt("Earlier user message")
            pi.settled()
            pi.hold("interrupt Hy4")
            pi.model("qwen.fixture")
            pi.prompt("/fixture-release")
            pi.settled()
            self.assertTrue(any(event["type"] == "compaction_end" and event["reason"] == "threshold"
                                and event["aborted"] for event in pi.seen))
            self.check_requests(router.snapshot(), thread, [
                ("hy4.fixture", "wide", "Earlier user message", False),
                ("hy4.fixture", "wide", "interrupt Hy4", False),
                ("hy4.fixture", "wide", "interrupt Hy4", True),
            ])
            pi.send("compact")
            summaries = router.snapshot()[3:]
            self.assertTrue(summaries)
            for post in summaries:
                self.assertEqual(("qwen.fixture", "standard", "65536", thread),
                                 (post["model"], post["profile"], post["context"], post["thread"]))

    def test_sdk_children_inherit_callback_or_load_air_independently(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("child.fixture")
            pi.prompt("/context wide")
            pi.model("hy4.fixture")
            pi.hold("interrupt Hy4")
            pi.prompt("/fixture-child inherited")
            pi.prompt("/fixture-child independent")
            children = [event["message"].removeprefix("Child thread ") for event in pi.seen
                        if isinstance(event.get("message"), str) and event["message"].startswith("Child thread ")]
            self.assertEqual(3, len({thread, *children}))
            requests = router.snapshot()
            self.check_requests(requests[:1], thread, [("hy4.fixture", "standard", "interrupt Hy4", False)])
            self.check_requests(requests[1:2], children[0], [("child.fixture", "standard", "inherited child", False)])
            self.check_requests(requests[2:], children[1], [
                ("child.fixture", "narrow", "independent child", False),
                ("child.fixture", "narrow", "independent child", True),
            ])
            pi.send("abort")
            self.assertEqual(requests, router.snapshot())


if __name__ == "__main__":
    unittest.main()
