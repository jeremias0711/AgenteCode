import json
import sys
import unittest

from orchestrator import ClaudeRunner, Config, ForbiddenOptionError, Task, validate_args
from orchestrator.claude_runner import KNOWN_DANGEROUS
from orchestrator.task import ALWAYS_DISALLOWED

from .helpers import ProjectCase


DENIED = ",".join(ALWAYS_DISALLOWED)


def base_args(tools="Read,Grep,Glob", allowed="Read,Grep,Glob"):
    return ["-p", "--output-format", "json", "--permission-prompts", "none", "--max-turns", "5",
            "--max-budget-usd", "1", "--tools", tools, "--allowedTools", allowed, "--disallowedTools", DENIED]


def value_of(args, flag):
    return args[args.index(flag) + 1]


class BuildArgs(ProjectCase):
    def setUp(self):
        super().setUp()
        self.runner_ = self.runner()

    def test_read_only_task(self):
        a = self.runner_.build_args(Task(prompt="x"))
        self.assertEqual(a[:5], ["-p", "--output-format", "json", "--permission-prompts", "none"])
        self.assertEqual(value_of(a, "--permission-mode"), "dontAsk")
        self.assertEqual(value_of(a, "--tools"), "Read,Grep,Glob")
        self.assertEqual(value_of(a, "--allowedTools"), "Read,Grep,Glob")
        self.assertIn("--strict-mcp-config", a)
        self.assertIn("--no-session-persistence", a)
        self.assertEqual(value_of(a, "--max-turns"), "20")
        self.assertEqual(value_of(a, "--max-budget-usd"), "1")

    def test_prompt_never_in_args(self):
        secret = "PROMPT-SECRETO-123"
        self.assertFalse(any(secret in x for x in self.runner_.build_args(Task(prompt=secret))))

    def test_edit_rules_and_overrides(self):
        t = Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"], agent="unity-game-developer", model="sonnet",
                 max_turns=7, max_budget_usd=2.5, json_schema={"type": "object"})
        a = self.runner_.build_args(t)
        self.assertEqual(value_of(a, "--tools"), "Read,Grep,Glob,Edit")
        self.assertEqual(value_of(a, "--allowedTools"), "Read,Grep,Glob,Edit(/Game/Assets/Scripts/**)")
        self.assertEqual(value_of(a, "--agent"), "unity-game-developer")
        self.assertEqual(value_of(a, "--model"), "sonnet")
        self.assertEqual(value_of(a, "--max-turns"), "7")
        self.assertEqual(value_of(a, "--max-budget-usd"), "2.5")
        self.assertEqual(json.loads(value_of(a, "--json-schema")), {"type": "object"})

    def test_disallowed_tools_always_present(self):
        a = self.runner_.build_args(Task(prompt="x", allowed_tools=("Read",)))
        self.assertEqual(value_of(a, "--disallowedTools"), ",".join(ALWAYS_DISALLOWED))
        for needle in ("Bash(git commit*)", "Bash(git push*)", "Bash(git merge*)", "Bash(pip install*)", "WebFetch"):
            self.assertIn(needle, value_of(a, "--disallowedTools").split(","))

    def test_no_forbidden_option_is_ever_built(self):
        for t in (Task(prompt="x"), Task(prompt="x", allowed_paths=["tests/**"], agent="code-reviewer")):
            a = self.runner_.build_args(t)
            self.assertFalse({x.split("=")[0] for x in a} & KNOWN_DANGEROUS)

    def test_full_command_starts_with_configured_prefix(self):
        cmd = self.runner_.command_for(Task(prompt="x"))
        self.assertEqual(cmd[0], sys.executable)
        self.assertEqual(cmd[len(self.runner_.config.claude_command)], "-p")


