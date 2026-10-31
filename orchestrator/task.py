"""Objeto de tarea y su validacion.

Una Task describe QUE se le pide a Claude Code y CON QUE ALCANCE. Todo lo que no se pide de forma
explicita queda denegado: no se aceptan herramientas, rutas ni opciones implicitas.

* `allowed_tools`: solo herramientas de LECTURA (Read, Grep, Glob) y un conjunto cerrado de patrones de shell
  (git de solo lectura, dotnet build/test, unittest). Las herramientas de escritura NO se piden aqui: se
  derivan de `allowed_paths`.
* `allowed_paths`: unicas rutas (relativas al proyecto) donde la tarea puede escribir. Vacio = tarea de
  solo lectura. Se rechazan rutas absolutas, `..`, comodines al principio y cualquier ruta que toque las
  zonas siempre protegidas (.claude, .git, templates/originals, CLAUDE.md, ...).
* Los limites (turnos, tiempo, presupuesto) siempre tienen un valor acotado.
"""
from __future__ import annotations

import posixpath
import re
import uuid
from dataclasses import dataclass, field
from typing import Optional

from .errors import TaskError

KNOWN_AGENTS = frozenset({
    "unity-game-developer", "unity6-migration-specialist", "code-reviewer", "blender-unity-asset-specialist",
})
READ_TOOLS = frozenset({"Read", "Grep", "Glob"})
_SHELL_BASE = (
    "git status*", "git diff*", "git log*", "git show*", "git ls-files*", "git rev-parse*",
    "dotnet build*", "dotnet test*", "python -m unittest*",
)
ALLOWED_SHELL_TOOLS = frozenset("%s(%s)" % (t, p) for t in ("Bash", "PowerShell") for p in _SHELL_BASE)
ALLOWED_TOOLS = READ_TOOLS | ALLOWED_SHELL_TOOLS

# Se aplican SIEMPRE (--disallowedTools) y una tarea no puede quitarlas.
_DENIED_SHELL = ("git commit*", "git push*", "git merge*", "git pull*", "git rebase*", "git reset*",
                 "git clean*", "git cherry-pick*", "git revert*", "git tag*", "git branch -D*",
                 "gh *", "pip install*", "pip3 install*", "python -m pip*", "npm install*", "npm i *",
                 "winget *", "choco *", "scoop *", "dotnet add package*", "dotnet tool install*")
ALWAYS_DISALLOWED = tuple(
    ["WebFetch", "WebSearch"] + ["%s(%s)" % (t, p) for t in ("Bash", "PowerShell") for p in _DENIED_SHELL])

PERMISSION_MODES = frozenset({"dontAsk", "plan", "acceptEdits"})

# Rutas donde NINGUNA tarea puede escribir, ni siquiera si una ruta permitida las contuviera.
FORBIDDEN_WRITE_DIRS = (".claude", ".git", "templates/originals")
FORBIDDEN_WRITE_FILES = ("CLAUDE.md", ".gitignore", ".gitattributes", "Game/Packages/manifest.json",
                         "Game/Packages/packages-lock.json", "Game/ProjectSettings/ProjectVersion.txt")

MAX_PROMPT_CHARS = 200_000
MIN_JUSTIFICATION_CHARS = 20
# Unicos nombres de herramientas integradas que pueden aparecer en --tools.
TOOL_NAMES = ("Read", "Grep", "Glob", "Edit", "Write", "Bash", "PowerShell")
_WILD = re.compile(r"[*?\[]")
_AGENT_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,63}$")


