"""A small FIX 4.4 initiator for Tickwire, built on simplefix (framing and encoding only).

It handles the parts of the session layer a test client needs: logon with ResetSeqNumFlag, heartbeats on request,
answering TestRequests, and waiting for specific messages. It does not persist sequence numbers.
"""
from __future__ import annotations

import json
import socket
import time
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime, timezone

import simplefix


def utc_now() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H:%M:%S.%f")[:-3]


ADMIN_KEY: str | None = None  # set by tools that run as an operator (e.g. the load generator)


def _headers(token: str | None) -> dict:
    h = {"Content-Type": "application/json"}
    if token:
        h["Authorization"] = f"Bearer {token}"
    if ADMIN_KEY:
        h["X-Admin-Key"] = ADMIN_KEY
    return h


def api_get(api: str, path: str, token: str | None = None) -> object:
    req = urllib.request.Request(api.rstrip("/") + path, headers=_headers(token))
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.loads(r.read())


def api_put(api: str, path: str, token: str | None, body: object) -> object:
    req = urllib.request.Request(api.rstrip("/") + path, data=json.dumps(body).encode(), headers=_headers(token), method="PUT")
    with urllib.request.urlopen(req, timeout=15) as r:
        text = r.read()
        return json.loads(text) if text else None


def api_post(api: str, path: str, token: str | None = None, body: object | None = None) -> object:
    data = json.dumps(body).encode() if body is not None else b""
    req = urllib.request.Request(api.rstrip("/") + path, data=data, headers=_headers(token), method="POST")
    with urllib.request.urlopen(req, timeout=15) as r:
        text = r.read()
        return json.loads(text) if text else None


def provision(api: str) -> dict:
    """Creates a guest account and a TCP FIX session for it. Returns the /api/connect response plus the token."""
    guest = api_post(api, "/api/guest")
    creds = api_post(api, "/api/connect", guest["token"])
    creds["token"] = guest["token"]
    return creds


@dataclass
class Message:
    raw: simplefix.FixMessage
    received_at: float = field(default_factory=time.perf_counter)

    def get(self, tag: int) -> str | None:
        v = self.raw.get(tag)
        return v.decode() if v is not None else None

    @property
    def msg_type(self) -> str:
        return self.get(35) or "?"


class FixClient:
    def __init__(self, host: str, port: int, sender: str, target: str, heartbeat: int = 30, verbose: bool = True):
        self.host, self.port, self.sender, self.target = host, port, sender, target
        self.heartbeat = heartbeat
        self.verbose = verbose
        self.seq = 1
        self.sock: socket.socket | None = None
        self.parser = simplefix.FixParser()
        self.inbox: list[Message] = []

    # -- connection
    def connect(self, timeout: float = 10.0) -> None:
        self.sock = socket.create_connection((self.host, self.port), timeout=timeout)
        self.sock.settimeout(0.2)

    def close(self) -> None:
        if self.sock:
            self.sock.close()
            self.sock = None

    # -- sending
    def send(self, msg_type: str, fields: list[tuple[int, object]]) -> int:
        m = simplefix.FixMessage()
        m.append_pair(8, "FIX.4.4")
        m.append_pair(35, msg_type)
        m.append_pair(49, self.sender)
        m.append_pair(56, self.target)
        m.append_pair(34, self.seq)
        m.append_pair(52, utc_now())
        for tag, value in fields:
            if value is not None:
                m.append_pair(tag, value)
        wire = m.encode()
        assert self.sock is not None
        self.sock.sendall(wire)
        if self.verbose:
            print(">>", wire.replace(b"\x01", b"|").decode())
        self.seq += 1
        return self.seq - 1

    def logon(self, timeout: float = 10.0) -> Message:
        self.send("A", [(98, 0), (108, self.heartbeat), (141, "Y")])
        reply = self.wait(lambda m: m.msg_type in ("A", "5"), timeout)
        if reply.msg_type != "A":
            raise RuntimeError(f"Logon refused: {reply.get(58)}")
        return reply

    def logout(self, timeout: float = 5.0) -> None:
        self.send("5", [])
        try:
            self.wait(lambda m: m.msg_type == "5", timeout)
        except TimeoutError:
            pass

    def new_order(self, cl_ord_id: str, symbol: str, put_or_call: int, strike: float, maturity: str, side: str, qty: int,
                  price: float | None, tif: str = "0") -> int:
        return self.send("D", [(11, cl_ord_id), (21, "1"), (55, symbol), (167, "OPT"), (541, maturity), (201, put_or_call),
                               (202, strike), (54, side), (60, utc_now()), (38, qty), (40, "2" if price is not None else "1"),
                               (44, f"{price:.2f}" if price is not None else None), (59, tif)])

    def cancel(self, cl_ord_id: str, orig_cl_ord_id: str, symbol: str, side: str, qty: int) -> int:
        return self.send("F", [(41, orig_cl_ord_id), (11, cl_ord_id), (55, symbol), (54, side), (60, utc_now()), (38, qty)])

    # -- receiving
    def poll(self) -> None:
        assert self.sock is not None
        try:
            data = self.sock.recv(65536)
        except socket.timeout:
            return
        if not data:
            raise ConnectionError("connection closed by venue")
        self.parser.append_buffer(data)
        while (raw := self.parser.get_message()) is not None:
            msg = Message(raw)
            if self.verbose:
                print("<<", raw.encode(raw=True).replace(b"\x01", b"|").decode())
            if msg.msg_type == "1":  # TestRequest: answer or the venue logs us out
                self.send("0", [(112, msg.get(112))])
            self.inbox.append(msg)

    def wait(self, predicate, timeout: float = 10.0) -> Message:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for i, m in enumerate(self.inbox):
                if predicate(m):
                    return self.inbox.pop(i)
            self.poll()
        raise TimeoutError("expected message not received")