class ValidateArgs(unittest.TestCase):
    def test_valid_base(self):
        validate_args(base_args())

    def test_forbidden_options_rejected(self):
        for extra in (["--dangerously-skip-permissions"], ["--allow-dangerously-skip-permissions"], ["--bare"],
                      ["--bare=1"], ["--settings", "x.json"], ["--settings={}"], ["--setting-sources", "user"],
                      ["--add-dir", "C:\\"], ["--mcp-config", "x"], ["--agents", "{}"], ["--plugin-dir", "x"],
                      ["--system-prompt", "x"], ["--append-system-prompt", "x"], ["--resume", "abc"], ["-r"],
                      ["--continue"], ["-c"], ["--cloud"], ["--bg"], ["--worktree"],
                      ["--DANGEROUSLY-SKIP-PERMISSIONS"], ["--BARE"]):
            with self.subTest(extra=extra), self.assertRaises(ForbiddenOptionError):
                validate_args(base_args() + extra)

    def test_unknown_flags_and_positional_rejected(self):
        for extra in (["--unknown"], ["-x"], ["tarea en la linea de comandos"], ["--model"], ["--no-session-persistence=1"]):
            with self.subTest(extra=extra), self.assertRaises(ForbiddenOptionError):
                validate_args(base_args() + extra)

    def test_required_flags_and_values(self):
        full = base_args()
        cases = [
            [a for a in full if a != "-p"],
            ["-p", "--output-format", "text", "--permission-prompts", "none", "--max-turns", "5", "--max-budget-usd", "1",
             "--tools", "Read", "--allowedTools", "Read"],
            ["-p", "--output-format", "json", "--permission-prompts", "host", "--max-turns", "5", "--max-budget-usd", "1",
             "--tools", "Read", "--allowedTools", "Read"],
            ["-p", "--output-format", "json", "--max-turns", "5", "--max-budget-usd", "1", "--tools", "Read"],
            ["-p", "--output-format", "json", "--permission-prompts", "none", "--max-budget-usd", "1", "--tools", "Read"],
            ["-p", "--output-format", "json", "--permission-prompts", "none", "--max-turns", "5", "--tools", "Read"],
            full + ["--permission-mode", "bypassPermissions"],
            full + ["--permission-mode", "auto"],
            base_args(allowed="Read,--bare"),
        ]
        for a in cases:
            with self.subTest(args=a), self.assertRaises(ForbiddenOptionError):
                validate_args(a)

    def test_inline_equals_form(self):
        validate_args(["-p", "--output-format=json", "--permission-prompts=none", "--max-turns=3", "--max-budget-usd=1",
                       "--tools=Read,Grep", "--disallowedTools=" + DENIED])
        with self.assertRaises(ForbiddenOptionError):
            validate_args(["-p", "--output-format=json", "--permission-prompts=host", "--max-turns=3", "--max-budget-usd=1",
                           "--tools=Read", "--disallowedTools=" + DENIED])

    def test_disallowed_tools_cannot_be_removed_or_reduced(self):
        a = base_args()
        i = a.index("--disallowedTools")
        with self.assertRaises(ForbiddenOptionError):                     # ausente
            validate_args(a[:i] + a[i + 2:])
        for value in ("", "WebFetch", ",".join(ALWAYS_DISALLOWED[:-1]), ",".join(ALWAYS_DISALLOWED[2:])):
            with self.subTest(value=value[:30]), self.assertRaises(ForbiddenOptionError):
                validate_args(a[:i] + ["--disallowedTools", value] + a[i + 2:])
        validate_args(a[:i] + ["--disallowedTools", DENIED + ",Bash(extra*)"] + a[i + 2:])   # ampliar si se puede

    def test_non_string_rejected(self):
        with self.assertRaises(ForbiddenOptionError):
            validate_args(base_args() + [None])

    def test_repeated_options_rejected(self):
        for extra in (["-p"], ["--max-turns", "50"], ["--output-format", "text"], ["--permission-prompts", "host"],
                      ["--allowedTools", "Read,Bash(rm *)"], ["--tools", "Read,Bash"], ["--tools=Read,Bash"],
                      ["--disallowedTools", ""], ["--no-session-persistence"]):
            a = base_args() + (["--no-session-persistence"] if extra == ["--no-session-persistence"] else []) + extra
            with self.subTest(extra=extra), self.assertRaises(ForbiddenOptionError):
                validate_args(a)


if __name__ == "__main__":
    unittest.main()
