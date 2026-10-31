"""Minimo privilegio: que herramientas recibe Claude Code y que NO puede recibir."""
import json
import unittest

from orchestrator import ForbiddenOptionError, Status, Task, TaskError, validate_args
from orchestrator.claude_runner import revalidated

from .helpers import ProjectCase
from .test_args import base_args, value_of

FORBIDDEN_FOR_READ_ONLY = ("Bash", "PowerShell", "Edit", "Write", "NotebookEdit", "MultiEdit", "WebFetch", "WebSearch")
JUSTIFICATION = "Crear las clases nuevas de torres pedidas por el usuario en la tarea 12"


class ToolDerivation(unittest.TestCase):
    def test_read_only_default_is_exactly_read_grep_glob(self):
        self.assertEqual(Task(prompt="x").tools(), ("Read", "Grep", "Glob"))
        self.assertEqual(Task(prompt="x", agent="code-reviewer").tools(), ("Read", "Grep", "Glob"))

    def test_read_only_task_has_no_other_tool_whatever_the_other_options(self):
        for kw in ({}, {"permission_mode": "plan"}, {"permission_mode": "acceptEdits"}, {"permission_mode": "dontAsk"},
                   {"agent": "unity-game-developer"}, {"agent": "blender-unity-asset-specialist"},
                   {"model": "sonnet", "max_turns": 99}):
            with self.subTest(kw=kw):
                self.assertEqual(Task(prompt="x", **kw).tools(), ("Read", "Grep", "Glob"))

    def test_only_declared_read_tools_are_enabled(self):
        self.assertEqual(Task(prompt="x", allowed_tools=("Read",)).tools(), ("Read",))
        self.assertEqual(Task(prompt="x", allowed_tools=("Glob", "Read")).tools(), ("Read", "Glob"))

    def test_edit_only_with_allowed_paths(self):
        self.assertNotIn("Edit", Task(prompt="x").tools())
        t = Task(prompt="x", allowed_paths=["tests/**"])
        self.assertEqual(t.tools(), ("Read", "Grep", "Glob", "Edit"))
        self.assertNotIn("Write", t.tools())

    def test_write_requires_explicit_flag_and_justification_and_paths(self):
        t = Task(prompt="x", allowed_paths=["tests/**"], allow_write_tool=True, write_justification=JUSTIFICATION)
        self.assertEqual(t.tools(), ("Read", "Grep", "Glob", "Edit", "Write"))
        bad = ({"allow_write_tool": True, "write_justification": JUSTIFICATION},                       # sin rutas
               {"allowed_paths": ["tests/**"], "allow_write_tool": True},                              # sin justificacion
               {"allowed_paths": ["tests/**"], "allow_write_tool": True, "write_justification": "porque si"},
               {"allowed_paths": ["tests/**"], "allow_write_tool": True, "write_justification": " " * 40},
               {"allowed_paths": ["tests/**"], "allow_write_tool": True, "write_justification": None},
               {"allowed_paths": ["tests/**"], "allow_write_tool": "si", "write_justification": JUSTIFICATION})
        for kw in bad:
            with self.subTest(kw=kw), self.assertRaises(TaskError):
                Task(prompt="x", **kw)

    def test_justification_alone_does_not_enable_write(self):
        t = Task(prompt="x", allowed_paths=["tests/**"], write_justification=JUSTIFICATION)
        self.assertNotIn("Write", t.tools())

    def test_shell_only_with_declared_patterns(self):
        self.assertEqual(Task(prompt="x", allowed_tools=("Read", "Bash(git status*)")).tools(), ("Read", "Bash"))
        self.assertEqual(Task(prompt="x", allowed_tools=("PowerShell(git diff*)",)).tools(), ("PowerShell",))
        t = Task(prompt="x", allowed_tools=("Read", "Bash(git log*)", "PowerShell(dotnet build*)"))
        self.assertEqual(t.tools(), ("Read", "Bash", "PowerShell"))
        for bad in ("Bash", "PowerShell", "Bash(*)", "Bash(rm -rf *)", "PowerShell(Remove-Item *)", "Bash(git push*)"):
            with self.subTest(tool=bad), self.assertRaises(TaskError):
                Task(prompt="x", allowed_tools=("Read", bad))

    def test_task_without_any_tool_is_rejected(self):
        with self.assertRaises(TaskError):
            Task(prompt="x", allowed_tools=())

    def test_permission_rules_are_consistent_with_tools(self):
        t = Task(prompt="x", allowed_tools=("Read", "Bash(git status*)"), allowed_paths=["tests/**"],
                 allow_write_tool=True, write_justification=JUSTIFICATION)
        self.assertEqual(t.permission_rules(), ("Read", "Bash(git status*)", "Edit(/tests/**)"))


