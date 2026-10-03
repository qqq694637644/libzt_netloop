#!/usr/bin/env python3
from __future__ import annotations

import argparse
from pathlib import Path
import select
import socket
import struct
import threading
import time


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


class Logger:
    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self._file = path.open("a", encoding="utf-8")
        self._lock = threading.Lock()

    def write(self, message: str) -> None:
        line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {message}"
        with self._lock:
            print(line, flush=True)
            self._file.write(line + "\n")
            self._file.flush()


def relay(left: socket.socket, right: socket.socket) -> None:
    sockets = [left, right]
    while True:
        readable, _, _ = select.select(sockets, [], [], 30)
        if not readable:
            continue
        for source in readable:
            data = source.recv(65536)
            if not data:
                return
            target = right if source is left else left
            target.sendall(data)


def handle(client: socket.socket, logger: Logger) -> None:
    upstream: socket.socket | None = None
    try:
        version, method_count = recv_exact(client, 2)
        if version != 5:
            raise ValueError(f"unsupported SOCKS version {version}")
        methods = recv_exact(client, method_count)
        if 0 not in methods:
            client.sendall(b"\x05\xff")
            return
        client.sendall(b"\x05\x00")

        version, command, _reserved, address_type = recv_exact(client, 4)
        if version != 5 or command != 1:
            client.sendall(b"\x05\x07\x00\x01\x00\x00\x00\x00\x00\x00")
            return

        if address_type == 1:
            host = socket.inet_ntop(socket.AF_INET, recv_exact(client, 4))
        elif address_type == 3:
            length = recv_exact(client, 1)[0]
            host = recv_exact(client, length).decode("idna")
        elif address_type == 4:
            host = socket.inet_ntop(socket.AF_INET6, recv_exact(client, 16))
        else:
            raise ValueError(f"unsupported address type {address_type}")

        port = struct.unpack("!H", recv_exact(client, 2))[0]
        logger.write(f"CONNECT {host}:{port}")
        upstream = socket.create_connection((host, port), timeout=20)
        upstream.settimeout(None)
        client.sendall(b"\x05\x00\x00\x01\x00\x00\x00\x00\x00\x00")
        relay(client, upstream)
    except Exception as exc:
        logger.write(f"connection error: {exc!r}")
        try:
            client.sendall(b"\x05\x01\x00\x01\x00\x00\x00\x00\x00\x00")
        except OSError:
            pass
    finally:
        client.close()
        if upstream is not None:
            upstream.close()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=18080)
    parser.add_argument("--log", required=True)
    args = parser.parse_args()

    logger = Logger(Path(args.log))
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind((args.host, args.port))
    listener.listen(128)
    logger.write(f"mock SOCKS5 ready on {args.host}:{args.port}")

    while True:
        client, address = listener.accept()
        logger.write(f"accepted {address}")
        threading.Thread(target=handle, args=(client, logger), daemon=True).start()


if __name__ == "__main__":
    raise SystemExit(main())
