"""Ejecutor de Claude Code: construye la linea de comandos, la valida, lanza `claude -p` y vigila el resultado.

Garantias (todas comprobadas en tests/):
  * subprocess con LISTA de argumentos; nunca shell=True. El prompt viaja por stdin (no en la linea de comandos).
  * Lista BLANCA de opciones del CLI: cualquier otra se rechaza (ForbiddenOptionError). En particular nunca
    se admiten --dangerously-skip-permissions, --bare (desactiva los hooks), --settings/--setting-sources
    (sustituirian la configuracion de seguridad), --add-dir, --mcp-config, --agents, --system-prompt, --resume...
  * Siempre `--permission-prompts none`: lo que requeriria una autorizacion interactiva se deniega y el
    resultado es Status.DENIED con needs_authorization=True. El coordinador NO reintenta ni escala solo.
  * Herramientas (minimo privilegio): `--tools` fija el conjunto EXACTO que existe para Claude Code, derivado solo de
    la Task: Read/Grep/Glob declaradas; Edit solo con allowed_paths; Write solo con allow_write_tool + justificacion;
    Bash/PowerShell solo con patrones declarados. (Verificado: `--allowedTools` por si solo NO restringe, solo aprueba.)
    `--allowedTools` se mantiene como reglas finas (Edit(/ruta), patrones de shell) coherentes con `--tools`.
    ALWAYS_DISALLOWED (git commit/push/merge..., gh, instaladores, WebFetch/WebSearch) se aplica siempre.
  * Falta de confianza de la carpeta: si Claude Code avisa de que ignora reglas del proyecto, se registra en
    RunResult.warnings/trust_warning y en el log; el coordinador no toca la configuracion de confianza.
  * Entorno saneado por lista blanca; se descarta CLAUDE_CODE_SIMPLE (equivale a --bare) y OPENAI_*.
  * Integridad del hook y de las rutas protegidas ANTES y DESPUES; si falla antes, Claude Code no se lanza.
  * Huella del proyecto antes/despues: los cambios fuera de Task.allowed_paths dan Status.SCOPE_VIOLATION
    (se informa; el coordinador no revierte ni borra nada por su cuenta).
  * Tiempo maximo: al agotarse se mata el ARBOL de procesos (taskkill /T /F en Windows).
  * Cada ejecucion deja un registro en JSONL, tambien las que fallan antes de lanzar Claude Code.

Campos del JSON de salida de `claude -p --output-format json` que se leen (con tolerancia si faltan):
result, is_error, subtype, permission_denials, num_turns, total_cost_usd, session_id.
"""
from __future__ import annotations

import json
import os
import re
import signal
import subprocess
import time
import uuid
from typing import Mapping, Optional, Sequence

from .config import ENV_ALLOWLIST, ENV_NEVER, ENV_NEVER_PREFIXES, Config
from .errors import ForbiddenOptionError
from .integrity import (IntegrityChecker, changed_paths, diff_snapshots, out_of_scope,
                        snapshot_tree)
from .logger import RunLogger
from .results import RunResult, Status
from .task import ALWAYS_DISALLOWED, PERMISSION_MODES, TOOL_NAMES, Task

FLAGS_WITH_VALUE = frozenset({
    "--output-format", "--permission-prompts", "--permission-mode", "--max-turns", "--max-budget-usd",
    "--model", "--tools", "--allowedTools", "--disallowedTools", "--json-schema", "--agent",
})
# Claude Code ignora las reglas `permissions.allow` del proyecto hasta que se acepta el dialogo de confianza.
TRUST_RE = re.compile(r"(?i)(has not been trusted|workspace (?:is )?not (?:been )?trusted|"
                      r"Ignoring \d+ permissions\.allow entries)")
