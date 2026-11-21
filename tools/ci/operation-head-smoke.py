"""Published operation-head binary smoke on a disposable Linux runner with two process UIDs."""

import hashlib
import json
import os
from pathlib import Path
import socket
import ssl
import struct
import subprocess
import sys
import tempfile
import time
import uuid

PRODUCER_UID = 65534
CLIENT = """
import hashlib, json, secrets, socket, ssl, struct, sys

path, pin, scope_json, step = sys.argv[1:]
scope = json.loads(scope_json)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
context.check_hostname = False
context.verify_mode = ssl.CERT_NONE

def exchange(op, prior=None, following=None):
    request = dict(Version=1, Operation=op, Scope=scope, Nonce=secrets.token_hex(32).upper(),
                   Predecessor=prior, Next=following)
    request_bytes = json.dumps(request, separators=(',', ':')).encode()
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as raw:
        raw.settimeout(5)
        raw.connect(path)
        with context.wrap_socket(raw) as tls:
            assert hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest() == pin
            tls.sendall(struct.pack('>I', len(request_bytes)) + request_bytes)
            with tls.makefile('rb') as stream:
                size, = struct.unpack('>I', stream.read(4))
                assert 0 < size <= 4096
                reply = json.loads(stream.read(size))
            assert reply['Request'] == request
            return reply['Outcome'], reply['Head']

head = dict(Scope=scope, Revision=0, Digest='A' * 64)
if step == 'write':
    assert exchange('Read') == ('Absent', None)
    assert exchange('CompareAdvance', following=head) == ('Applied', head)
assert exchange('Read') == ('Current', head)
assert exchange('CompareAdvance', following=head) == ('Refused', None)
"""


def run_service(binary, authority, runtime, pin, scope, step):
    endpoint = runtime / "operation-head.sock"
    with subprocess.Popen([binary, authority, runtime]) as service:
        try:
            deadline = time.monotonic() + 5
            while True:
                if service.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("The scratch authority did not start")
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as probe:
                    try:
                        probe.connect(str(endpoint))
                        break
                    except (FileNotFoundError, ConnectionRefusedError):
                        time.sleep(0.02)
            subprocess.run([sys.executable, "-I", "-c", CLIENT, endpoint, pin, json.dumps(scope), step],
                           user=PRODUCER_UID, group=os.getegid(), extra_groups=[], check=True, timeout=22)
        finally:
            if service.poll() is None:
                service.terminate()
                try:
                    service.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    service.kill()
                    service.wait(timeout=5)
                    raise
        if service.returncode != 0 or endpoint.exists():
            raise RuntimeError("The scratch authority did not shut down cleanly")


def main():
    if len(sys.argv) != 2 or not sys.platform.startswith("linux") or os.geteuid() != 0:
        raise SystemExit("Run with the published binary path in a disposable two-UID Linux runner")
    binary = Path(sys.argv[1]).absolute()
    old_mask = os.umask(0o077)
    try:
        with tempfile.TemporaryDirectory(prefix="air-oh-aot-", dir="/tmp") as directory:
            root = Path(directory)
            root.chmod(0o710)
            authority = root / "head"
            authority.mkdir(mode=0o700)
            runtime = root / "run"
            runtime.mkdir(mode=0o710)
            runtime.chmod(0o710)
            subprocess.run(["openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:P-256",
                            "-noenc", "-days", "1", "-subj", "/CN=operation-head-scratch",
                            "-keyout", authority / "authority.key", "-out", authority / "authority.pem"],
                           check=True, capture_output=True, timeout=5)
            pem = (authority / "authority.pem").read_text(encoding="ascii")
            pin = hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest()
            scope = dict(Slot=str(root / "synthetic-slot"), Identity=str(uuid.uuid4()),
                         Enrollment=dict(Group="scratch", Generation=1))
            (authority / "bootstrap.json").write_text(
                json.dumps(dict(Version=1, Scope=scope, ProducerUid=PRODUCER_UID)), encoding="utf-8")
            (authority / "head.json").write_text(
                json.dumps(dict(Version=1, Scope=scope, Kind="Absent", Revision=None, Digest=None)), encoding="utf-8")
            (authority / "head.lock").touch(exist_ok=False)
            run_service(binary, authority, runtime, pin, scope, "write")
            run_service(binary, authority, runtime, pin, scope, "reopen")
        print("OPERATION_HEAD_AOT_SMOKE_OK")
    finally:
        os.umask(old_mask)


if __name__ == "__main__":
    main()