def normalize_scope_pattern(pattern: str) -> str:
    """Valida y normaliza un patron de escritura (separadores '/')."""
    if not isinstance(pattern, str) or not pattern.strip():
        raise TaskError("allowed_paths contiene una ruta vacia.")
    p = pattern.strip().replace("\\", "/")
    if p.startswith("/") or re.match(r"^[A-Za-z]:", p) or p.startswith("~"):
        raise TaskError("allowed_paths solo admite rutas relativas al proyecto: %r" % pattern)
    if "\x00" in p or any(ord(c) < 32 for c in p):
        raise TaskError("allowed_paths contiene caracteres de control: %r" % pattern)
    parts = [x for x in p.split("/") if x not in ("", ".")]
    if ".." in parts:
        raise TaskError("allowed_paths no admite '..': %r" % pattern)
    if not parts:
        raise TaskError("allowed_paths no puede ser la raiz del proyecto: %r" % pattern)
    if _WILD.search(parts[0]):
        raise TaskError("allowed_paths debe empezar por un directorio concreto, no por un comodin: %r" % pattern)
    norm = "/".join(parts)
    literal = []
    for seg in parts:
        if _WILD.search(seg):
            break
        literal.append(seg)
    lit = "/".join(literal).lower()
    zones = list(FORBIDDEN_WRITE_DIRS) + [f for f in FORBIDDEN_WRITE_FILES]
    for z in zones:
        zl = z.lower()
        # la ruta literal esta dentro de la zona, es la zona o es un ancestro suyo
        if lit == zl or lit.startswith(zl + "/") or zl.startswith(lit + "/"):
            raise TaskError("allowed_paths toca una zona protegida (%s): %r" % (z, pattern))
    return norm


def is_forbidden_write(relpath: str) -> bool:
    """True si la ruta relativa cae en una zona donde ninguna tarea puede escribir."""
    r = posixpath.normpath(relpath.replace("\\", "/")).lower()
    if any(r == d.lower() or r.startswith(d.lower() + "/") for d in FORBIDDEN_WRITE_DIRS):
        return True
    return r in {f.lower() for f in FORBIDDEN_WRITE_FILES}