FLAGS_WITHOUT_VALUE = frozenset({"-p", "--no-session-persistence", "--strict-mcp-config"})
# Solo para dar un mensaje claro; la lista blanca anterior ya rechaza cualquier otra opcion.
KNOWN_DANGEROUS = frozenset({
    "--dangerously-skip-permissions", "--allow-dangerously-skip-permissions", "--bare", "--settings",
    "--setting-sources", "--add-dir", "--mcp-config", "--plugin-dir", "--plugin-url", "--agents",
    "--system-prompt", "--system-prompt-file", "--append-system-prompt", "--append-system-prompt-file",
    "--resume", "-r", "--continue", "-c", "--fork-session", "--from-pr", "--bg", "--background", "--cloud",
    "--worktree", "-w", "--chrome", "--ide", "--desktop", "--file", "--betas",
})


def validate_args(args: Sequence[str]) -> None:
    """Defensa final sobre la lista de argumentos ya construida. Lanza ForbiddenOptionError."""
    seen: dict = {}
    i, n = 0, len(args)
    while i < n:
        tok = args[i]
        if not isinstance(tok, str):
            raise ForbiddenOptionError("Argumento que no es texto: %r" % (tok,))
        if not tok.startswith("-"):
            raise ForbiddenOptionError("Argumento posicional inesperado %r: el prompt va por stdin." % tok)
        name, eq, inline = tok.partition("=")
        if name in KNOWN_DANGEROUS or name.lower() in KNOWN_DANGEROUS:
            raise ForbiddenOptionError("Opcion prohibida: %s" % name)
        if name in FLAGS_WITHOUT_VALUE:
            if eq:
                raise ForbiddenOptionError("La opcion %s no admite valor." % name)
            if name in seen:
                raise ForbiddenOptionError("Opcion repetida: %s" % name)
            seen[name] = True
            i += 1
            continue
        if name in FLAGS_WITH_VALUE:
            if eq:
                value, i = inline, i + 1
            else:
                if i + 1 >= n:
                    raise ForbiddenOptionError("Falta el valor de %s." % name)
                value, i = args[i + 1], i + 2
            if name in seen:            # una segunda aparicion podria anular o ampliar la primera
                raise ForbiddenOptionError("Opcion repetida: %s" % name)
            seen[name] = value
            continue
        raise ForbiddenOptionError("Opcion no permitida (no esta en la lista blanca): %s" % tok)
    if "-p" not in seen:
        raise ForbiddenOptionError("Falta -p (modo no interactivo).")
    if seen.get("--output-format") != "json":
        raise ForbiddenOptionError("--output-format debe ser json.")
    if seen.get("--permission-prompts") != "none":
        raise ForbiddenOptionError("--permission-prompts debe ser none en ejecuciones automaticas.")
    mode = seen.get("--permission-mode")
    if mode is not None and mode not in PERMISSION_MODES:
        raise ForbiddenOptionError("--permission-mode no permitido: %r" % (mode,))
    writes = (any(t in str(seen.get("--tools", "")).split(",") for t in ("Edit", "Write"))
              or any(s.startswith("Edit(") for s in str(seen.get("--allowedTools", "")).split(",")))
    if writes and mode != "dontAsk":
        raise ForbiddenOptionError("Con herramientas de escritura el modo de permisos debe ser dontAsk (recibido %r): "
                                   "con otros modos las reglas Edit(/ruta) no acotan la escritura." % (mode,))
    if "--max-turns" not in seen or "--max-budget-usd" not in seen:
        raise ForbiddenOptionError("Faltan los limites --max-turns y --max-budget-usd.")
    for flag in ("--allowedTools", "--disallowedTools"):
        for spec in str(seen.get(flag, "")).split(","):
            if spec.startswith("-"):
                raise ForbiddenOptionError("Especificacion de herramienta sospechosa: %r" % spec)
    denied = set(str(seen.get("--disallowedTools", "")).split(","))
    missing = [d for d in ALWAYS_DISALLOWED if d not in denied]
    if missing:
        raise ForbiddenOptionError("--disallowedTools debe incluir siempre ALWAYS_DISALLOWED; faltan: %s" % ", ".join(missing[:5]))
    _validate_tools(seen)