class ReadOnlyCannotReachWriteOrShell(ProjectCase):
    """Una tarea de solo lectura nunca llega a recibir Bash, PowerShell, Edit ni Write."""

    def test_arguments_of_a_read_only_task(self):
        a = self.runner().build_args(Task(prompt="x"))
        tools = value_of(a, "--tools").split(",")
        self.assertEqual(tools, ["Read", "Grep", "Glob"])
        allowed = value_of(a, "--allowedTools")
        for name in FORBIDDEN_FOR_READ_ONLY:
            self.assertNotIn(name, tools)
            self.assertNotIn(name, allowed)

    def test_real_process_receives_exactly_those_tools(self):
        r = self.runner(record=str(self.scen_dir / "rec.json"))
        res = r.run(Task(prompt="x"))
        self.assertEqual(res.status, Status.OK, res.problems)
        args = json.loads((self.scen_dir / "rec.json").read_text(encoding="utf-8"))["args"]
        self.assertEqual(args.count("--tools"), 1)
        self.assertEqual(value_of(args, "--tools"), "Read,Grep,Glob")
        self.assertEqual(res.tools, ["Read", "Grep", "Glob"])

    def test_every_forbidden_tool_is_rejected_at_task_level(self):
        for name in FORBIDDEN_FOR_READ_ONLY + ("Bash(*)", "Edit(/**)", "Write(/x)"):
            with self.subTest(tool=name), self.assertRaises(TaskError):
                Task(prompt="x", allowed_tools=("Read", name))

    def test_tampered_task_is_revalidated_before_anything_is_derived(self):
        t = Task(prompt="x")
        for field, value in (("allowed_tools", ("Read", "Bash(rm -rf *)")), ("allowed_tools", ("Bash",)),
                             ("allowed_paths", ("/etc",)), ("allowed_paths", (".claude/**",)),
                             ("permission_mode", "bypassPermissions"), ("agent", "otro")):
            with self.subTest(field=field, value=value):
                tampered = Task(prompt="x")
                object.__setattr__(tampered, field, value)          # salta el __post_init__ de la dataclass
                with self.assertRaises(TaskError):
                    revalidated(tampered)
                with self.assertRaises(TaskError):
                    self.runner().build_args(tampered)
        self.assertEqual(revalidated(t).tools(), ("Read", "Grep", "Glob"))

    def test_run_refuses_a_tampered_task_without_launching_claude(self):
        tampered = Task(prompt="x")
        object.__setattr__(tampered, "allowed_tools", ("Read", "Bash(git status*)", "Bash(git push*)"))
        with self.assertRaises(TaskError):
            self.runner(record=str(self.scen_dir / "rec.json")).run(tampered)
        self.assertFalse((self.scen_dir / "rec.json").exists())


