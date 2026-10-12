#!/usr/bin/env python3
"""Offline native DLL smoke check for logPR #4 (does not start ZeroTier)."""
from __future__ import annotations

import argparse
import ctypes
from pathlib import Path


class UnderlayTraceEvent(ctypes.Structure):
    _fields_ = [
        ("timestamp_ms", ctypes.c_int64),
        ("peer_id", ctypes.c_uint64),
        ("reference", ctypes.c_uint64),
        ("kind", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("byte_count", ctypes.c_uint32),
        ("result", ctypes.c_int32),
        ("primary_port", ctypes.c_uint16),
        ("secondary_port", ctypes.c_uint16),
        ("previous_secondary_port", ctypes.c_uint16),
        ("reserved", ctypes.c_uint16),
        ("endpoint", ctypes.c_char * 64),
        ("local_endpoint", ctypes.c_char * 64),
        ("interface_name", ctypes.c_char * 64),
    ]


def check_layout() -> None:
    assert ctypes.sizeof(UnderlayTraceEvent) == 240, "ABI size changed"
    assert UnderlayTraceEvent.endpoint.offset == 48, "endpoint offset changed"
    assert UnderlayTraceEvent.primary_port.offset == 40, "port offset changed"
    assert UnderlayTraceEvent.local_endpoint.offset == 112, "local endpoint offset changed"
    assert UnderlayTraceEvent.interface_name.offset == 176, "interface name offset changed"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native_library", type=Path)
    args = parser.parse_args()
    check_layout()

    lib = ctypes.CDLL(str(args.native_library.resolve()))
    poll = lib.zts_netloop_poll_underlay_event
    poll.argtypes = [
        ctypes.POINTER(UnderlayTraceEvent),
        ctypes.POINTER(ctypes.c_uint64),
    ]
    poll.restype = ctypes.c_int
    assert poll(None, None) == -3, "null pointers must fail with ZTS_ERR_ARG"

    sample = UnderlayTraceEvent()
    dropped = ctypes.c_uint64(0)
    assert poll(ctypes.byref(sample), ctypes.byref(dropped)) == -2, (
        "unstarted native service must return ZTS_ERR_SERVICE"
    )
    print("PASS: Native trace export, argument checks and 240-byte ABI")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