@dataclass(frozen=True)
class Task:
    prompt: str
    allowed_paths: tuple = ()
    allowed_tools: tuple = ("Read", "Grep", "Glob")
    agent: Optional[str] = None
    model: Optional[str] = None
    max_turns: Optional[int] = None
    timeout_s: Optional[int] = None
    max_budget_usd: Optional[float] = None
    permission_mode: str = "dontAsk"
    json_schema: Optional[dict] = None
    label: str = ""
    task_id: str = field(default_factory=lambda: uuid.uuid4().hex[:12])
    allow_write_tool: bool = False          # habilita la herramienta Write (ademas de Edit): requiere justificacion
    write_justification: str = ""

    def __post_init__(self):
        if not isinstance(self.prompt, str) or not self.prompt.strip():
            raise TaskError("El prompt no puede estar vacio.")
        if len(self.prompt) > MAX_PROMPT_CHARS or "\x00" in self.prompt:
            raise TaskError("Prompt demasiado largo o con caracteres NUL.")
        paths = tuple(dict.fromkeys(normalize_scope_pattern(p) for p in self.allowed_paths))
        object.__setattr__(self, "allowed_paths", paths)
        tools = tuple(dict.fromkeys(self.allowed_tools))
        for t in tools:
            if t not in ALLOWED_TOOLS:
                raise TaskError("Herramienta no permitida: %r. Las herramientas de escritura se derivan de "
                                "allowed_paths y no se aceptan herramientas implicitas." % (t,))
        object.__setattr__(self, "allowed_tools", tools)
        if self.agent is not None and (not _AGENT_RE.match(self.agent) or self.agent not in KNOWN_AGENTS):
            raise TaskError("Agente desconocido: %r (permitidos: %s)." % (self.agent, ", ".join(sorted(KNOWN_AGENTS))))
        if self.permission_mode not in PERMISSION_MODES:
            raise TaskError("permission_mode no permitido: %r (admitidos: %s)." % (
                self.permission_mode, ", ".join(sorted(PERMISSION_MODES))))
        if self.permission_mode == "plan" and paths:
            raise TaskError("El modo 'plan' es de solo lectura: no combine permission_mode='plan' con allowed_paths.")
        if paths and self.permission_mode != "dontAsk":
            # Verificado con Claude Code real (Fase 2.2): con acceptEdits las reglas Edit(/ruta) NO acotan la
            # escritura (se escribio fuera del alcance); solo dontAsk deniega lo que no esta permitido.
            raise TaskError("Las tareas con escritura (allowed_paths) exigen permission_mode='dontAsk'; %r no acota la "
                            "escritura a las rutas autorizadas." % (self.permission_mode,))
        if self.model is not None and not re.match(r"^[A-Za-z0-9][A-Za-z0-9._\[\]-]{0,63}$", self.model):
            raise TaskError("Nombre de modelo no valido: %r" % (self.model,))
        if self.max_turns is not None and (isinstance(self.max_turns, bool) or not isinstance(self.max_turns, int)
                                           or not 1 <= self.max_turns <= 100):
            raise TaskError("max_turns debe ser un entero entre 1 y 100.")
        if self.timeout_s is not None and (isinstance(self.timeout_s, bool) or not isinstance(self.timeout_s, int)
                                           or not 1 <= self.timeout_s <= 7200):
            raise TaskError("timeout_s debe ser un entero entre 1 y 7200.")
        if self.max_budget_usd is not None and (isinstance(self.max_budget_usd, bool)
                                                or not isinstance(self.max_budget_usd, (int, float))
                                                or not 0 < self.max_budget_usd <= 50):
            raise TaskError("max_budget_usd debe estar entre 0 (excluido) y 50.")
        if self.json_schema is not None and not isinstance(self.json_schema, dict):
            raise TaskError("json_schema debe ser un diccionario.")
        if not isinstance(self.allow_write_tool, bool):
            raise TaskError("allow_write_tool debe ser un booleano.")
        if self.allow_write_tool:
            if not paths:
                raise TaskError("La herramienta Write solo puede habilitarse junto a allowed_paths validas.")
            if not isinstance(self.write_justification, str) or len(self.write_justification.strip()) < MIN_JUSTIFICATION_CHARS:
                raise TaskError("Habilitar Write requiere una justificacion explicita (al menos %d caracteres)."
                                % MIN_JUSTIFICATION_CHARS)
        if not self.tools():
            raise TaskError("La tarea no habilita ninguna herramienta.")

    @property
    def read_only(self) -> bool:
        return not self.allowed_paths

    def tools(self) -> tuple:
        """Conjunto EXACTO de herramientas integradas que recibira Claude Code (--tools). Minimo privilegio:

        * Read/Grep/Glob: solo las declaradas en allowed_tools.
        * Edit: unicamente si hay allowed_paths validas.
        * Write: unicamente si ademas allow_write_tool=True con justificacion.
        * Bash/PowerShell: unicamente si allowed_tools declara al menos un patron de ese shell.
        Nada se habilita por aparecer en argumentos externos: solo se deriva de campos ya validados.
        """
        out = [t for t in ("Read", "Grep", "Glob") if t in self.allowed_tools]
        if self.allowed_paths:
            out.append("Edit")
            if self.allow_write_tool:
                out.append("Write")
        for shell in ("Bash", "PowerShell"):
            if any(t.startswith(shell + "(") for t in self.allowed_tools):
                out.append(shell)
        return tuple(out)

    def permission_rules(self) -> tuple:
        """Reglas para --allowedTools: lecturas declaradas, patrones de shell declarados y Edit(/ruta)."""
        rules = [t for t in self.allowed_tools if t in READ_TOOLS or "(" in t]
        return tuple(rules) + self.edit_rules()

    def edit_rules(self) -> tuple:
        """Reglas Edit(...) derivadas de allowed_paths (sintaxis de permisos de Claude Code)."""
        return tuple("Edit(/%s)" % p for p in self.allowed_paths)
