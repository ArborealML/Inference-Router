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
WINDOWS = {"compact": 8192, "narrow": 32768, "standard": 65536, "wide": 131072}
MODELS = ["hy4.fixture", "qwen.fixture", "child.fixture", "mixed.fixture", "qwen-3.8-27b"]
LARGE_PROMPT = "Large input " + "x" * 35000
CAP_PROMPT = "Start capped turn"
CAP_FIT_PROMPT = "Near window with cap " + "x" * 26000
THRESHOLD_PROMPT = "Cross only the pending threshold " + "x" * 16000
HELD_PROMPTS = {"start Hy4", "interrupt Hy4", "independent child", "start Hy4 compact",
                "mixed default turn", LARGE_PROMPT, CAP_PROMPT, THRESHOLD_PROMPT}


def message_text(content):
    if isinstance(content, str):
        return content
    return "".join(part["text"] for part in content if part["type"] == "text")


class FakeRouter(ThreadingHTTPServer):
    def __init__(self, retry_summaries=False):
        super().__init__(("127.0.0.1", 0), RouterHandler)
        self.posts = []
        self.lock = threading.Lock()
        self.stop_feed = threading.Event()
        self.held_response_started = threading.Event()
        self.release_held_response = threading.Event()
        self.retry_summaries = retry_summaries

    @contextmanager
    def running(self):
        with self:
            worker = threading.Thread(target=self.serve_forever)
            worker.start()
            try:
                yield self
            finally:
                self.stop_feed.set()
                self.release_held_response.set()
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
                "Engines": [
                    {"Name": "fake", "Models": models},
                    {"Name": "second", "Models": [
                        {"Id": "mixed.fixture", "DefaultProfile": "wide", "Aliases": [], "Profiles": profiles},
                    ]},
                ],
            }).encode())
        elif self.path in {"/admin/tickets/cancel-test", "/admin/tickets/fail-test"}:
            cancelled = self.path.endswith("cancel-test")
            state = {"Id": self.path.split("/")[-1], "ThreadId": self.server.snapshot()[-1]["thread"],
                     "State": "Cancelled" if cancelled else "Failed",
                     "ErrorCode": "cancelled" if cancelled else "switch_failed"}
            self.write_response(200, "application/json", json.dumps(state).encode())
        elif self.path == "/admin/status":
            self.write_response(200, "application/json", b'{"Engines":[]}')
        else:
            self.send_error(404)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        messages = body.get("messages", [])
        summary = any(message["role"] == "system" and
                      message_text(message["content"]).startswith("You are a context summarization assistant.")
                      for message in messages)
        users = [message_text(message["content"]) for message in messages if message["role"] == "user"]
        last_user = users[-1] if users else None
        count = self.server.record({
            "template": body.get("chat_template_kwargs", {}),
            "path": self.path, "model": body.get("model"), "user": last_user, "users": users,
            "thread": self.headers["X-Inference-Thread"], "profile": self.headers["X-Inference-Profile"],
            "context": self.headers["X-Inference-Required-Context"], "output_limit": body.get("max_tokens"),
            "has_tool_result": any(message["role"] == "tool" for message in messages), "summary": summary,
        })
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        if (last_user in {"start Hy4", CAP_PROMPT} and count == 2) or (summary and self.server.retry_summaries and count == 1):
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
        event = {"choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
        if last_user in {"held speed", "second speed"}:
            rate = 111 if last_user == "held speed" else 222
            event["timings"] = {"prompt_per_second": rate, "predicted_per_second": rate / 10}
        chunk = json.dumps(event).encode()
        tickets = {"held speed": "speed-ticket", "inherited child": "inherited-ticket", "direct registry call": "registry-ticket"}
        self.write_response(200, "text/event-stream", b"data: " + chunk + b"\n\ndata: [DONE]\n\n",
                            ticket={**tickets, "cancel status": "cancel-test", "fail status": "fail-test"}.get(last_user))

    def write_response(self, status, mime, body, ticket=None):
        self.send_response(status)
        self.send_header("Content-Type", mime)
        if ticket:
            self.send_header("X-Inference-Ticket", ticket)
        self.end_headers()
        if ticket:
            self.wfile.flush()
            self.server.held_response_started.set()
            self.server.release_held_response.wait(12)
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

    def request(self, command, **kwargs):
        self.counter += 1
        request_id = str(self.counter)
        self.process.stdin.write(json.dumps({"type": command, "id": request_id, **kwargs}) + "\n")
        self.process.stdin.flush()
        return request_id

    def response(self, request_id):
        response = self.wait(lambda event: event["type"] == "response" and event["id"] == request_id)
        if not response["success"]:
            raise AssertionError(response)
        return response.get("data")

    def send(self, command, **kwargs):
        return self.response(self.request(command, **kwargs))

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
    def test_overlapping_sessions_and_next_request_keep_rates_separate(self):
        def rates(pi, offset=0):
            return [event["statusText"] for event in pi.seen[offset:]
                    if event.get("method") == "setStatus" and "prefill" in (event.get("statusText") or "")]

        with FakeRouter().running() as router, pi_session(router) as first, pi_session(router) as second:
            first.prompt("held speed")
            self.assertTrue(router.held_response_started.wait(5))
            second.prompt("second speed")
            second.settled()
            self.assertIn("prefill 222.0 · decode 22.2", rates(second)[-1])
            router.release_held_response.set()
            first.settled()
            self.assertIn("prefill 111.0 · decode 11.1", rates(first)[-1])
            self.assertFalse(any("222.0" in row for row in rates(first)))
            offset = len(first.seen)
            first.prompt("no timing data")
            first.settled()
            self.assertTrue(rates(first, offset))
            self.assertTrue(all("prefill — · decode —" in row for row in rates(first, offset)))

    def test_tool_preparation_and_execution_have_distinct_status(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            pi.hold("start Hy4")
            status = [e.get("statusText", "") for e in pi.seen if e.get("method") == "setStatus"]
            labels = [s for s in status if s]
            self.assertTrue(any("Preparing Tool" in s for s in labels), labels)
            self.assertIn("Executing Tool", labels[-1])

    def test_cancelled_ticket_visible_without_error_notification(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            router.release_held_response.set()
            pi.prompt("cancel status")
            pi.settled()
            pi.prompt("/air-status")
            self.assertTrue(any(e.get("method") == "notify" and "Cancelled" in e.get("message", "") for e in pi.seen))
            pi.prompt("fail status")
            pi.wait(lambda e: e.get("notifyType") == "error" and "switch_failed" in e.get("message", ""))
            self.assertFalse(any(e.get("notifyType") == "error" and "Cancelled" in e.get("message", "")
                                 for e in pi.seen))

    def test_installed_model_reasoning_lists(self):
        groups = [
            (["qwen-3.8-27b", "qwen3.8-flash-next-iq3_s", "qwen3.8-flash-next-coder-iq1_m",
              "swift", "swift-1.5-iq3_s"], ["off", "low", "medium", "xhigh"]),
            (["deepseek-v4.1-flash-1017-iq2-xxs", "deepseek-v4.1-flash-1017-maya-xs-v2",
              "glm-5.3-flash"], ["off", "low", "high", "max"]),
            (["hy4-flash"], ["off", "high"]),
            (["minimax-m3.1-flash"], ["off", "low", "medium", "high", "xhigh", "max"]),
        ]
        from unittest.mock import patch
        with patch(__name__ + ".MODELS", MODELS + [m for models, _ in groups for m in models if m not in MODELS]):
            with FakeRouter().running() as router, pi_session(router) as pi:
                for models, expected in groups:
                    for model in models:
                        with self.subTest(model=model):
                            pi.model(model)
                            self.assertEqual(expected, pi.send("get_available_thinking_levels")["levels"])
                            pi.send("set_thinking_level", level=expected[-1])
                            pi.prompt("Check effort")
                            pi.settled()
                            self.assertEqual(expected[-1], router.snapshot()[-1]["template"]["reasoning_effort"])


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

    def test_qwen_27b_high_uses_xhigh_template_effort(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            pi.model("hy4.fixture")
            pi.send("set_thinking_level", level="high")
            for model, expected, levels in [
                ("qwen-3.8-27b", "xhigh", ["off", "low", "medium", "xhigh"]),
                ("hy4.fixture", "high", ["off", "minimal", "low", "medium", "high"]),
            ]:
                pi.model(model)
                self.assertEqual(levels, pi.send("get_available_thinking_levels")["levels"])
                self.assertEqual(expected, pi.send("get_state")["thinkingLevel"])
                pi.prompt("Check effort")
                pi.settled()
                self.assertEqual(expected, router.snapshot()[-1]["template"]["reasoning_effort"])

    def test_held_tool_retry_and_native_follow_up(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.prompt("/context compact")
            pi.hold("start Hy4")
            pi.prompt("/context wide")
            pi.model("qwen.fixture")
            self.assertEqual("queued", pi.send("follow_up", message="next user")["disposition"])
            pi.prompt("/context wide")
            self.check_requests(router.snapshot(), thread, [("hy4.fixture", "compact", "start Hy4", False)])
            pi.prompt("/fixture-release")
            self.assertFalse(pi.settled()["aborted"])
            requests = router.snapshot()
            self.check_requests(requests, thread, [
                ("hy4.fixture", "compact", "start Hy4", False),
                ("hy4.fixture", "compact", "start Hy4", True),
                ("hy4.fixture", "compact", "start Hy4", True),
                ("qwen.fixture", "wide", "next user", True),
            ])
            self.assertEqual([2048, 2048, 2048, 8192], [post["output_limit"] for post in requests])
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

    def test_same_model_keeps_profile_and_output_limit_until_identical_follow_up(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            prompt = "start Hy4 compact"
            pi.prompt("/context compact")
            pi.hold(prompt)
            pi.send("follow_up", message=prompt)
            pi.prompt("/context wide")
            self.assertEqual(1, len(router.snapshot()))
            pi.prompt("/fixture-release")
            pi.settled()
            requests = router.snapshot()
            self.check_requests(requests, thread, [
                ("hy4.fixture", "compact", prompt, False),
                ("hy4.fixture", "compact", prompt, True),
                ("hy4.fixture", "wide", prompt, True),
            ])
            self.assertEqual([2048, 2048, 8192], [post["output_limit"] for post in requests])
            self.assertEqual([prompt, prompt], requests[-1]["users"])

    def test_pending_downgrade_revalidates_at_dispatch(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.prompt("/context wide")
            pi.hold(LARGE_PROMPT)
            pi.prompt("/context compact")
            self.assertTrue(any(event.get("message") == "hy4.fixture · compact · 8192 tokens" for event in pi.seen))
            pi.send("follow_up", message="Too large for the newly selected window")
            pi.prompt("/fixture-release")
            pi.settled()
            self.check_requests(router.snapshot(), thread, [
                ("hy4.fixture", "wide", LARGE_PROMPT, False),
                ("hy4.fixture", "wide", LARGE_PROMPT, True),
            ])
            failures = [event["message"]["errorMessage"] for event in pi.seen
                        if event["type"] == "message_end" and event["message"].get("stopReason") == "error"]
            self.assertEqual(1, len(failures))
            self.assertIn("prompt and output reserve exceed this window", failures[0])

    def test_mixed_defaults_report_ambiguity_and_keep_automatic_routing(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("mixed.fixture")
            pi.hold("mixed default turn")
            pi.prompt("/context wide")
            pi.send("follow_up", message="explicit profile turn")
            pi.prompt("/fixture-release")
            pi.settled()
            requests = router.snapshot()
            self.assertEqual(["mixed.fixture"] * 3, [post["model"] for post in requests])
            self.assertEqual([thread] * 3, [post["thread"] for post in requests])
            self.assertEqual([None, None, "wide"], [post["profile"] for post in requests])
            self.assertEqual(["65536", "65536", "131072"], [post["context"] for post in requests])
            notices = [event["message"] for event in pi.seen
                       if event.get("method") == "notify" and "automatic routing" in event["message"]]
            self.assertEqual(["AIR could not resolve one serving profile for mixed.fixture. "
                              "This turn uses automatic routing with a 65536-token minimum."], notices)

    def test_pending_smaller_window_cannot_trigger_compaction_before_follow_up(self):
        settings = {"enabled": True, "reserveTokens": 7000, "keepRecentTokens": 1}
        with FakeRouter().running() as router, pi_session(router, settings) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("qwen.fixture")
            pi.prompt("/context compact")
            pi.model("hy4.fixture")
            pi.prompt("/context wide")
            pi.prompt("Earlier user message")
            pi.settled()
            pi.hold(THRESHOLD_PROMPT)
            pi.model("qwen.fixture")
            follow_up = "Use the narrower selection"
            pi.send("follow_up", message=follow_up)
            pi.prompt("/fixture-release")
            pi.settled()
            skipped = next(index for index, event in enumerate(pi.seen)
                           if event["type"] == "compaction_end" and event["reason"] == "threshold" and event["aborted"])
            dispatched = next(index for index, event in enumerate(pi.seen) if event["type"] == "message_start"
                              and event["message"]["role"] == "user"
                              and message_text(event["message"]["content"]) == follow_up)
            self.assertLess(skipped, dispatched)
            requests = router.snapshot()
            self.assertEqual([False, False, False, False, True, True], [post["summary"] for post in requests])
            self.check_requests(requests[:4], thread, [
                ("hy4.fixture", "wide", "Earlier user message", False),
                ("hy4.fixture", "wide", THRESHOLD_PROMPT, False),
                ("hy4.fixture", "wide", THRESHOLD_PROMPT, True),
                ("qwen.fixture", "compact", follow_up, True),
            ])
            self.assertEqual([8192, 8192, 8192], [post["output_limit"] for post in requests[:3]])
            self.assertLessEqual(requests[3]["output_limit"], 2048)
            summary_threads = {post["thread"] for post in requests[4:]}
            self.assertTrue(all(summary_threads))
            self.assertEqual(3, len(summary_threads | {thread}))
            for post in requests[4:]:
                self.assertEqual(("qwen.fixture", "compact", "8192"),
                                 (post["model"], post["profile"], post["context"]))

    def test_active_window_can_compact_before_tool_continuation_with_its_own_thread(self):
        settings = {"enabled": True, "reserveTokens": 30000, "keepRecentTokens": 1,
                    "modelOverrides": {"air/hy4.fixture": {"reserveTokens": 62000, "keepRecentTokens": 6000}}}
        with FakeRouter(retry_summaries=True).running() as router, pi_session(router, settings) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("qwen.fixture")
            pi.prompt("/context narrow")
            pi.model("hy4.fixture")
            pi.prompt("Earlier user message")
            pi.settled()
            pi.hold(LARGE_PROMPT)
            pi.model("qwen.fixture")
            pi.prompt("/fixture-release")
            pi.settled()
            pi.prompt("/fixture-compaction-boundary")
            boundary = next(event["message"].split()[-2:] for event in pi.seen if event.get("method") == "notify"
                            and event["message"].startswith("Compaction boundary "))
            self.assertEqual(boundary[1], boundary[0], "The held user message should be the first retained entry")
            self.assertTrue(any(event["type"] == "compaction_end" and event["reason"] == "threshold"
                                and event.get("result") for event in pi.seen))
            requests = router.snapshot()
            self.assertEqual([False, False, True, True, False], [post["summary"] for post in requests])
            self.check_requests(requests[:2] + requests[-1:], thread, [
                ("hy4.fixture", "standard", "Earlier user message", False),
                ("hy4.fixture", "standard", LARGE_PROMPT, False),
                ("hy4.fixture", "standard", LARGE_PROMPT, True),
            ])
            summaries = requests[2:4]
            self.assertTrue(summaries[0]["thread"])
            self.assertNotEqual(thread, summaries[0]["thread"])
            self.assertEqual(summaries[0], summaries[1])
            for post in summaries:
                self.assertEqual(("hy4.fixture", "standard", "65536"),
                                 (post["model"], post["profile"], post["context"]))
                self.assertIn("Earlier user message", post["user"])
                self.assertNotIn(LARGE_PROMPT, post["user"])
            self.assertNotIn("Earlier user message", requests[-1]["users"])

    def test_pi_can_skip_hook_when_only_active_threshold_is_crossed(self):
        settings = {"enabled": True, "reserveTokens": 30000, "keepRecentTokens": 1,
                    "modelOverrides": {"air/hy4.fixture": {"reserveTokens": 62000}}}
        with FakeRouter().running() as router, pi_session(router, settings) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.prompt("Earlier user message")
            pi.settled()
            pi.hold(LARGE_PROMPT)
            # Both models have a 65536-token window. Only Hy4's reserve puts this input above threshold.
            pi.model("qwen.fixture")
            self.assertEqual(65536, pi.send("get_state")["model"]["contextWindow"])
            pi.prompt("/fixture-release")
            pi.settled()
            self.assertFalse(any(event["type"] == "compaction_start" for event in pi.seen))
            self.check_requests(router.snapshot(), thread, [
                ("hy4.fixture", "standard", "Earlier user message", False),
                ("hy4.fixture", "standard", LARGE_PROMPT, False),
                ("hy4.fixture", "standard", LARGE_PROMPT, True),
            ])

    def test_manual_compaction_uses_selected_profile_and_separate_thread(self):
        settings = {"enabled": False, "keepRecentTokens": 1}
        with FakeRouter().running() as router, pi_session(router, settings) as pi:
            thread = pi.send("get_state")["sessionId"]
            for prompt in ("Earlier user message", "Recent user message"):
                pi.prompt(prompt)
                pi.settled()
            pi.model("qwen.fixture")
            pi.prompt("/context narrow")
            pi.send("compact")
            requests = router.snapshot()
            self.assertEqual([False, False, True, True], [post["summary"] for post in requests])
            self.check_requests(requests[:2], thread, [
                ("hy4.fixture", "standard", "Earlier user message", False),
                ("hy4.fixture", "standard", "Recent user message", False),
            ])
            summaries = requests[2:]
            for post in summaries:
                self.assertTrue(post["summary"])
                self.assertTrue(post["thread"])
                self.assertNotEqual(thread, post["thread"])
                self.assertEqual(("qwen.fixture", "narrow", "32768"),
                                 (post["model"], post["profile"], post["context"]))

    def test_sdk_children_inherit_callback_or_load_air_independently(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("child.fixture")
            pi.prompt("/context wide")
            pi.model("hy4.fixture")
            pi.prompt("/context compact")
            pi.hold("interrupt Hy4")
            pi.model("qwen.fixture")
            offset = len(pi.seen)
            command = pi.request("prompt", message="/fixture-child inherited")
            try:
                self.assertTrue(router.held_response_started.wait(5), "Inherited child did not submit while parent was held")
                pi.prompt("/air-cancel")
                self.assertTrue(any(event.get("message") == "No matching active AIR request to cancel."
                                    for event in pi.seen[offset:]))
                pi.prompt("/fixture-child independent")
            finally:
                router.release_held_response.set()
            pi.response(command)
            self.assertFalse(any(event.get("method") == "setStatus" for event in pi.seen[offset:]))
            children = dict(event["message"].removeprefix("Child thread ").split(" ") for event in pi.seen
                            if isinstance(event.get("message"), str) and event["message"].startswith("Child thread "))
            self.assertEqual({"inherited", "independent"}, children.keys())
            self.assertEqual(3, len({thread, *children.values()}))
            pi.prompt("/fixture-release")
            pi.settled()
            requests = router.snapshot()
            self.check_requests(requests[:1] + requests[-1:], thread, [
                ("hy4.fixture", "compact", "interrupt Hy4", False),
                ("hy4.fixture", "compact", "interrupt Hy4", True),
            ])
            self.check_requests(requests[1:2], children["inherited"], [
                ("child.fixture", "standard", "inherited child", False),
            ])
            self.check_requests(requests[2:-1], children["independent"], [
                ("child.fixture", "narrow", "independent child", False),
                ("child.fixture", "narrow", "independent child", True),
            ])

    def test_direct_registry_call_keeps_session_routing_without_parent_status_or_cancel(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            thread = pi.send("get_state")["sessionId"]
            pi.model("child.fixture")
            pi.prompt("/context wide")
            pi.model("hy4.fixture")
            pi.prompt("/context compact")
            pi.hold("interrupt Hy4")
            offset = len(pi.seen)
            command = pi.request("prompt", message="/fixture-registry same-session")
            try:
                self.assertTrue(router.held_response_started.wait(5), "Direct registry call did not submit")
                pi.prompt("/air-cancel")
                self.assertTrue(any(event.get("message") == "No matching active AIR request to cancel."
                                    for event in pi.seen[offset:]))
                status_offset = len(pi.seen)
                pi.prompt("/air-status")
                self.assertTrue(any(event.get("method") == "notify" and "hy4.fixture" in event["message"]
                                    for event in pi.seen[status_offset:]))
            finally:
                router.release_held_response.set()
            pi.response(command)
            self.assertFalse(any(event.get("method") == "setStatus" for event in pi.seen[offset:]))
            pi.prompt("/fixture-registry missing-session")
            requests = router.snapshot()
            self.check_requests(requests, thread, [
                ("hy4.fixture", "compact", "interrupt Hy4", False),
                ("child.fixture", "wide", "direct registry call", False),
            ])
            self.assertEqual([2048, 8192], [post["output_limit"] for post in requests])
            replies = [event["message"] for event in pi.seen
                       if event.get("method") == "notify" and event["message"].startswith("Registry call ")]
            self.assertEqual(["Registry call stop", "Registry call error: AIR needs a Pi session identity"], replies)

    def test_registry_call_with_real_loop_options_cannot_take_active_turn_selection(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            pi.prompt("/fixture-child registry-options")
            prefix = "Child thread registry-options "
            thread = next(event["message"].removeprefix(prefix) for event in pi.seen
                          if event.get("method") == "notify" and event["message"].startswith(prefix))
            requests = router.snapshot()
            self.check_requests(requests, thread, [
                ("hy4.fixture", "compact", "interrupt Hy4", False),
                ("child.fixture", "wide", "registry with loop options", False),
                ("hy4.fixture", "compact", "interrupt Hy4", True),
            ])
            self.assertEqual([2048, 8192, 2048], [post["output_limit"] for post in requests])

    def test_explicit_output_cap_controls_fit_and_survives_retry(self):
        with FakeRouter().running() as router, pi_session(router) as pi:
            pi.prompt("/fixture-child capped")
            request_thread = next(event["message"].removeprefix("Child thread capped ") for event in pi.seen
                                  if event.get("method") == "notify" and
                                  event["message"].startswith("Child thread capped "))
            requests = router.snapshot()
            self.check_requests(requests, request_thread, [
                ("hy4.fixture", "compact", CAP_PROMPT, False),
                ("hy4.fixture", "compact", CAP_PROMPT, True),
                ("hy4.fixture", "compact", CAP_PROMPT, True),
                ("hy4.fixture", "compact", CAP_FIT_PROMPT, True),
            ])
            self.assertEqual([512, 512, 512], [post["output_limit"] for post in requests[:3]])
            self.assertLessEqual(requests[-1]["output_limit"], 512)


if __name__ == "__main__":
    unittest.main()