class ArgumentInjection(unittest.TestCase):
    """validate_args rechaza cualquier intento de anadir herramientas por la linea de comandos."""

    def rejected(self, args):
        with self.assertRaises(ForbiddenOptionError):
            validate_args(args)

    def test_extra_tools_flag_or_inline_form(self):
        for extra in (["--tools", "Bash"], ["--tools=Read,Bash"], ["--tools", "Read,Grep,Glob,Edit"]):
            with self.subTest(extra=extra):
                self.rejected(base_args() + extra)

    def test_invalid_tools_values(self):
        for tools in ("", "Bash", "Read,Bash,Bash", "Read Bash", "read,grep", "default", "Task", "WebFetch", "Read,*",
                      "Read,--bare", "NotebookEdit", "MultiEdit", "Read,", ",Read", "Read,,Grep"):
            with self.subTest(tools=tools):
                self.rejected(base_args(tools=tools, allowed="Read"))

    def test_tools_flag_is_mandatory(self):
        a = base_args()
        i = a.index("--tools")
        self.rejected(a[:i] + a[i + 2:])

    def test_allowed_tools_cannot_grant_what_tools_does_not_enable(self):
        for allowed in ("Read,Bash", "Read,Bash(git status*)", "Read,PowerShell(git status*)", "Read,Edit(/tests/**)",
                        "Read,Write", "Read,Write(/x)", "Read,Edit", "Read,WebFetch", "Read,mcp__srv__tool", "Read,Task"):
            with self.subTest(allowed=allowed):
                self.rejected(base_args(allowed=allowed))

    def test_write_capable_tools_need_explicit_scoped_rules(self):
        self.rejected(base_args(tools="Read,Edit", allowed="Read"))                       # Edit sin regla
        self.rejected(base_args(tools="Read,Edit", allowed="Read,Edit"))                  # Edit sin ruta
        self.rejected(base_args(tools="Read,Edit,Write", allowed="Read,Edit(/tests/**),Write"))
        self.rejected(base_args(tools="Read,Write", allowed="Read"))                      # Write sin Edit(ruta)
        self.rejected(base_args(tools="Read,Bash", allowed="Read"))                       # shell sin patron
        self.rejected(base_args(tools="Read,Bash", allowed="Read,Bash"))
        self.rejected(base_args(tools="Read,PowerShell", allowed="Read,PowerShell"))

    def test_write_requires_dont_ask_mode(self):
        """Fase 3: con escritura el modo debe ser dontAsk (con acceptEdits las reglas Edit(/ruta) no acotan nada)."""
        write = base_args(tools="Read,Edit", allowed="Read,Edit(/tests/**)")
        validate_args(write + ["--permission-mode", "dontAsk"])
        for mode in ("acceptEdits", "plan", "manual", "auto", "bypassPermissions"):
            with self.subTest(mode=mode):
                self.rejected(write + ["--permission-mode", mode])
        self.rejected(write)                                                       # sin modo explicito
        self.rejected(write + ["--permission-mode=acceptEdits"])
        full = base_args(tools="Read,Edit,Write", allowed="Read,Edit(/tests/**)")
        self.rejected(full + ["--permission-mode", "acceptEdits"])
        validate_args(full + ["--permission-mode", "dontAsk"])
        # sin escritura cualquier modo admitido sigue valiendo
        validate_args(base_args() + ["--permission-mode", "acceptEdits"])
        validate_args(base_args() + ["--permission-mode", "plan"])

    def test_scoped_rules_are_accepted(self):
        validate_args(base_args(tools="Read,Edit", allowed="Read,Edit(/tests/**)") + ["--permission-mode", "dontAsk"])
        validate_args(base_args(tools="Read,Edit,Write", allowed="Read,Edit(/tests/**)") + ["--permission-mode", "dontAsk"])
        validate_args(base_args(tools="Read,Bash", allowed="Read,Bash(git status*)"))
        validate_args(base_args(tools="Read,PowerShell", allowed="Read,PowerShell(git diff*)"))

    def test_tools_flag_smuggled_in_other_values(self):
        self.rejected(base_args() + ["--model", "x", "--tools", "Bash"])
        self.rejected(base_args(allowed="Read,--tools"))
        self.rejected(base_args() + ["Bash"])                                              # posicional


if __name__ == "__main__":
    unittest.main()