def _validate_tools(seen: dict) -> None:
    """--tools es obligatorio, esta limitado a TOOL_NAMES y debe ser coherente con --allowedTools:
    ninguna regla puede referirse a una herramienta que no este en --tools, y cada herramienta que
    puede escribir o ejecutar comandos (Edit, Write, Bash, PowerShell) exige reglas explicitas."""
    if "--tools" not in seen:
        raise ForbiddenOptionError("Falta --tools: el conjunto de herramientas debe fijarse de forma explicita.")
    tools = str(seen["--tools"]).split(",")
    if not all(t in TOOL_NAMES for t in tools) or len(set(tools)) != len(tools):
        raise ForbiddenOptionError("--tools solo admite nombres de %s, sin repetir: %r" % (", ".join(TOOL_NAMES), seen["--tools"]))
    allowed = [s for s in str(seen.get("--allowedTools", "")).split(",") if s]
    for spec in allowed:
        base = spec.split("(", 1)[0]
        if base not in TOOL_NAMES:
            raise ForbiddenOptionError("--allowedTools contiene una herramienta desconocida: %r" % spec)
        if base not in tools:
            raise ForbiddenOptionError("--allowedTools menciona %r que no esta habilitada en --tools." % base)
        if base in ("Edit", "Bash", "PowerShell") and "(" not in spec:
            raise ForbiddenOptionError("%s sin patron en --allowedTools: habilita todo el alcance." % base)
        if base == "Write":
            raise ForbiddenOptionError("Write se autoriza con las reglas Edit(/ruta), no con una regla Write propia.")
    for needs, rule in (("Edit", "Edit("), ("Bash", "Bash("), ("PowerShell", "PowerShell(")):
        if needs in tools and not any(s.startswith(rule) for s in allowed):
            raise ForbiddenOptionError("%s esta en --tools pero sin reglas explicitas en --allowedTools." % needs)
    if "Write" in tools and not any(s.startswith("Edit(") for s in allowed):
        raise ForbiddenOptionError("Write esta en --tools sin reglas Edit(/ruta) que acoten su alcance.")


def build_env(config: Config, base: Optional[Mapping[str, str]] = None) -> dict:
    """Entorno saneado: solo la lista blanca (+ config.env_passthrough). Nunca CLAUDE_CODE_SIMPLE ni OPENAI_*."""
    base = os.environ if base is None else base
    allowed = {n.upper() for n in ENV_ALLOWLIST} | {n.upper() for n in config.env_passthrough}
    env = {}
    for k, v in base.items():
        ku = k.upper()
        if ku in ENV_NEVER or ku.startswith(ENV_NEVER_PREFIXES):
            continue
        if ku in allowed:
            env[k] = v
    env["PYTHONIOENCODING"] = "utf-8"
    return env


def kill_tree(proc: subprocess.Popen) -> None:
    """Mata el proceso y todos sus descendientes."""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True, timeout=20)
        else:
            os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
    except Exception:
        pass
    try:
        proc.kill()
    except Exception:
        pass


def parse_claude_json(stdout: str) -> dict:
    """Interpreta la salida de `claude -p --output-format json`. Tolera lineas de aviso antes del JSON."""
    text = stdout.strip()
    if not text:
        raise ValueError("salida vacia")
    candidates = [text] + [ln for ln in reversed(text.splitlines()) if ln.lstrip().startswith("{")]
    for c in candidates:
        try:
            obj = json.loads(c)
        except ValueError:
            continue
        if isinstance(obj, dict):
            return obj
    raise ValueError("la salida no contiene un objeto JSON valido")


