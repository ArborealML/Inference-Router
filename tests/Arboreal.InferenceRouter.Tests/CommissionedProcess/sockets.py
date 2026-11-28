"""Test-only fd activation and an instrumented loopback fixture backend."""

import fcntl
import hashlib
import json
import os
import socket
import struct
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


def activate(uid, group, paths, command):
    listeners = []
    for name in paths:
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(name)
        os.chown(name, int(uid), int(group))
        os.chmod(name, 0o660)
        listener.listen(16)
        # Keep Python's descriptor object away from the activation descriptor range.
        listeners.append(fcntl.fcntl(listener, fcntl.F_DUPFD_CLOEXEC, 16))
        listener.close()

    for index, listener in enumerate(listeners):
        os.dup2(listener, 3 + index, inheritable=True)
    for listener in listeners:
        if listener < 3 or listener >= 3 + len(paths):
            os.close(listener)
    os.environ["LISTEN_FDS"] = str(len(paths))
    os.environ["LISTEN_PID"] = str(os.getpid())
    os.setgroups([])
    os.setgid(int(group))
    os.setuid(int(uid))
    os.execv(command[0], command)


def process_identity(pid):
    fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return {"ProcessId": pid, "StartTicks": int(fields[19])}


def cookie(sock):
    return struct.unpack("=Q", sock.getsockopt(socket.SOL_SOCKET, 57, 8))[0]


def exact(peer, count):
    data = b""
    while len(data) < count:
        more = peer.recv(count - len(data))
        if not more:
            raise OSError("terminal peer refused the exact accepted socket")
        data += more
    return data


def backend(port, model, context, slots, terminal, release, receipts, stop):
    port, context, slots = map(int, (port, context, slots))
    # This CPU child stands in for one completed worker leg. It never receives weights.
    dispatch, compute = socket.socketpair()
    worker = os.fork()
    if worker == 0:
        dispatch.close()
        while compute.recv(1):
            compute.sendall(b"R")
        os._exit(0)
    compute.close()
    worker_identity = process_identity(worker)
    worker_lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):
            pass

        def answer(self, kind, value, application=None, stream=False):
            result = json.dumps(value, separators=(",", ":")).encode()
            if stream:
                result = b"data: " + result + b"\n\ndata: [DONE]\n\n"
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as peer:
                peer.settimeout(30)
                inode = os.fstat(self.connection.fileno()).st_ino
                accepted_cookie = cookie(self.connection)
                peer.connect(terminal)
                peer.sendall(struct.pack(">BBQQ", 1, kind, inode, accepted_cookie))
                use = exact(peer, 16)
                if application is not None:
                    with open(receipts, "a", encoding="utf-8") as target:
                        target.write(json.dumps({
                            "pid": os.getpid(), "inode": inode, "cookie": accepted_cookie,
                            "use": use.hex(), "sha256": hashlib.sha256(application).hexdigest()
                        }) + "\n")
                self.send_response(200)
                self.send_header("Content-Type", "text/event-stream" if stream else "application/json")
                self.send_header("Content-Length", str(len(result)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(result)
                self.wfile.flush()
                if application is not None and (b"held-original" in application or b"queued-original" in application):
                    for _ in range(3000):
                        if Path(release).exists():
                            break
                        threading.Event().wait(0.01)
                    else:
                        return
                if kind == 4:
                    with worker_lock:
                        dispatch.sendall(b"W")
                        if exact(dispatch, 1) != b"R":
                            raise OSError("fixture worker did not complete the leg")
                identity = (worker, worker_identity["StartTicks"], 1) if kind == 4 else (0, 0, 0)
                peer.sendall(use + struct.pack(">IQI", *identity))
            self.close_connection = True

        def do_GET(self):
            if self.path.endswith("/models"):
                self.answer(2, {"data": [{"id": model, "status": {"value": "loaded"}}]})
            else:
                self.answer(3, {"n_ctx": context, "total_slots": slots,
                                "default_generation_settings": {"n_ctx": context}})

        def do_POST(self):
            body = self.rfile.read(int(self.headers["Content-Length"]))
            if json.loads(body).get("stream"):
                self.answer(4, {"id": "fixture-successor", "object": "chat.completion.chunk", "model": model,
                                "choices": [{"index": 0, "delta": {"content": "successor-only"},
                                             "finish_reason": "stop"}]}, body, stream=True)
            else:
                self.answer(4, {"id": "fixture-incumbent", "object": "chat.completion", "model": model,
                                "choices": [{"index": 0, "message": {"role": "assistant", "content": "incumbent-only"},
                                             "finish_reason": "stop"}]}, body)

    with ThreadingHTTPServer(("127.0.0.1", port), Handler) as server:
        ns = os.stat("/proc/self/ns/net")
        identity = {
            "Handler": process_identity(os.getpid()), "Worker": worker_identity,
            "Namespace": {"Device": ns.st_dev, "Inode": ns.st_ino},
            "Listener": {"Number": server.socket.fileno(), "Bound": {"Address": "127.0.0.1", "Port": port},
                         "Inode": os.fstat(server.socket.fileno()).st_ino, "Cookie": cookie(server.socket)}
        }
        print(json.dumps(identity), flush=True)
        server.timeout = 0.1
        try:
            while not Path(stop).exists():
                server.handle_request()
        finally:
            dispatch.close()
            os.waitpid(worker, 0)


def stale_listener(port, receipts):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", int(port)))
        listener.listen(16)
        print(os.getpid(), flush=True)
        while True:
            peer, _ = listener.accept()
            with peer:
                peer.settimeout(15)
                payload = peer.recv(65536)
                if payload:
                    with open(receipts, "a", encoding="utf-8") as target:
                        target.write(f"{len(payload)}\n")


if sys.argv[1] == "activate":
    cut = sys.argv.index("--")
    activate(sys.argv[2], sys.argv[3], sys.argv[4:cut], sys.argv[cut + 1:])
elif sys.argv[1] == "backend":
    backend(*sys.argv[2:])
elif sys.argv[1] == "stale":
    stale_listener(*sys.argv[2:])
else:
    raise SystemExit(2)
