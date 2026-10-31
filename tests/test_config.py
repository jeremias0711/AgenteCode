import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from orchestrator import Config, ConfigError, build_env, locate_claude
from orchestrator.config import ENV_CLAUDE, ENV_GUARD_SHA, ENV_LOG_DIR, ENV_PROJECT

PY = sys.executable


class LocateClaude(unittest.TestCase):
    def test_explicit_has_priority(self):
        self.assertEqual(locate_claude(PY, env={ENV_CLAUDE: "no-existe.exe"}, which=lambda n: None), (PY,))

    def test_explicit_sequence_keeps_prefix_args(self):
        self.assertEqual(locate_claude([PY, "-I"], env={}, which=lambda n: None), (PY, "-I"))

    def test_environment_variable(self):
        self.assertEqual(locate_claude(env={ENV_CLAUDE: PY}, which=lambda n: None), (PY,))

    def test_path_lookup_is_last(self):
        self.assertEqual(locate_claude(env={}, which=lambda n: PY if n == "claude" else None), (PY,))

    def test_not_found_explains_how_to_fix_without_vscode_paths(self):
        with self.assertRaises(ConfigError) as cm:
            locate_claude(env={}, which=lambda n: None)
        msg = str(cm.exception)
        self.assertIn(ENV_CLAUDE, msg)
        self.assertIn("claude install", msg)
        self.assertNotIn(".vscode", msg.replace("de VS Code", ""))

    def test_missing_file_and_scripts_are_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            for name in ("claude.cmd", "claude.bat", "claude.ps1", "claude.js"):
                p = Path(d) / name
                p.write_text("x")
                with self.subTest(name=name), self.assertRaises(ConfigError):
                    locate_claude(str(p))
            with self.assertRaises(ConfigError):
                locate_claude(str(Path(d) / "no-existe.exe"))
        with self.assertRaises(ConfigError):
            locate_claude(["", "x"])


class ConfigValidation(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)

    def make(self, **kw):
        return Config(project_root=self.root, claude_command=(PY,), log_dir=self.root / "logs", **kw)

    def test_valid_defaults(self):
        c = self.make()
        self.assertEqual((c.default_max_turns, c.default_timeout_s), (20, 900))
        self.assertTrue(c.require_pinned_hash)

    def test_invalid_values(self):
        for kw in ({"default_max_turns": 0}, {"default_max_turns": 101}, {"default_timeout_s": 0},
                   {"default_timeout_s": 7201}, {"default_max_budget_usd": 0}, {"default_max_budget_usd": 51},
                   {"expected_guard_sha256": "abc"}, {"expected_settings_sha256": "g" * 64},
                   {"env_passthrough": ("CLAUDE_CODE_SIMPLE",)}, {"env_passthrough": ("openai_api_key",)}):
            with self.subTest(kw=kw), self.assertRaises(ConfigError):
                self.make(**kw)
        with self.assertRaises(ConfigError):
            Config(project_root=self.root / "no-existe", claude_command=(PY,), log_dir=self.root)

    def test_from_environment(self):
        env = {ENV_CLAUDE: PY, ENV_PROJECT: str(self.root), ENV_LOG_DIR: str(self.root / "mis_logs"),
               ENV_GUARD_SHA: "a" * 64}
        c = Config.from_environment(env=env)
        self.assertEqual(c.claude_command, (PY,))
        self.assertEqual(c.project_root, self.root.resolve())
        self.assertEqual(c.log_dir, (self.root / "mis_logs").resolve())
        self.assertEqual(c.expected_guard_sha256, "a" * 64)

    def test_default_log_dir_is_inside_orchestrator(self):
        c = Config.from_environment(project_root=self.root, claude=PY, env={})
        self.assertEqual(c.log_dir, (self.root / "orchestrator" / "runs").resolve())


class EnvironmentSanitizing(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.cfg = Config(project_root=self._tmp.name, claude_command=(PY,), log_dir=Path(self._tmp.name) / "l",
                          env_passthrough=("MI_VARIABLE",))

    def test_allowlist_only(self):
        base = {"PATH": "p", "USERPROFILE": "u", "ANTHROPIC_API_KEY": "k", "OPENAI_API_KEY": "SECRETO",
                "openai_org": "x", "CLAUDE_CODE_SIMPLE": "1", "AWS_SECRET_ACCESS_KEY": "s", "MI_VARIABLE": "v",
                "RANDOM": "r"}
        env = build_env(self.cfg, base)
        self.assertEqual(set(env) - {"PYTHONIOENCODING"}, {"PATH", "USERPROFILE", "ANTHROPIC_API_KEY", "MI_VARIABLE"})
        self.assertNotIn("CLAUDE_CODE_SIMPLE", env)
        self.assertNotIn("OPENAI_API_KEY", env)

    def test_case_insensitive_names(self):
        env = build_env(self.cfg, {"Path": "p", "claude_code_simple": "1"})
        self.assertIn("Path", env)
        self.assertNotIn("claude_code_simple", env)


if __name__ == "__main__":
    unittest.main()
