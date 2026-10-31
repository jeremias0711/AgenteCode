import json
import unittest
from pathlib import Path

from orchestrator.integrity import (IntegrityChecker, changed_paths, diff_snapshots, in_scope, out_of_scope,
                                    sha256_file, snapshot_tree)

from .helpers import GUARD_TEXT, ProjectCase, make_project, settings_json, sha


class HookVerification(ProjectCase):
    def checker(self, **kw):
        return IntegrityChecker(self.root, **kw)

    def rewrite_settings(self, **kw):
        (self.root / ".claude" / "settings.json").write_text(json.dumps(settings_json(sha(GUARD_TEXT), **kw)), encoding="utf-8")

    def test_ok(self):
        r = self.checker().verify()
        self.assertTrue(r.ok, r.problems)
        self.assertEqual(r.guard_sha256, sha(GUARD_TEXT))

    def test_modified_guard_is_detected(self):
        (self.root / ".claude" / "hooks" / "guard.py").write_text(GUARD_TEXT + "# x\n", encoding="utf-8")
        r = self.checker().verify()
        self.assertFalse(r.ok)
        self.assertTrue(any("no coincide" in p for p in r.problems))

    def test_empty_truncated_or_missing_guard(self):
        g = self.root / ".claude" / "hooks" / "guard.py"
        for content in (b"", GUARD_TEXT.encode()[:10]):
            g.write_bytes(content)
            self.assertFalse(self.checker().verify().ok)
        g.unlink()
        r = self.checker().verify()
        self.assertFalse(r.ok)
        self.assertIn("Falta .claude/hooks/guard.py", r.problems)

    def test_settings_problems(self):
        s = self.root / ".claude" / "settings.json"
        s.unlink()
        self.assertFalse(self.checker().verify().ok)
        s.write_text("{no es json", encoding="utf-8")
        self.assertFalse(self.checker().verify().ok)
        s.write_text("[]", encoding="utf-8")
        self.assertFalse(self.checker().verify().ok)
        for kw in ({"on_failure": None}, {"on_failure": "continue"}, {"bypass": None}, {"bypass": "enable"},
                   {"matcher": "Bash"}, {"pinned": False}):
            with self.subTest(kw=kw):
                self.rewrite_settings(**kw)
                self.assertFalse(self.checker().verify().ok)

    def test_pin_not_required_when_disabled(self):
        self.rewrite_settings(pinned=False)
        self.assertTrue(self.checker(require_pinned_hash=False).verify().ok)

    def test_pin_mismatch(self):
        (self.root / ".claude" / "settings.json").write_text(json.dumps(settings_json("0" * 64)), encoding="utf-8")
        r = self.checker().verify()
        self.assertFalse(r.ok)

    def test_external_expected_hashes(self):
        r = self.checker().verify()
        self.assertTrue(self.checker(expected_guard_sha256=r.guard_sha256, expected_settings_sha256=r.settings_sha256).verify().ok)
        self.assertFalse(self.checker(expected_guard_sha256="1" * 64).verify().ok)
        self.assertFalse(self.checker(expected_settings_sha256="2" * 64).verify().ok)

    def test_protected_snapshot_detects_changes(self):
        c = self.checker()
        before = c.snapshot_protected()
        self.assertEqual(c.protected_changes(before), [])
        (self.root / ".claude" / "hooks" / "nuevo.py").write_text("x", encoding="utf-8")
        (self.root / "templates" / "originals" / "a.txt").write_text("cambiado\n", encoding="utf-8")
        (self.root / ".claude" / "settings.local.json").write_text('{"a":1}', encoding="utf-8")
        ch = c.protected_changes(before)
        self.assertIn(".claude/hooks/nuevo.py", ch)
        self.assertIn("templates/originals/a.txt", ch)
        self.assertIn(".claude/settings.local.json", ch)

    def test_protected_snapshot_detects_deletion_and_new_original(self):
        c = self.checker()
        before = c.snapshot_protected()
        (self.root / ".claude" / "hooks" / "protected_paths.local.txt").unlink()
        (self.root / "templates" / "originals" / "b.txt").write_text("nuevo", encoding="utf-8")
        ch = c.protected_changes(before)
        self.assertIn(".claude/hooks/protected_paths.local.txt", ch)
        self.assertIn("templates/originals/b.txt", ch)


