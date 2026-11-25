"""Exercise air top row boundaries and repaints in an isolated tmux terminal."""
import argparse
from contextlib import suppress
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import shlex
import subprocess
import tempfile
import threading
import time


class Router(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.send_response(200)
        if self.path.startswith("/admin/events"):
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            with suppress(BrokenPipeError, ConnectionResetError):
                for seq in range(1, 121):
                    event = dict(Seq=seq, Timestamp="2026-01-01T00:00:00Z", Kind="engine.health", Engine="alpha")
                    self.wfile.write(f"data: {json.dumps(event)}\n\n".encode())
                    self.wfile.flush()
                    time.sleep(.1)
        else:
            self.end_headers()
            engines = [dict(Engine=name, Healthy=True, SwitchingToward=None, Inflight=0,
                            LoadedModels=[name + "-model"]) for name in ("alpha", "zulu")]
            self.wfile.write(json.dumps(dict(Engines=engines, QueueDepths={})).encode())


def check(binary):
    with tempfile.TemporaryDirectory() as directory, ThreadingHTTPServer(("127.0.0.1", 0), Router) as server:
        threading.Thread(target=server.serve_forever, daemon=True).start()
        def tmux(*args):
            return subprocess.check_output(["tmux", "-S", directory + "/socket", *args], text=True)

        def screen():
            return tmux("capture-pane", "-pt", "monitor")

        def key(name):
            tmux("send-keys", "-t", "monitor", name)

        def expect(*parts):
            for _ in range(100):
                text = screen()
                if all(part in text for part in parts):
                    return text
                time.sleep(.05)
            raise AssertionError((parts, text))

        url = f"http://127.0.0.1:{server.server_port}"
        try:
            tmux("new-session", "-d", "-s", "monitor", "-x", "80", "-y", "24",
                 shlex.join([str(binary.resolve()), "top", "-s", url]))
            initial = expect("Engines", "Queues", "Events", "alpha  healthy", "zulu-model")
            key("Up")
            time.sleep(.2)
            assert "alpha  healthy" in screen(), screen()
            key("Down")
            expect("zulu  healthy")
            key("Down")
            # Span multiple status polls while the event feed is also changing.
            for _ in range(30):
                time.sleep(.2)
                text = screen()
                assert "zulu  healthy" in text and "zulu-model" in text, text
                assert initial.splitlines()[1:4] == text.splitlines()[1:4], text
            key("Tab")
            key("Up")
            key("Down")
            expect("zulu  healthy")
            key("Escape")
            key("Right")
            expect("No queued requests")
            key("Up")
            key("Down")
            time.sleep(.2)
            assert "No queued requests" in screen(), screen()
            key("Right")
            expect("engine.health")
            key("Left")
            key("Left")
            expect("zulu  healthy")
            # SGR mouse coordinates are one-based and include the window border.
            for edge in ("M", "m"):
                click = f"\x1b[<0;15;2{edge}".encode()
                tmux("send-keys", "-t", "monitor", "-H", *(f"{byte:02x}" for byte in click))
            expect("[Queues]", "No queued requests")
            key("q")
        finally:
            subprocess.run(["tmux", "-S", directory + "/socket", "kill-server"], check=False)
            server.shutdown()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    check(parser.parse_args().binary)
