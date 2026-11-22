"""Exercise Pi 1.3's request and automatic compaction paths against a loopback fake."""
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import unittest


class PromptBudgetTest(unittest.TestCase):
    def test_large_prompt_after_valid_downgrade_sends_no_inference_or_compaction(self):
        posts = []
        shutdown = threading.Event()
        profiles = [{"Name": name, "MaxContext": window, "MaxSlots": 1}
                    for name, window in [("short", 32768), ("long", 262144)]]
        model = {"Id": "demo", "DefaultProfile": "short", "Aliases": [], "Profiles": profiles}

        class Router(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                if self.path.startswith("/admin/events?"):
                    self.send_response(200)
                    self.send_header("Content-Type", "text/event-stream")
                    self.end_headers()
                    shutdown.wait(30)
                    return
                if self.path == "/admin/catalog":
                    data = {"Instance": "fixture", "Generation": 1, "DefaultModel": "demo",
                            "Engines": [{"Name": "remote", "Models": [model]}]}
                else:
                    if self.path != "/admin/status":
                        self.send_error(404)
                        return
                    data = {"Engines": []}
                self.send_response(200)
                self.end_headers()
                self.wfile.write(json.dumps(data).encode())

            def do_POST(self):
                posts.append(self.headers.get("X-Inference-Profile"))
                self.rfile.read(int(self.headers["Content-Length"]))
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.end_headers()
                self.wfile.write(b'data: {"choices":[{"index":0,"delta":{"content":"ok"},'
                                 b'"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n')

        with ThreadingHTTPServer(("127.0.0.1", 0), Router) as server, tempfile.TemporaryDirectory() as tmp:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            directory = Path(tmp)
            (directory / "settings.json").write_text(json.dumps({"retry": {"enabled": False}}))
            args = ["pi", "--no-extensions", "-e", str(Path(__file__).with_name("index.mjs").resolve()),
                    "--mode", "rpc", "--no-tools", "--no-session", "--provider", "air", "--model", "demo"]
            env = {**os.environ, "PI_CODING_AGENT_DIR": tmp, "PI_OFFLINE": "1",
                   "PI_AIR_BASE_URL": f"http://127.0.0.1:{server.server_port}/v1"}
            with subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True,
                                  env=env, cwd=tmp) as pi:
                events = queue.Queue()

                def read():
                    for line in pi.stdout:
                        events.put(json.loads(line))

                def wait(predicate):
                    while True:
                        event = events.get(timeout=10)
                        self.assertNotEqual(event["type"], "extension_error", event)
                        if predicate(event):
                            return event

                def prompt(message):
                    pi.stdin.write(json.dumps({"type": "prompt", "message": message}) + "\n")
                    pi.stdin.flush()
                    reply = wait(lambda e: e["type"] == "response" and e["command"] == "prompt")
                    self.assertTrue(reply["success"], reply)

                threading.Thread(target=read, daemon=True).start()
                try:
                    prompt("/context long")
                    prompt("fits")
                    wait(lambda e: e["type"] == "agent_settled")
                    prompt("/context short")
                    prompt("z" * 150000)
                    failure = wait(lambda e: e["type"] == "message_end" and e["message"]["role"] == "assistant")
                    self.assertEqual("error", failure["message"]["stopReason"])
                    self.assertIn("reserve", failure["message"]["errorMessage"])
                    wait(lambda e: e["type"] == "agent_settled")
                    self.assertEqual(["long"], posts)
                finally:
                    pi.terminate()
                    shutdown.set()
                    server.shutdown()


if __name__ == "__main__":
    unittest.main()
