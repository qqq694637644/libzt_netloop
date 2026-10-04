#!/usr/bin/env python3
from __future__ import annotations

import argparse
import select
import socket
import struct
import threading
import traceback


SOCKS_VERSION = 5
NO_AUTH = 0
USERNAME_PASSWORD = 2
NO_ACCEPTABLE_METHODS = 0xFF
CONNECT = 1
UDP_ASSOCIATE = 3
IPV4 = 1
DOMAIN = 3
IPV6 = 4


def recv_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise EOFError("unexpected EOF")
        data.extend(chunk)
    return bytes(data)


def read_target_from_socket(sock: socket.socket, address_type: int) -> tuple[str, int]:
    if address_type == IPV4:
        host = socket.inet_ntop(socket.AF_INET, recv_exact(sock, 4))
    elif address_type == IPV6:
        host = socket.inet_ntop(socket.AF_INET6, recv_exact(sock, 16))
    elif address_type == DOMAIN:
        length = recv_exact(sock, 1)[0]
        if length == 0:
            raise ValueError("empty SOCKS5 domain")
        host = recv_exact(sock, length).decode("ascii")
    else:
        raise ValueError(f"unsupported address type {address_type}")

    port = struct.unpack("!H", recv_exact(sock, 2))[0]
    return host, port


def parse_udp_target(packet: bytes) -> tuple[str, int, bytes]:
    if len(packet) < 7 or packet[0:2] != b"\x00\x00":
        raise ValueError("invalid SOCKS5 UDP RSV")
    if packet[2] != 0:
        raise ValueError(f"fragmented SOCKS5 UDP packet is unsupported: FRAG={packet[2]}")

    offset = 3
    address_type = packet[offset]
    offset += 1
    if address_type == IPV4:
        if len(packet) < offset + 6:
            raise ValueError("truncated IPv4 UDP packet")
        host = socket.inet_ntop(socket.AF_INET, packet[offset : offset + 4])
        offset += 4
    elif address_type == IPV6:
        if len(packet) < offset + 18:
            raise ValueError("truncated IPv6 UDP packet")
        host = socket.inet_ntop(socket.AF_INET6, packet[offset : offset + 16])
        offset += 16
    elif address_type == DOMAIN:
        if len(packet) < offset + 1:
            raise ValueError("truncated UDP domain length")
        length = packet[offset]
        offset += 1
        if len(packet) < offset + length + 2:
            raise ValueError("truncated UDP domain")
        host = packet[offset : offset + length].decode("ascii")
        offset += length
    else:
        raise ValueError(f"unsupported UDP address type {address_type}")

    port = struct.unpack("!H", packet[offset : offset + 2])[0]
    offset += 2
    if port == 0:
        raise ValueError("UDP target port zero")
    return host, port, packet[offset:]


def encode_endpoint(host: str, port: int) -> bytes:
    address = socket.inet_pton(socket.AF_INET, host)
    return b"\x00\x00\x00\x01" + address + struct.pack("!H", port)


def send_reply(sock: socket.socket, reply: int, host: str, port: int) -> None:
    sock.sendall(bytes([SOCKS_VERSION, reply]) + encode_endpoint(host, port)[2:])


