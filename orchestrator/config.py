"""Configuracion del coordinador y localizacion del CLI de Claude Code.

REQUISITO: hace falta un CLI de Claude Code funcional (`claude`). El coordinador NO fija una ruta
concreta: la ruta de la extension de VS Code cambia con cada actualizacion y no es fiable. El
ejecutable se localiza, por este orden:

  1. El argumento explicito (`Config.from_environment(claude=...)`).
  2. La variable de entorno GAMEDEVAGENT_CLAUDE (ruta a `claude.exe`).
  3. `claude` en el PATH (`shutil.which`).

Si no se encuentra ninguno se lanza ConfigError con instrucciones. Para tener un CLI estable en el PATH
se puede ejecutar `claude install` (accion del usuario; el coordinador nunca instala nada).

Por seguridad se rechazan como ejecutable los scripts .bat/.cmd/.ps1/.vbs/.js: en Windows pasan por un
interprete de comandos con reglas de comillas que no se controlan.
"""
from __future__ import annotations

import os
import shutil
from dataclasses import dataclass, field
from pathlib import Path
from typing import Mapping, Optional, Sequence

from .errors import ConfigError

ENV_CLAUDE = "GAMEDEVAGENT_CLAUDE"
ENV_PROJECT = "GAMEDEVAGENT_PROJECT_DIR"
ENV_LOG_DIR = "GAMEDEVAGENT_LOG_DIR"
ENV_GUARD_SHA = "GAMEDEVAGENT_EXPECTED_GUARD_SHA256"
ENV_SETTINGS_SHA = "GAMEDEVAGENT_EXPECTED_SETTINGS_SHA256"

BLOCKED_EXECUTABLE_SUFFIXES = frozenset({".bat", ".cmd", ".ps1", ".vbs", ".js"})
DEFAULT_IGNORE_DIRS = frozenset({
    ".git", "Library", "Temp", "obj", "Logs", "UserSettings", ".vs", "__pycache__", ".venv", "node_modules",
})
# Variables de entorno que se pasan a Claude Code (lista BLANCA). Todo lo demas se descarta: no se heredan
# claves de otros servicios (p. ej. OpenAI) ni variables que cambien el comportamiento de Claude Code.
ENV_ALLOWLIST = (
    "PATH", "PATHEXT", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "USERPROFILE", "HOMEDRIVE",
    "HOMEPATH", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "PROGRAMFILES(X86)",
    "TEMP", "TMP", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "HOME", "LANG", "LC_ALL", "TERM",
    "ANTHROPIC_API_KEY",
)
# Nunca se pasan, ni siquiera pidiendolo en env_passthrough.
ENV_NEVER = ("CLAUDE_CODE_SIMPLE",)       # equivale a --bare: desactivaria los hooks
ENV_NEVER_PREFIXES = ("OPENAI_",)


def _check_executable(command: Sequence[str]) -> tuple[str, ...]:
    if not command or not all(isinstance(c, str) and c for c in command):
        raise ConfigError("claude_command debe ser una lista no vacia de cadenas.")
    exe = Path(command[0])
    if exe.suffix.lower() in BLOCKED_EXECUTABLE_SUFFIXES:
        raise ConfigError("El ejecutable de Claude Code no puede ser un script (%s): use claude.exe." % exe.suffix)
    if not exe.is_file():
        raise ConfigError("No existe el ejecutable de Claude Code: %s" % exe)
    return tuple(command)


def locate_claude(explicit: "str | Sequence[str] | None" = None,
                  env: Optional[Mapping[str, str]] = None,
                  which=shutil.which) -> tuple[str, ...]:
    """Devuelve el comando (tupla) para lanzar Claude Code. Ver el orden de busqueda en el docstring del modulo."""
    env = os.environ if env is None else env
    if explicit:
        cmd = (explicit,) if isinstance(explicit, str) else tuple(explicit)
        return _check_executable(cmd)
    if env.get(ENV_CLAUDE):
        return _check_executable((env[ENV_CLAUDE],))
    found = which("claude")
    if found:
        return _check_executable((found,))
    raise ConfigError(
        "No se encontro el CLI de Claude Code. Opciones: (1) definir %s con la ruta de claude.exe, "
        "(2) tener `claude` en el PATH (p. ej. con `claude install`), (3) pasar claude=... a Config. "
        "El coordinador no usa rutas de la extension de VS Code porque cambian con cada version." % ENV_CLAUDE)


@dataclass(frozen=True)
class Config:
    project_root: Path
    claude_command: tuple[str, ...]
    log_dir: Path
    expected_guard_sha256: Optional[str] = None      # pin FUERA de settings.json (opcional pero recomendado)
    expected_settings_sha256: Optional[str] = None
    require_pinned_hash: bool = True                 # settings.json debe fijar el SHA-256 de guard.py
    default_model: Optional[str] = None
    default_max_turns: int = 20
    default_timeout_s: int = 900
    default_max_budget_usd: float = 1.0
    env_passthrough: tuple[str, ...] = ()
    ignore_dirs: frozenset = DEFAULT_IGNORE_DIRS
    log_prompt_preview_chars: int = 200
    extra: dict = field(default_factory=dict)

    def __post_init__(self):
        root = Path(self.project_root)
        if not root.is_dir():
            raise ConfigError("project_root no es un directorio: %s" % root)
        object.__setattr__(self, "project_root", root.resolve())
        object.__setattr__(self, "log_dir", Path(self.log_dir).resolve())
        object.__setattr__(self, "claude_command", _check_executable(self.claude_command))
        for name, lo, hi in (("default_max_turns", 1, 100), ("default_timeout_s", 1, 7200)):
            v = getattr(self, name)
            if not isinstance(v, int) or isinstance(v, bool) or not lo <= v <= hi:
                raise ConfigError("%s debe ser un entero entre %d y %d." % (name, lo, hi))
        b = self.default_max_budget_usd
        if isinstance(b, bool) or not isinstance(b, (int, float)) or not 0 < b <= 50:
            raise ConfigError("default_max_budget_usd debe estar entre 0 (excluido) y 50.")
        for n in self.env_passthrough:
            if n.upper() in ENV_NEVER or n.upper().startswith(ENV_NEVER_PREFIXES):
                raise ConfigError("La variable de entorno %s no puede pasarse a Claude Code." % n)
        for v in (self.expected_guard_sha256, self.expected_settings_sha256):
            if v is not None and (len(v) != 64 or any(c not in "0123456789abcdefABCDEF" for c in v)):
                raise ConfigError("Los hashes esperados deben ser SHA-256 hexadecimales de 64 caracteres.")

    @classmethod
    def from_environment(cls, project_root: "str | Path | None" = None,
                         claude: "str | Sequence[str] | None" = None,
                         env: Optional[Mapping[str, str]] = None, **overrides) -> "Config":
        env = os.environ if env is None else env
        root = project_root or env.get(ENV_PROJECT) or Path(__file__).resolve().parent.parent
        root = Path(root)
        log_dir = overrides.pop("log_dir", None) or env.get(ENV_LOG_DIR) or root / "orchestrator" / "runs"
        overrides.setdefault("expected_guard_sha256", env.get(ENV_GUARD_SHA) or None)
        overrides.setdefault("expected_settings_sha256", env.get(ENV_SETTINGS_SHA) or None)
        return cls(project_root=root, claude_command=locate_claude(claude, env), log_dir=Path(log_dir), **overrides)