def revalidated(task: Task) -> Task:
    """Copia de la tarea que vuelve a pasar TODA la validacion. Task es inmutable, pero object.__setattr__ puede
    saltarsela: el ejecutor no se fia de un objeto ya construido y revalida antes de derivar herramientas o rutas."""
    import dataclasses
    return Task(**{f.name: getattr(task, f.name) for f in dataclasses.fields(task)})


def _short(value, limit=300):
    s = value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, default=str)
    return s if len(s) <= limit else s[:limit] + "..."


class ClaudeRunner:
    def __init__(self, config: Config, logger: Optional[RunLogger] = None,
                 integrity: Optional[IntegrityChecker] = None):
        self.config = config
        self.logger = logger or RunLogger(config.log_dir, config.log_prompt_preview_chars)
        self.integrity = integrity or IntegrityChecker(
            config.project_root, config.expected_guard_sha256, config.expected_settings_sha256,
            config.require_pinned_hash, config.ignore_dirs)

    # ------------------------------------------------------------ argumentos
    def build_args(self, task: Task) -> list:
        c = self.config
        task = revalidated(task)
        mode = task.permission_mode
        tools = list(task.permission_rules())
        args = [
            "-p", "--output-format", "json",
            "--permission-prompts", "none",
            "--permission-mode", mode,
            "--max-turns", str(task.max_turns or c.default_max_turns),
            "--max-budget-usd", "%g" % (task.max_budget_usd or c.default_max_budget_usd),
            "--tools", ",".join(task.tools()),
            "--allowedTools", ",".join(tools),
            "--disallowedTools", ",".join(ALWAYS_DISALLOWED),
            "--strict-mcp-config", "--no-session-persistence",
        ]
        model = task.model or c.default_model
        if model:
            args += ["--model", model]
        if task.agent:
            args += ["--agent", task.agent]
        if task.json_schema is not None:
            args += ["--json-schema", json.dumps(task.json_schema, separators=(",", ":"), ensure_ascii=False)]
        validate_args(args)
        return args

    def command_for(self, task: Task) -> list:
        return list(self.config.claude_command) + self.build_args(task)

    # --------------------------------------------------------------- CLI
    def check_cli(self, timeout_s: int = 30) -> str:
        """`<claude> --version` (no llama al modelo). Sirve para comprobar que hay un CLI funcional."""
        p = subprocess.run(list(self.config.claude_command) + ["--version"], capture_output=True,
                           timeout=timeout_s, env=build_env(self.config), cwd=str(self.config.project_root))
        if p.returncode != 0:
            raise RuntimeError("el CLI de Claude Code devolvio %d: %s" % (p.returncode, p.stderr.decode("utf-8", "replace")[:200]))
        return p.stdout.decode("utf-8", "replace").strip()

    # --------------------------------------------------------------- run
    def run(self, task: Task) -> RunResult:
        c = self.config
        task = revalidated(task)
        run_id = uuid.uuid4().hex[:12]
        t0 = time.monotonic()
        full_cmd = self.command_for(task)                 # valida; puede lanzar ForbiddenOptionError
        args = full_cmd[len(c.claude_command):]
        result = RunResult(run_id=run_id, task_id=task.task_id, status=Status.OK, args=args, tools=list(task.tools()))
        pre = post_report = None
        problems: list = []
        try:
            pre = self.integrity.verify()
            if not pre.ok:
                problems += ["integridad (antes): " + p for p in pre.problems]
                result.status = Status.INTEGRITY_FAILURE
                result.summary = "No se lanzo Claude Code: la comprobacion de integridad previa fallo."
                return self._finish(result, task, problems, t0, pre, None)

            protected_before = self.integrity.snapshot_protected()
            ignore_paths = [c.log_dir]
            tree_before = snapshot_tree(c.project_root, c.ignore_dirs, ignore_paths)

            out, err, rc, timed_out = self._execute(full_cmd, task)
            result.returncode, result.timed_out = rc, timed_out
            result.stderr_tail = err[-1500:]
            self._detect_trust_warning(result, err, out)

            post_report = self.integrity.verify()
            if not post_report.ok:
                problems += ["integridad (despues): " + p for p in post_report.problems]
            prot = self.integrity.protected_changes(protected_before)
            if prot:
                result.protected_changes = prot
                problems.append("Cambiaron archivos protegidos durante la ejecucion: %s" % ", ".join(prot[:10]))
            tree_after = snapshot_tree(c.project_root, c.ignore_dirs, ignore_paths)
            changed = changed_paths(diff_snapshots(tree_before, tree_after))
            result.changed_files = changed
            result.out_of_scope_files = out_of_scope(changed, task.allowed_paths)
            if result.out_of_scope_files:
                problems.append("Cambios fuera del alcance autorizado: %s" % ", ".join(result.out_of_scope_files[:10]))

            integrity_bad = (not post_report.ok) or bool(prot)
            self._interpret(result, out, rc, timed_out, problems, integrity_bad)
            return self._finish(result, task, problems, t0, pre, post_report)
        except Exception as e:                            # nunca perder el registro de una ejecucion
            problems.append("excepcion del coordinador: %s: %s" % (type(e).__name__, e))
            result.status = Status.ERROR
            result.summary = "Error interno del coordinador: %s" % e
            return self._finish(result, task, problems, t0, pre, post_report)

    def _execute(self, full_cmd: list, task: Task):
        timeout = task.timeout_s or self.config.default_timeout_s
        kwargs = {"start_new_session": True} if os.name != "nt" else {}
        proc = subprocess.Popen(full_cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                cwd=str(self.config.project_root), env=build_env(self.config), shell=False, **kwargs)
        timed_out = False
        try:
            out, err = proc.communicate(input=task.prompt.encode("utf-8"), timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            kill_tree(proc)
            try:
                out, err = proc.communicate(timeout=15)
            except Exception:
                out, err = b"", b""
        return (out.decode("utf-8", "replace"), err.decode("utf-8", "replace"),
                proc.returncode, timed_out)

    # ----------------------------------------------------- interpretacion
    @staticmethod
    def _detect_trust_warning(result: RunResult, stderr: str, stdout: str) -> None:
        """Claude Code avisa (stderr o lineas previas al JSON) cuando ignora reglas del proyecto por falta de
        confianza en la carpeta. Solo se REGISTRA: no se toca la configuracion de confianza ni se debilita nada."""
        noise = "\n".join(ln for ln in stdout.splitlines() if not ln.lstrip().startswith("{"))
        for text in (stderr, noise):
            m = TRUST_RE.search(text or "")
            if m:
                line = next((ln.strip() for ln in text.splitlines() if TRUST_RE.search(ln)), m.group(0))
                result.trust_warning = True
                result.warnings.append("CONFIANZA: Claude Code ignoro reglas del proyecto porque la carpeta no esta "
                                       "confiada (%s). Las reglas permissions.allow no se aplican; las denegaciones "
                                       "y --tools si. Acepte el dialogo de confianza en una sesion interactiva si "
                                       "procede (el coordinador no lo hace)." % line[:240])
                return

    def _interpret(self, result: RunResult, stdout: str, rc, timed_out: bool, problems: list,
                   integrity_bad: bool = False) -> None:
        statuses = []
        data = None
        if timed_out:
            statuses.append(Status.TIMEOUT)
            problems.append("Tiempo maximo agotado; se detuvo el arbol de procesos.")
        else:
            try:
                data = parse_claude_json(stdout)
            except ValueError as e:
                statuses.append(Status.ERROR)
                problems.append("Salida no interpretable (%s)." % e)
            if rc != 0:
                statuses.append(Status.ERROR)
                problems.append("El CLI termino con codigo %s." % rc)
        if data is not None:
            result.result_text = data.get("result") if isinstance(data.get("result"), str) else ""
            result.session_id = data.get("session_id")
            result.num_turns = data.get("num_turns")
            result.terminal_reason = data.get("terminal_reason") if isinstance(data.get("terminal_reason"), str) else None
            result.stop_reason = data.get("stop_reason") if isinstance(data.get("stop_reason"), str) else None
            result.usage = data.get("usage") if isinstance(data.get("usage"), dict) else {}
            result.model_usage = data.get("modelUsage") if isinstance(data.get("modelUsage"), dict) else {}
            cost = data.get("total_cost_usd")
            result.cost_usd = float(cost) if isinstance(cost, (int, float)) else None
            subtype = str(data.get("subtype") or "")
            if data.get("is_error") or subtype.startswith("error"):
                statuses.append(Status.ERROR)
                problems.append("Claude Code informo de un error (%s)." % (subtype or "is_error"))
            raw = data.get("permission_denials")
            for d in raw if isinstance(raw, list) else []:
                if isinstance(d, dict):
                    result.denials.append({"tool_name": d.get("tool_name"), "tool_input": _short(d.get("tool_input", ""))})
                else:
                    result.denials.append({"tool_name": None, "tool_input": _short(d)})
            if result.denials:
                statuses.append(Status.DENIED)
                result.needs_authorization = True
        if integrity_bad:
            statuses.append(Status.INTEGRITY_FAILURE)
        if result.out_of_scope_files:
            statuses.append(Status.SCOPE_VIOLATION)
        for st in Status.severity_order():
            if st in statuses:
                result.status = st
                break
        else:
            result.status = Status.OK

    def _finish(self, result: RunResult, task: Task, problems: list, t0: float, pre, post) -> RunResult:
        result.problems = list(problems)
        result.duration_s = round(time.monotonic() - t0, 3)
        if not result.summary:
            result.summary = {
                Status.OK: "Completada sin incidencias.",
                Status.DENIED: "Claude Code necesito permisos que no estan concedidos. Ejecucion detenida: "
                               "revise las denegaciones y autorice a mano si procede.",
                Status.ERROR: "La ejecucion fallo: " + ("; ".join(problems) or "sin detalle"),
                Status.TIMEOUT: "Se agoto el tiempo maximo.",
                Status.SCOPE_VIOLATION: "Hay cambios fuera de las rutas autorizadas: revise y revierta a mano.",
                Status.INTEGRITY_FAILURE: "La integridad del hook o de las rutas protegidas no es la esperada.",
            }[result.status]
        try:
            self.logger.log_run({
                "run_id": result.run_id, "task_id": task.task_id, "label": task.label,
                "status": result.status.value, "needs_authorization": result.needs_authorization,
                "summary": result.summary, "problems": result.problems, "returncode": result.returncode,
                "timed_out": result.timed_out, "duration_s": result.duration_s, "cost_usd": result.cost_usd,
                "num_turns": result.num_turns, "session_id": result.session_id, "denials": result.denials,
                "changed_files": result.changed_files, "out_of_scope_files": result.out_of_scope_files,
                "protected_changes": result.protected_changes, "args": result.args, "tools": result.tools,
                "warnings": result.warnings, "trust_warning": result.trust_warning,
                "terminal_reason": result.terminal_reason, "stop_reason": result.stop_reason,
                "usage": result.usage, "model_usage": result.model_usage,
                "cwd": str(self.config.project_root), "agent": task.agent,
                "allowed_paths": list(task.allowed_paths), "prompt": self.logger.prompt_info(task.prompt),
                "integrity_before": {"ok": pre.ok, "guard_sha256": pre.guard_sha256} if pre else None,
                "integrity_after": {"ok": post.ok, "guard_sha256": post.guard_sha256} if post else None,
                "result_text": result.result_text, "stderr_tail": result.stderr_tail,
            })
        except Exception as e:      # un fallo de registro no debe ocultar el resultado
            result.problems.append("no se pudo escribir el registro: %s" % e)
        return result
