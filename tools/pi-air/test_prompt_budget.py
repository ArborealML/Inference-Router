"""Exercise Pi 1.3's request and automatic compaction paths against a loopback fake."""
import json
import os
from pathlib import Path
import queue
import sys
import subprocess
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import unittest


class PromptBudgetTest(unittest.TestCase):
    def test_running_upgrade_then_oversized_prompt_after_downgrade(self):
        posts = []
        old_entry_markers = []
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
                old_entry_markers.append(self.headers.get("X-Fixture-Old-Entry"))
                posts.append((self.headers["X-Inference-Preferred-Profile"], self.headers["X-Inference-Thread"]))
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
            source = Path(__file__).resolve().parent
            installation = directory / "pi-air"
            installation.mkdir()
            entry = installation / "index.mjs"
            helper = installation / "catalog.mjs"
            # The earlier installation used a separate helper with a different export.
            old_entry = (source / entry.name).read_text().replace("requestFits", "previousFits")
            entry.write_text(old_entry.replace("const headers = new Headers(init.headers);",
                                               'const headers = new Headers(init.headers); headers.set("X-Fixture-Old-Entry", "yes");'))
            helper.write_text((source / helper.name).read_text().replace("requestFits", "previousFits"))
            launcher = directory / "pi-clm.py"
            launcher.write_text("from pi_air_launcher import router_provider\n")
            reload_command = directory / "reload.mjs"
            reload_command.write_text('export default pi => pi.registerCommand("fixture-reload", '
                                      '{handler: async (_, ctx) => ctx.reload()});')
            args = ["pi", "--no-extensions", "-e", str(entry), "-e", str(reload_command),
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
                    prompt("/context short")
                    prompt("first")
                    wait(lambda e: e["type"] == "agent_settled")
                    subprocess.run([sys.executable, str(source / "install.py"), str(launcher)], check=True)
                    prompt("/fixture-reload")
                    prompt("/context long")
                    prompt("fits")
                    wait(lambda e: e["type"] == "agent_settled")
                    prompt("/context short")
                    prompt("z" * 150000)
                    failure = wait(lambda e: e["type"] == "message_end" and e["message"]["role"] == "assistant")
                    self.assertEqual("error", failure["message"]["stopReason"])
                    self.assertIn("reserve", failure["message"]["errorMessage"])
                    wait(lambda e: e["type"] == "agent_settled")
                    self.assertEqual(["short", "long"], [profile for profile, _ in posts])
                    self.assertEqual(["yes", None], old_entry_markers)
                    self.assertTrue(posts[0][1])
                    self.assertEqual(posts[0][1], posts[1][1])
                finally:
                    pi.terminate()
                    shutdown.set()
                    server.shutdown()


if __name__ == "__main__":
    unittest.main()
