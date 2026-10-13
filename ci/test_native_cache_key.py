"""Native cache key correctness without building or touching submodules."""
from __future__ import annotations

import re
import sys
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from build_csharp import LIBZT_PATCHES, ZERO_TIER_ONE_PATCHES
from build_android_native import PATCHES as ANDROID_PATCHES
from native_cache_key import native_cache_key


class NativeCacheKeyTests(unittest.TestCase):
    def test_fixed_length_and_determinism(self) -> None:
        key = native_cache_key("windows", "x64")
        self.assertRegex(key, r"^[0-9a-f]{40}$")
        self.assertEqual(key, native_cache_key("windows", "x64"))
        self.assertNotEqual(key, native_cache_key("linux", "x64"))
        self.assertNotEqual(key, native_cache_key("windows", "arm64"))
        self.assertNotEqual(
            native_cache_key("android", "arm64-v8a", "26.1"),
            native_cache_key("android", "arm64-v8a", "27.1"),
        )

    def test_cache_manifest_uses_existing_pinned_patches(self) -> None:
        for path in [*LIBZT_PATCHES, *ZERO_TIER_ONE_PATCHES, *ANDROID_PATCHES]:
            self.assertTrue(path.is_file(), f"missing native patch: {path}")
        self.assertEqual(ANDROID_PATCHES[0], LIBZT_PATCHES[0])
        self.assertEqual(len({p.name for p in LIBZT_PATCHES}), len(LIBZT_PATCHES))


if __name__ == "__main__":
    unittest.main()