class TreeSnapshots(ProjectCase):
    def test_snapshot_and_diff(self):
        before = snapshot_tree(self.root, ignore_dirs={".git"})
        (self.root / "Game" / "Assets" / "Scripts" / "Nuevo.cs").write_text("x", encoding="utf-8")
        (self.root / "Game" / "Assets" / "Scripts" / "Existing.cs").write_text("class Otra {}\n", encoding="utf-8")
        (self.root / "CLAUDE.md").unlink()
        d = diff_snapshots(before, snapshot_tree(self.root, ignore_dirs={".git"}))
        self.assertEqual(d["added"], ["Game/Assets/Scripts/Nuevo.cs"])
        self.assertEqual(d["modified"], ["Game/Assets/Scripts/Existing.cs"])
        self.assertEqual(d["removed"], ["CLAUDE.md"])
        self.assertEqual(len(changed_paths(d)), 3)

    def test_ignored_dirs_and_paths(self):
        (self.root / ".git").mkdir()
        (self.root / ".git" / "HEAD").write_text("x", encoding="utf-8")
        logs = self.root / "orchestrator" / "runs"
        logs.mkdir()
        (logs / "r.jsonl").write_text("x", encoding="utf-8")
        snap = snapshot_tree(self.root, ignore_dirs={".git"}, ignore_paths=[logs])
        self.assertFalse(any(k.startswith(".git/") or k.startswith("orchestrator/runs") for k in snap))

    def test_same_size_content_change_is_detected_by_hash(self):
        p = self.root / "tests" / "x.txt"
        p.write_text("aaaa", encoding="utf-8")
        before = snapshot_tree(self.root)
        stat = p.stat()
        p.write_text("bbbb", encoding="utf-8")
        import os
        os.utime(p, ns=(stat.st_atime_ns, stat.st_mtime_ns))     # mismo tamano y mismo mtime
        self.assertIn("tests/x.txt", diff_snapshots(before, snapshot_tree(self.root))["modified"])

    def test_sha256_file(self):
        p = self.root / "tests" / "h.txt"
        p.write_bytes(b"abc")
        self.assertEqual(sha256_file(p), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")


class ScopeMatching(unittest.TestCase):
    def test_in_scope(self):
        pats = ["Game/Assets/Scripts/**", "tests/*.py", "docs/**/*.md"]
        yes = ["Game/Assets/Scripts/A.cs", "Game/Assets/Scripts/sub/dir/B.cs", "tests/test_a.py", "docs/x.md",
               "docs/a/b/c.md", "GAME/assets/scripts/c.cs"]
        no = ["Game/Assets/Other/A.cs", "tests/sub/test_a.py", "tests/a.txt", "docs/x.txt", "Game/Assets/Scriptsx/A.cs",
              "README.md", "Game/Assets/Scripts"]
        for p in yes:
            self.assertTrue(in_scope(p, pats), p)
        for p in no:
            self.assertFalse(in_scope(p, pats), p)

    def test_forbidden_zones_never_in_scope_even_with_broad_patterns(self):
        for p in (".claude/settings.json", "templates/originals/a.txt", "CLAUDE.md", ".git/index"):
            self.assertFalse(in_scope(p, ["**", "*", ".claude/**", "templates/**"]), p)

    def test_out_of_scope_and_read_only(self):
        self.assertEqual(out_of_scope(["a.cs", "tests/b.py"], ["tests/**"]), ["a.cs"])
        self.assertEqual(out_of_scope(["x"], []), ["x"])
        self.assertEqual(out_of_scope([], []), [])


if __name__ == "__main__":
    unittest.main()
