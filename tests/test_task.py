import unittest

from orchestrator import Task, TaskError
from orchestrator.task import ALWAYS_DISALLOWED, ALLOWED_SHELL_TOOLS, is_forbidden_write, normalize_scope_pattern


class TaskValidation(unittest.TestCase):
    def test_defaults_are_read_only_and_least_privilege(self):
        t = Task(prompt="hola")
        self.assertTrue(t.read_only)
        self.assertEqual(t.allowed_tools, ("Read", "Grep", "Glob"))
        self.assertEqual(t.permission_mode, "dontAsk")
        self.assertEqual(t.edit_rules(), ())

    def test_prompt_validation(self):
        for bad in ("", "   ", None, "x\x00y", "a" * 200_001):
            with self.subTest(bad=bad if bad is None or len(str(bad)) < 20 else "largo"):
                with self.assertRaises(TaskError):
                    Task(prompt=bad)

    def test_tools_must_be_explicitly_allowed(self):
        for bad in ("Bash", "PowerShell", "Edit", "Write", "MultiEdit", "WebFetch", "WebSearch", "Task",
                    "Bash(git push*)", "Bash(git commit*)", "Bash(rm *)", "Bash(*)", "Read(/secrets/**)", "read"):
            with self.subTest(tool=bad):
                with self.assertRaises(TaskError):
                    Task(prompt="x", allowed_tools=(bad,))

    def test_allowed_shell_patterns_are_accepted(self):
        for ok in sorted(ALLOWED_SHELL_TOOLS)[:6]:
            Task(prompt="x", allowed_tools=("Read", ok))

    def test_always_disallowed_covers_git_writes_installs_and_web(self):
        joined = " ".join(ALWAYS_DISALLOWED)
        for needle in ("git commit", "git push", "git merge", "git reset", "git clean", "pip install", "npm install",
                       "winget", "WebFetch", "WebSearch", "gh *"):
            self.assertIn(needle, joined)

    def test_allowed_paths_derive_edit_rules(self):
        t = Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**", "tests\\unit\\*.cs"])
        self.assertEqual(t.allowed_paths, ("Game/Assets/Scripts/**", "tests/unit/*.cs"))
        self.assertEqual(t.edit_rules(), ("Edit(/Game/Assets/Scripts/**)", "Edit(/tests/unit/*.cs)"))
        self.assertFalse(t.read_only)

    def test_bad_paths_rejected(self):
        bad = ["", " ", "/etc", "C:\\x", "C:/x/y", "~/x", "../x", "a/../../b", ".", "./", "*", "**/x", "*.cs",
               "?x/y", "[ab]/x", ".claude/**", ".claude/agents/x.md", ".git/config", "templates/**",
               "templates/originals/**", "templates/originals/a.txt", "CLAUDE.md", ".gitignore", ".gitattributes",
               "Game/**", "Game/Packages/manifest.json", "Game/Packages/packages-lock.json",
               "Game/ProjectSettings/ProjectVersion.txt", ".CLAUDE/x", "Templates/Originals/x"]
        for p in bad:
            with self.subTest(path=p):
                with self.assertRaises(TaskError):
                    Task(prompt="x", allowed_paths=[p])

    def test_good_paths_accepted(self):
        for p in ("Game/Assets/Scripts/**", "Game/Assets/Tests/**", "tests/**", "orchestrator/x.py",
                  "templates/working/**", "Game/Packages/otro.json", "docs/*.md"):
            with self.subTest(path=p):
                normalize_scope_pattern(p)

    def test_agents_limits_and_modes(self):
        Task(prompt="x", agent="code-reviewer")
        for bad in ("evil", "../x", "Code-Reviewer", ""):
            with self.assertRaises(TaskError):
                Task(prompt="x", agent=bad)
        for kw in ({"max_turns": 0}, {"max_turns": 101}, {"max_turns": True}, {"max_turns": 1.5},
                   {"timeout_s": 0}, {"timeout_s": 7201}, {"max_budget_usd": 0}, {"max_budget_usd": 51},
                   {"max_budget_usd": -1}, {"permission_mode": "bypassPermissions"}, {"permission_mode": "auto"},
                   {"model": "a b"}, {"model": "--bare"}, {"json_schema": "x"}):
            with self.subTest(kw=kw):
                with self.assertRaises(TaskError):
                    Task(prompt="x", **kw)

    def test_plan_mode_cannot_write(self):
        Task(prompt="x", permission_mode="plan")
        with self.assertRaises(TaskError):
            Task(prompt="x", permission_mode="plan", allowed_paths=["tests/**"])

    def test_write_tasks_require_dont_ask(self):
        """Fase 3: con allowed_paths solo vale dontAsk (acceptEdits no acota la escritura; verificado en la Fase 2.2)."""
        t = Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"])
        self.assertEqual(t.permission_mode, "dontAsk")
        Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"], permission_mode="dontAsk")
        for mode in ("acceptEdits", "plan"):
            with self.subTest(mode=mode), self.assertRaises(TaskError):
                Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"], permission_mode=mode)
            with self.subTest(mode=mode, write_tool=True), self.assertRaises(TaskError):
                Task(prompt="x", allowed_paths=["tests/**"], permission_mode=mode, allow_write_tool=True,
                     write_justification="justificacion suficientemente larga para la prueba")
        # tareas de solo lectura: los demas modos siguen permitidos
        for mode in ("acceptEdits", "plan", "dontAsk"):
            Task(prompt="x", permission_mode=mode)

    def test_is_forbidden_write(self):
        for p in (".claude/settings.json", ".claude/hooks/guard.py", ".git/HEAD", "templates/originals/a.txt",
                  "CLAUDE.md", "claude.md", "Game/Packages/manifest.json", ".CLAUDE\\x"):
            self.assertTrue(is_forbidden_write(p), p)
        for p in ("Game/Assets/Scripts/A.cs", "tests/x.py", "templates/working/a", "README.md"):
            self.assertFalse(is_forbidden_write(p), p)


if __name__ == "__main__":
    unittest.main()
