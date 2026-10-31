import json
import os
import time
import unittest
from unittest import mock

from orchestrator import ClaudeRunner, ForbiddenOptionError, RunLogger, Status, Task
from orchestrator.claude_runner import parse_claude_json

from .helpers import GUARD_TEXT, ProjectCase, pid_alive


class ParseJson(unittest.TestCase):
    def test_plain_and_noisy(self):
        self.assertEqual(parse_claude_json('{"a": 1}'), {"a": 1})
        self.assertEqual(parse_claude_json('Aviso: reglas ignoradas\n{"a": 2}\n'), {"a": 2})
        self.assertEqual(parse_claude_json('linea\n  {"a": 3}'), {"a": 3})

    def test_invalid(self):
        for bad in ("", "   ", "no json", "[1,2]", '{"a": '):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                parse_claude_json(bad)


class RunnerExecution(ProjectCase):
    def record(self):
        return json.loads((self.scen_dir / "rec.json").read_text(encoding="utf-8"))

    def run_task(self, task=None, **scen):
        scen.setdefault("record", str(self.scen_dir / "rec.json"))
        r = self.runner(**scen)
        return r, r.run(task or Task(prompt="hola"))

    # ------------------------------------------------ entrada / ejecucion
    def test_ok_run_stdin_args_cwd(self):
        prompt = "Escribe un resumen.\nSegunda linea con acentos: ñ é ü"
        r, res = self.run_task(Task(prompt=prompt, label="demo"))
        self.assertEqual(res.status, Status.OK, res.problems)
        self.assertTrue(res.ok)
        self.assertEqual((res.result_text, res.num_turns, res.session_id, res.cost_usd), ("hecho", 2, "fake-session", 0.0123))
        rec = self.record()
        self.assertEqual(rec["stdin"], prompt)                       # el prompt viaja por stdin
        self.assertEqual(os.path.normcase(rec["cwd"]), os.path.normcase(str(self.root)))   # directorio de trabajo
        self.assertEqual(rec["args"], res.args)
        self.assertFalse(any(prompt.split("\n")[0] in a for a in rec["args"]))
        self.assertFalse(res.needs_authorization)

    def test_environment_is_sanitized_in_child(self):
        with mock.patch.dict(os.environ, {"OPENAI_API_KEY": "SECRETO", "CLAUDE_CODE_SIMPLE": "1", "MI_TOKEN": "x",
                                          "ANTHROPIC_API_KEY": "clave-anthropic"}):
            self.run_task()
        env = self.record()["env"]
        for k in ("OPENAI_API_KEY", "CLAUDE_CODE_SIMPLE", "MI_TOKEN"):
            self.assertNotIn(k, env)
        self.assertEqual(env.get("ANTHROPIC_API_KEY"), "clave-anthropic")

    def test_noise_before_json_is_tolerated(self):
        _, res = self.run_task(noise="Ignoring 6 permissions.allow entries")
        self.assertEqual(res.status, Status.OK)

    def test_no_shell_is_used(self):
        with mock.patch("orchestrator.claude_runner.subprocess.Popen", side_effect=RuntimeError("stop")) as popen:
            r = self.runner()
            res = r.run(Task(prompt="x"))
        self.assertEqual(res.status, Status.ERROR)
        kwargs = popen.call_args.kwargs
        self.assertIs(kwargs["shell"], False)
        self.assertIsInstance(popen.call_args.args[0], list)

    def test_check_cli(self):
        self.assertEqual(self.runner().check_cli(), "0.0.0-fake")

    # ------------------------------------------------------------ errores
    def test_nonzero_exit_is_error(self):
        _, res = self.run_task(exit=3, stderr="fallo interno")
        self.assertEqual(res.status, Status.ERROR)
        self.assertEqual(res.returncode, 3)
        self.assertIn("fallo interno", res.stderr_tail)

    def test_invalid_output_is_error(self):
        for raw in ("", "texto sin json", "[1]", '{"truncado": '):
            with self.subTest(raw=raw):
                _, res = self.run_task(stdout_raw=raw)
                self.assertEqual(res.status, Status.ERROR)

    def test_claude_reported_errors(self):
        for resp in ({"is_error": True, "result": "boom"}, {"subtype": "error_max_turns"},
                     {"subtype": "error_during_execution"}):
            with self.subTest(resp=resp):
                _, res = self.run_task(response=resp)
                self.assertEqual(res.status, Status.ERROR)

    def test_permission_denials_stop_and_report(self):
        den = [{"tool_name": "Bash", "tool_use_id": "t1", "tool_input": {"command": "git push"}},
               {"tool_name": "Edit", "tool_input": {"file_path": "x"}}, "cadena-rara"]
        _, res = self.run_task(response={"permission_denials": den})
        self.assertEqual(res.status, Status.DENIED)
        self.assertTrue(res.needs_authorization)
        self.assertEqual([d["tool_name"] for d in res.denials], ["Bash", "Edit", None])
        self.assertIn("git push", res.denials[0]["tool_input"])
        self.assertIn("autorice a mano", res.summary)

    def test_runner_does_not_retry_after_denial(self):
        scen = self.scenario(record=str(self.scen_dir / "rec.json"), response={"permission_denials": [{"tool_name": "Bash"}]})
        calls = []
        r = ClaudeRunner(self.config(scen))
        original = r._execute
        r._execute = lambda *a, **k: (calls.append(1), original(*a, **k))[1]
        r.run(Task(prompt="x"))
        self.assertEqual(len(calls), 1)

    # ------------------------------------------------------------- tiempo
    def test_timeout_kills_process_tree(self):
        pidfile = self.scen_dir / "child.pid"
        t0 = time.monotonic()
        _, res = self.run_task(Task(prompt="x", timeout_s=2), sleep=60, spawn_child=str(pidfile))
        self.assertLess(time.monotonic() - t0, 30)
        self.assertEqual(res.status, Status.TIMEOUT)
        self.assertTrue(res.timed_out)
        pid = int(pidfile.read_text())
        deadline = time.monotonic() + 10
        while pid_alive(pid) and time.monotonic() < deadline:
            time.sleep(0.2)
        self.assertFalse(pid_alive(pid), "el proceso hijo sigue vivo tras el timeout")

    # ------------------------------------------------------------- alcance
    def test_write_inside_scope_is_ok(self):
        _, res = self.run_task(Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"]),
                               write=["Game/Assets/Scripts/Nuevo.cs", "Game/Assets/Scripts/Existing.cs"])
        self.assertEqual(res.status, Status.OK, res.problems)
        self.assertEqual(res.changed_files, ["Game/Assets/Scripts/Existing.cs", "Game/Assets/Scripts/Nuevo.cs"])
        self.assertEqual(res.out_of_scope_files, [])

    def test_write_outside_scope_is_violation(self):
        _, res = self.run_task(Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"]),
                               write=["Game/Assets/Scripts/Nuevo.cs", "Game/Assets/Otro/B.cs", "README.md"])
        self.assertEqual(res.status, Status.SCOPE_VIOLATION)
        self.assertEqual(res.out_of_scope_files, ["Game/Assets/Otro/B.cs", "README.md"])
        self.assertIn("revise y revierta a mano", res.summary)

    def test_read_only_task_any_change_is_violation(self):
        _, res = self.run_task(Task(prompt="x"), write=["Game/Assets/Scripts/Nuevo.cs"])
        self.assertEqual(res.status, Status.SCOPE_VIOLATION)

    def test_delete_outside_scope_is_violation(self):
        _, res = self.run_task(Task(prompt="x", allowed_paths=["tests/**"]), delete=["Game/Assets/Scripts/Existing.cs"])
        self.assertEqual(res.status, Status.SCOPE_VIOLATION)
        self.assertEqual(res.out_of_scope_files, ["Game/Assets/Scripts/Existing.cs"])

    def test_log_dir_changes_are_not_counted_as_project_changes(self):
        _, res = self.run_task()
        self.assertEqual(res.changed_files, [])

    # ---------------------------------------------------------- integridad
    def test_tampering_with_protected_files_during_run(self):
        cases = {
            "guard.py": ".claude/hooks/guard.py", "settings.json": ".claude/settings.json",
            "settings.local.json": ".claude/settings.local.json", "original": "templates/originals/a.txt",
            "nuevo original": "templates/originals/nuevo.txt", "hook nuevo": ".claude/hooks/otro.py",
        }
        for name, rel in cases.items():
            with self.subTest(name=name):
                _, res = self.run_task(Task(prompt="x", allowed_paths=["Game/Assets/Scripts/**"]), write=[rel])
                self.assertEqual(res.status, Status.INTEGRITY_FAILURE, res.problems)
                self.assertIn(rel, res.protected_changes)
                self.setUp()          # proyecto limpio para el siguiente caso

    def test_preflight_failure_does_not_launch_claude(self):
        (self.root / ".claude" / "hooks" / "guard.py").write_text(GUARD_TEXT + "# manipulado\n", encoding="utf-8")
        rec = self.scen_dir / "rec.json"
        _, res = self.run_task()
        self.assertEqual(res.status, Status.INTEGRITY_FAILURE)
        self.assertFalse(rec.exists(), "Claude Code se lanzo a pesar del fallo de integridad")
        self.assertIsNone(res.returncode)

    def test_preflight_failure_without_pin(self):
        (self.root / ".claude" / "settings.json").write_text(json.dumps({"permissions": {}}), encoding="utf-8")
        _, res = self.run_task()
        self.assertEqual(res.status, Status.INTEGRITY_FAILURE)

    def test_integrity_beats_other_problems(self):
        _, res = self.run_task(Task(prompt="x"), write=[".claude/hooks/guard.py", "README.md"],
                               response={"permission_denials": [{"tool_name": "Bash"}]})
        self.assertEqual(res.status, Status.INTEGRITY_FAILURE)

    def test_scope_beats_denied_and_ok_needs_none(self):
        _, res = self.run_task(Task(prompt="x"), write=["README.md"],
                               response={"permission_denials": [{"tool_name": "Bash"}]})
        self.assertEqual(res.status, Status.SCOPE_VIOLATION)
        self.assertTrue(res.needs_authorization)

    # ----------------------------------------------------------- rechazos
    def test_forbidden_options_cannot_reach_the_process(self):
        r = self.runner()
        with mock.patch("orchestrator.claude_runner.validate_args", side_effect=ForbiddenOptionError("x")):
            with self.assertRaises(ForbiddenOptionError):
                r.run(Task(prompt="x"))
        self.assertFalse((self.scen_dir / "rec.json").exists())

    # --------------------------------------------- confianza de la carpeta
    TRUST_TEXT = ('Ignoring 6 permissions.allow entries from .claude/settings.json: this workspace has not been '
                  'trusted. Run Claude Code interactively here once and accept the trust dialog.')

    def test_trust_warning_on_stderr_is_detected_and_logged_but_not_fixed(self):
        before = (self.root / ".claude" / "settings.json").read_bytes()
        r, res = self.run_task(stderr=self.TRUST_TEXT)
        self.assertEqual(res.status, Status.OK)                     # es un aviso: no cambia el estado
        self.assertTrue(res.trust_warning)
        self.assertEqual(len(res.warnings), 1)
        self.assertIn("CONFIANZA", res.warnings[0])
        self.assertIn("no lo hace", res.warnings[0])
        rec = RunLogger.read_records(next((self.root / "orchestrator" / "runs").glob("runs-*.jsonl")))[0]
        self.assertTrue(rec["trust_warning"])
        self.assertIn("CONFIANZA", rec["warnings"][0])
        # no se debilita nada ni se toca la configuracion
        self.assertEqual((self.root / ".claude" / "settings.json").read_bytes(), before)
        joined = " ".join(res.args)
        for flag in ("--dangerously-skip-permissions", "--settings", "--bare", "bypassPermissions"):
            self.assertNotIn(flag, joined)
        self.assertEqual(res.protected_changes, [])

    def test_trust_warning_on_stdout_noise_is_detected(self):
        _, res = self.run_task(noise=self.TRUST_TEXT)
        self.assertTrue(res.trust_warning)
        self.assertEqual(res.status, Status.OK)

    def test_no_warning_when_claude_does_not_complain(self):
        _, res = self.run_task(stderr="otra cosa", response={"result": "el texto menciona confianza"})
        self.assertFalse(res.trust_warning)
        self.assertEqual(res.warnings, [])
        rec = RunLogger.read_records(next((self.root / "orchestrator" / "runs").glob("runs-*.jsonl")))[0]
        self.assertFalse(rec["trust_warning"])

    def test_trust_phrase_inside_json_result_is_not_a_warning(self):
        _, res = self.run_task(response={"result": self.TRUST_TEXT})
        self.assertFalse(res.trust_warning)

    def test_trust_warning_does_not_hide_other_problems(self):
        _, res = self.run_task(stderr=self.TRUST_TEXT, response={"permission_denials": [{"tool_name": "Bash"}]})
        self.assertEqual(res.status, Status.DENIED)
        self.assertTrue(res.trust_warning)

    # ------------------------------------------------- campos adicionales
    def test_extra_json_fields_are_captured(self):
        _, res = self.run_task(response={"terminal_reason": "completed", "stop_reason": "end_turn",
                                         "usage": {"input_tokens": 5}, "modelUsage": {"modelo": {"costUSD": 0.01}}})
        self.assertEqual((res.terminal_reason, res.stop_reason), ("completed", "end_turn"))
        self.assertEqual(res.usage, {"input_tokens": 5})
        self.assertEqual(res.model_usage["modelo"]["costUSD"], 0.01)
        rec = RunLogger.read_records(next((self.root / "orchestrator" / "runs").glob("runs-*.jsonl")))[0]
        self.assertEqual(rec["terminal_reason"], "completed")
        self.assertEqual(rec["tools"], ["Read", "Grep", "Glob"])

    def test_max_turns_error_shape(self):
        _, res = self.run_task(exit=1, response={"subtype": "error_max_turns", "is_error": True, "terminal_reason": "max_turns",
                                                 "errors": [], "num_turns": 2}, )
        self.assertEqual(res.status, Status.ERROR)
        self.assertEqual(res.terminal_reason, "max_turns")

    def test_wrong_types_in_extra_fields_are_ignored(self):
        _, res = self.run_task(response={"terminal_reason": 5, "usage": "x", "modelUsage": [1], "total_cost_usd": "gratis"})
        self.assertEqual(res.status, Status.OK)
        self.assertEqual((res.terminal_reason, res.usage, res.model_usage, res.cost_usd), (None, {}, {}, None))

    # ------------------------------------------------------------ registro
    def test_every_run_is_logged(self):
        r, res = self.run_task(Task(prompt="prompt reservado " + "x" * 400, label="etiqueta"))
        files = sorted((self.root / "orchestrator" / "runs").glob("runs-*.jsonl"))
        self.assertEqual(len(files), 1)
        recs = RunLogger.read_records(files[0])
        self.assertEqual(len(recs), 1)
        rec = recs[0]
        self.assertEqual((rec["run_id"], rec["task_id"], rec["status"], rec["label"]), (res.run_id, res.task_id, "ok", "etiqueta"))
        self.assertEqual(rec["prompt"]["chars"], len("prompt reservado " + "x" * 400))
        self.assertEqual(len(rec["prompt"]["preview"]), 200)             # no se guarda el prompt completo
        self.assertNotIn("x" * 300, json.dumps(rec["prompt"]))
        self.assertTrue(rec["integrity_before"]["ok"] and rec["integrity_after"]["ok"])
        self.assertNotIn("env", rec)
        self.assertTrue((self.root / "orchestrator" / "runs" / ".gitignore").is_file())
        r.run(Task(prompt="otra"))
        self.assertEqual(len(RunLogger.read_records(files[0])), 2)

    def test_failed_runs_are_logged_too(self):
        (self.root / ".claude" / "hooks" / "guard.py").write_text("manipulado", encoding="utf-8")
        _, res = self.run_task()
        rec = RunLogger.read_records(next((self.root / "orchestrator" / "runs").glob("runs-*.jsonl")))[0]
        self.assertEqual(rec["status"], "integrity_failure")
        self.assertIsNone(rec["integrity_after"])
        self.assertFalse(rec["integrity_before"]["ok"])

    def test_denials_and_changes_are_logged(self):
        self.run_task(Task(prompt="x"), write=["README.md"], response={"permission_denials": [{"tool_name": "Bash"}]})
        rec = RunLogger.read_records(next((self.root / "orchestrator" / "runs").glob("runs-*.jsonl")))[0]
        self.assertEqual(rec["status"], "scope_violation")
        self.assertEqual(rec["out_of_scope_files"], ["README.md"])
        self.assertEqual(rec["denials"][0]["tool_name"], "Bash")
        self.assertTrue(rec["needs_authorization"])


if __name__ == "__main__":
    unittest.main()
