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

    def test_consolidated_core_patch_carries_private_new_headers(self) -> None:
        # Node.hpp includes this build-specific policy. If a patch is built
        # using 'git diff' without intent-to-add, the header silently drops
        # out of the consolidated patch and every native platform fails.
        core_patch = ZERO_TIER_ONE_PATCHES[0].read_text(encoding="utf-8")
        self.assertIn(
            "diff --git a/node/NetloopHandoverPolicy.hpp b/node/NetloopHandoverPolicy.hpp",
            core_patch,
        )
        self.assertIn("+inline UdpSendMode udpSendMode", core_patch)
        self.assertIn("+struct RootBindingIdentity", core_patch)

    def test_release_native_cache_steps_match_regular_ci(self) -> None:
        """A release must not hash deleted patches after a patch-chain squash."""
        workflows = ROOT / ".github" / "workflows"
        release = (workflows / "release.yml").read_text(encoding="utf-8")
        windows = (workflows / "csharp-windows-ci.yml").read_text(encoding="utf-8")
        android = (workflows / "csharp-android-ci.yml").read_text(encoding="utf-8")

        windows_lines = [
            "$fingerprint = python tools/native_cache_key.py --platform windows --arch x64",
            "if ($LASTEXITCODE -ne 0) { throw 'Failed to hash native cache inputs' }",
            '"key=libzt-pinvoke-win-x64-$fingerprint" >> $env:GITHUB_OUTPUT',
        ]
        android_lines = [
            'fingerprint="$(python tools/native_cache_key.py --platform android --arch arm64-v8a --ndk-revision "$ndk_revision")"',
            'echo "key=libzt-pinvoke-android-arm64-${fingerprint}" >> "$GITHUB_OUTPUT"',
        ]
        for line in windows_lines:
            self.assertIn(line, windows)
            self.assertIn(line, release)
        for line in android_lines:
            self.assertIn(line, android)
            self.assertIn(line, release)

        for workflow in workflows.glob("*.yml"):
            source = workflow.read_text(encoding="utf-8")
            self.assertNotIn("git hash-object patches/", source, workflow.name)
            for match in re.findall(
                r"patches/(?:libzt|zerotierone)/[\w-]+\.patch", source
            ):
                self.assertTrue((ROOT / match).is_file(), f"{workflow.name}: {match}")


if __name__ == "__main__":
    unittest.main()