class Socks5TestProxy:
    def __init__(
        self,
        host: str,
        port: int,
        username: str | None,
        password: str | None,
    ) -> None:
        self.host = host
        self.port = port
        self.username = username
        self.password = password
        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.listener.bind((host, port))
        self.listener.listen(128)

    def serve_forever(self) -> None:
        print(
            f"SOCKS5 test proxy listening on {self.host}:{self.port} "
            f"auth={'rfc1929' if self.username is not None else 'none'}",
            flush=True,
        )
        while True:
            client, remote = self.listener.accept()
            threading.Thread(
                target=self.handle_client,
                args=(client, remote),
                daemon=True,
            ).start()

    def handle_client(self, client: socket.socket, remote) -> None:
        try:
            client.settimeout(30)
            self.negotiate_auth(client)

            header = recv_exact(client, 4)
            if header[0] != SOCKS_VERSION or header[2] != 0:
                raise ValueError(f"invalid request header {header.hex()}")
            command = header[1]
            target = read_target_from_socket(client, header[3])

            if command == CONNECT:
                self.handle_connect(client, target)
            elif command == UDP_ASSOCIATE:
                self.handle_udp_associate(client, remote, target)
            else:
                send_reply(client, 7, "0.0.0.0", 0)
        except EOFError:
            pass
        except Exception:
            traceback.print_exc()
        finally:
            try:
                client.close()
            except OSError:
                pass

    def negotiate_auth(self, client: socket.socket) -> None:
        greeting = recv_exact(client, 2)
        if greeting[0] != SOCKS_VERSION or greeting[1] == 0:
            raise ValueError("invalid SOCKS5 greeting")
        methods = recv_exact(client, greeting[1])

        required = USERNAME_PASSWORD if self.username is not None else NO_AUTH
        if required not in methods:
            client.sendall(bytes([SOCKS_VERSION, NO_ACCEPTABLE_METHODS]))
            raise PermissionError("required auth method not offered")

        client.sendall(bytes([SOCKS_VERSION, required]))
        if required == NO_AUTH:
            return

        auth_header = recv_exact(client, 2)
        if auth_header[0] != 1 or auth_header[1] == 0:
            client.sendall(b"\x01\x01")
            raise PermissionError("invalid RFC1929 auth request")

        username = recv_exact(client, auth_header[1]).decode("utf-8")
        password_length = recv_exact(client, 1)[0]
        password = recv_exact(client, password_length).decode("utf-8")
        if username != self.username or password != (self.password or ""):
            client.sendall(b"\x01\x01")
            raise PermissionError("invalid RFC1929 credentials")

        client.sendall(b"\x01\x00")

    def handle_connect(self, client: socket.socket, target: tuple[str, int]) -> None:
        remote = socket.create_connection(target, timeout=20)
        try:
            local = remote.getsockname()
            send_reply(client, 0, local[0], local[1])
            client.settimeout(None)
            remote.settimeout(None)
            self.pump(client, remote)
        finally:
            remote.close()

    @staticmethod
    def pump(left: socket.socket, right: socket.socket) -> None:
        peers = {left: right, right: left}
        readable = {left, right}

        while readable:
            ready, _, _ = select.select(list(readable), [], [], 30)
            if not ready:
                continue

            for source in ready:
                destination = peers[source]
                payload = source.recv(65536)
                if payload:
                    destination.sendall(payload)
                    continue

                readable.discard(source)
                try:
                    destination.shutdown(socket.SHUT_WR)
                except OSError:
                    pass

    def handle_udp_associate(
        self,
        client: socket.socket,
        tcp_remote,
        declared: tuple[str, int],
    ) -> None:
        relay = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        # The TCP test proxy intentionally listens on loopback, but the UDP
        # relay must not bind its source address to 127.0.0.1. A loopback-bound
        # UDP socket cannot reach Internet destinations on Windows
        # (WSAENETUNREACH / 10051). Binding the relay to the IPv4 wildcard still
        # accepts the NetLoop client's datagrams addressed to 127.0.0.1 while
        # allowing the OS to choose the runner's real egress interface.
        relay.bind(("0.0.0.0", 0))
        relay.setblocking(False)
        relay_port = relay.getsockname()[1]

        # Intentionally return wildcard BND.ADDR. Real proxies do this and the
        # NetLoop client must substitute the TCP proxy address.
        send_reply(client, 0, "0.0.0.0", relay_port)
        client.setblocking(False)

        client_endpoint: tuple[str, int] | None
        if declared[1] == 0:
            client_endpoint = None
        else:
            declared_host = tcp_remote[0] if declared[0] == "0.0.0.0" else declared[0]
            client_endpoint = (declared_host, declared[1])

        allowed_remotes: set[tuple[str, int]] = set()
        try:
            while True:
                ready, _, _ = select.select([client, relay], [], [], 1.0)
                if client in ready:
                    payload = client.recv(1)
                    if not payload:
                        return
                    # No application data is defined on the control connection.

                if relay not in ready:
                    continue

                packet, source = relay.recvfrom(65535)
                source_endpoint = (source[0], source[1])

                if client_endpoint is None:
                    if source[0] != tcp_remote[0]:
                        continue
                    client_endpoint = source_endpoint

                if source_endpoint == client_endpoint:
                    try:
                        host, port, payload = parse_udp_target(packet)
                        destination = self.resolve_udp_target(host, port)
                        relay.sendto(payload, destination)
                        allowed_remotes.add((destination[0], destination[1]))
                    except Exception:
                        traceback.print_exc()
                    continue

                if source_endpoint not in allowed_remotes or client_endpoint is None:
                    continue

                response = encode_endpoint(source[0], source[1]) + packet
                relay.sendto(response, client_endpoint)
        finally:
            relay.close()

    @staticmethod
    def resolve_udp_target(host: str, port: int) -> tuple[str, int]:
        infos = socket.getaddrinfo(
            host,
            port,
            family=socket.AF_INET,
            type=socket.SOCK_DGRAM,
            proto=socket.IPPROTO_UDP,
        )
        if not infos:
            raise OSError(f"unable to resolve UDP target {host}:{port}")
        sockaddr = infos[0][4]
        return sockaddr[0], sockaddr[1]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--username")
    parser.add_argument("--password")
    args = parser.parse_args()

    if (args.username is None) != (args.password is None):
        parser.error("--username and --password must be supplied together")

    Socks5TestProxy(
        args.host,
        args.port,
        args.username,
        args.password,
    ).serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
