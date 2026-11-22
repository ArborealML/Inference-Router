"""Drive the installed interactive Pi composer through a queued AIR ticket."""
import json
import os
from pathlib import Path
import pty
import re
import select
import shutil
import subprocess
import tempfile
import termios
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


@unittest.skipUnless(shutil.which("pi"), "requires an installed Pi terminal")
class WaitingIndicatorTest(unittest.TestCase):
    def test_queued_then_executing(self):
        release = threading.Event()
        executing = threading.Event()
        model = {"Id": "composer-fixture", "DefaultProfile": "short", "Aliases": [],
                 "Profiles": [{"Name": "short", "MaxContext": 32768, "MaxSlots": 1}]}

        class Router(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                if self.path.startswith("/admin/events?"):
                    self.send_response(200)
                    self.end_headers()
                    release.wait(30)
                    return
                if self.path == "/admin/catalog":
                    result = {"Instance": "fixture", "Generation": 1, "DefaultModel": model["Id"],
                              "Engines": [{"Name": "test", "Models": [model]}]}
                elif self.path == "/admin/status":
                    result = {"Engines": []}
                elif self.path == "/admin/tickets/test-ticket":
                    result = {"Id": "test-ticket", "ThreadId": self.server.thread_id,
                              "Model": model["Id"], "State": "Executing" if executing.is_set() else "Queued"}
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.end_headers()
                self.wfile.write(json.dumps(result).encode())

            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                self.server.thread_id = self.headers["X-Inference-Thread"]
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream")
                self.send_header("X-Inference-Ticket", "test-ticket")
                self.end_headers()
                self.wfile.write(b": waiting for admission\n\n")
                self.wfile.flush()
                release.wait(30)
                try:
                    self.wfile.write(b'data: {"choices":[{"index":0,"delta":{"content":"finished"},'
                                     b'"finish_reason":"stop"}]}\n\ndata: [DONE]\n\n')
                except OSError:
                    pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Router) as server, tempfile.TemporaryDirectory() as tmp:
            threading.Thread(target=server.serve_forever, daemon=True).start()
            master, slave = pty.openpty()
            termios.tcsetwinsize(slave, (40, 130))
            def expect(text):
                deadline = time.monotonic() + 20
                screen = b""
                while text not in screen:
                    remaining = deadline - time.monotonic()
                    if remaining <= 0 or not select.select([master], [], [], remaining)[0]:
                        rendered = re.sub(rb"\x1b\[[0-9;?]*[a-zA-Z]", b"", screen)
                        self.fail(f"Pi did not render {text!r}: {rendered.decode(errors='replace')[-2000:]}")
                    screen = (screen + os.read(master, 32768))[-64000:]

            env = {**os.environ, "PI_CODING_AGENT_DIR": tmp, "PI_OFFLINE": "1",
                   "PI_AIR_BASE_URL": f"http://127.0.0.1:{server.server_port}/v1", "TERM": "xterm-256color"}
            env.pop("TMUX", None)
            entry = Path(__file__).resolve().with_name("index.mjs")
            args = ["pi", "--no-extensions", "-e", str(entry), "--no-tools", "--no-session",
                    "--no-mcp", "--no-skills", "--no-context-files", "--provider", "air", "--model", model["Id"], "Say hello"]
            try:
                with subprocess.Popen(args, stdin=slave, stdout=slave, stderr=slave, env=env, cwd=tmp) as pi:
                    try:
                        expect(b"Waiting")
                        executing.set()
                        expect(b"Working")
                        release.set()
                        expect(b"finished")
                    finally:
                        pi.terminate()
                        release.set()
            finally:
                os.close(slave)
                os.close(master)
                server.shutdown()


if __name__ == "__main__":
    unittest.main()
