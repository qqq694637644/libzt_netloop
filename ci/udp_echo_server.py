#!/usr/bin/env python3
from __future__ import annotations

import argparse
import socket


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="::1")
    parser.add_argument("--port", type=int, required=True)
    args = parser.parse_args()

    family = socket.AF_INET6 if ":" in args.host else socket.AF_INET
    with socket.socket(family, socket.SOCK_DGRAM) as udp:
        udp.bind((args.host, args.port))
        while True:
            payload, remote = udp.recvfrom(65535)
            udp.sendto(payload, remote)


if __name__ == "__main__":
    raise SystemExit(main())
