"""Integridad del hook de seguridad, de las rutas protegidas y del alcance de escritura de una tarea.

Tres piezas independientes:

* IntegrityChecker.verify(): comprueba que el hook sigue siendo el esperado (settings.json registra el
  hook con onFailure=block, el SHA-256 de guard.py coincide con el fijado en settings.json y, si se
  configuran, con hashes esperados guardados FUERA del proyecto).
* IntegrityChecker.snapshot_protected(): huella de .claude/settings*.json, .claude/hooks/** y
  templates/originals/** para detectar cualquier cambio durante una ejecucion.
* snapshot_tree()/diff_snapshots()/out_of_scope(): huella de todo el proyecto antes y despues de una
  tarea, para detectar cambios fuera de las rutas autorizadas.

Limite honesto: la huella de un archivo es (tamano, mtime_ns y, si pesa <= 1 MiB, SHA-256). Un cambio
que conserve tamano y mtime en un archivo de mas de 1 MiB no se detectaria. Las ejecuciones pueden
tardar en proyectos con miles de archivos: los directorios de `ignore_dirs` (Library, Temp, .git...) no se recorren.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Optional

from .task import is_forbidden_write

HASHED_UP_TO = 1024 * 1024
_PIN_RE = re.compile(r"!=\s*'([0-9a-fA-F]{64})'")
REQUIRED_MATCHER_TOOLS = ("Bash", "PowerShell", "Write", "Edit")


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _fingerprint(path: str) -> tuple:
    st = os.stat(path)
    digest = sha256_file(Path(path)) if st.st_size <= HASHED_UP_TO else None
    return (st.st_size, st.st_mtime_ns, digest)


def snapshot_tree(root: Path, ignore_dirs: Iterable[str] = (), ignore_paths: Iterable[Path] = ()) -> dict:
    """{ruta relativa con '/': huella} de todos los archivos bajo root."""
    root = Path(root)
    ignore_names = {d.lower() for d in ignore_dirs}
    ignore_abs = {os.path.normcase(os.path.abspath(p)) for p in ignore_paths}
    out: dict = {}
    for dirpath, dirnames, filenames in os.walk(root, followlinks=False):
        dirnames[:] = [d for d in dirnames
                       if d.lower() not in ignore_names
                       and os.path.normcase(os.path.abspath(os.path.join(dirpath, d))) not in ignore_abs]
        for name in filenames:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, root).replace("\\", "/")
            try:
                out[rel] = _fingerprint(full)
            except OSError as e:          # archivo que desaparece o bloqueado durante el recorrido
                out[rel] = ("error", type(e).__name__)
    return out


def diff_snapshots(before: dict, after: dict) -> dict:
    added = sorted(set(after) - set(before))
    removed = sorted(set(before) - set(after))
    modified = sorted(k for k in set(before) & set(after) if before[k] != after[k])
    return {"added": added, "removed": removed, "modified": modified}


def changed_paths(diff: dict) -> list:
    return sorted(set(diff["added"]) | set(diff["removed"]) | set(diff["modified"]))


def _glob_to_regex(pattern: str) -> "re.Pattern[str]":
    """'**' = cualquier profundidad (incluido nada), '*' y '?' no cruzan '/'. Sin distinguir mayusculas."""
    i, out, n = 0, [], len(pattern)
    while i < n:
        c = pattern[i]
        if c == "*":
            if pattern[i:i + 2] == "**":
                i += 2
                if pattern[i:i + 1] == "/":
                    out.append("(?:.*/)?")
                    i += 1
                else:
                    out.append(".*")
                continue
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        elif c == "[":
            j = pattern.find("]", i + 1)
            if j == -1:
                out.append(re.escape(c))
            else:
                out.append("[" + pattern[i + 1:j].replace("\\", "\\\\") + "]")
                i = j
        else:
            out.append(re.escape(c))
        i += 1
    return re.compile("^" + "".join(out) + "$", re.IGNORECASE)


def in_scope(relpath: str, patterns: Iterable[str]) -> bool:
    """True si la ruta relativa encaja en algun patron Y no cae en una zona siempre protegida."""
    if is_forbidden_write(relpath):
        return False
    return any(_glob_to_regex(p).match(relpath) for p in patterns)


def out_of_scope(changed: Iterable[str], patterns: Iterable[str]) -> list:
    pats = list(patterns)
    return sorted(c for c in changed if not in_scope(c, pats))


@dataclass
class IntegrityReport:
    ok: bool
    problems: list = field(default_factory=list)
    guard_sha256: Optional[str] = None
    settings_sha256: Optional[str] = None


class IntegrityChecker:
    def __init__(self, project_root: Path, expected_guard_sha256: Optional[str] = None,
                 expected_settings_sha256: Optional[str] = None, require_pinned_hash: bool = True,
                 ignore_dirs: Iterable[str] = ()):
        self.root = Path(project_root)
        self.expected_guard = expected_guard_sha256.lower() if expected_guard_sha256 else None
        self.expected_settings = expected_settings_sha256.lower() if expected_settings_sha256 else None
        self.require_pin = require_pinned_hash
        self.ignore_dirs = tuple(ignore_dirs)
        self.settings_path = self.root / ".claude" / "settings.json"
        self.guard_path = self.root / ".claude" / "hooks" / "guard.py"

    # ---------------------------------------------------------------- hook
    def verify(self) -> IntegrityReport:
        problems: list = []
        guard_sha = settings_sha = None
        if not self.settings_path.is_file():
            return IntegrityReport(False, ["Falta .claude/settings.json"])
        settings_sha = sha256_file(self.settings_path)
        try:
            settings = json.loads(self.settings_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            return IntegrityReport(False, ["settings.json no es JSON valido: %s" % e], None, settings_sha)
        if not self.guard_path.is_file():
            problems.append("Falta .claude/hooks/guard.py")
        else:
            guard_sha = sha256_file(self.guard_path)

        perms = settings.get("permissions") if isinstance(settings, dict) else None
        if not isinstance(perms, dict) or perms.get("disableBypassPermissionsMode") != "disable":
            problems.append("settings.json no desactiva el modo bypassPermissions")
        hook = self._find_hook(settings)
        if hook is None:
            problems.append("settings.json no registra el hook PreToolUse de guard.py para Bash/PowerShell/Write/Edit")
        else:
            if hook.get("onFailure") != "block":
                problems.append("El hook no tiene onFailure=block")
            pins = []
            for a in hook.get("args") or []:
                if isinstance(a, str):
                    pins += _PIN_RE.findall(a)
            if len(pins) > 1:
                problems.append("settings.json fija mas de un SHA-256 para guard.py")
            elif pins:
                if guard_sha and pins[0].lower() != guard_sha:
                    problems.append("El SHA-256 fijado en settings.json no coincide con guard.py (%s...)" % guard_sha[:12])
            elif self.require_pin:
                problems.append("settings.json no fija el SHA-256 de guard.py (verificador externo ausente)")
        if self.expected_guard and guard_sha and guard_sha != self.expected_guard:
            problems.append("guard.py no coincide con el SHA-256 esperado (configuracion externa)")
        if self.expected_settings and settings_sha != self.expected_settings:
            problems.append("settings.json no coincide con el SHA-256 esperado (configuracion externa)")
        return IntegrityReport(not problems, problems, guard_sha, settings_sha)

    @staticmethod
    def _find_hook(settings) -> Optional[dict]:
        hooks = settings.get("hooks") if isinstance(settings, dict) else None
        entries = hooks.get("PreToolUse") if isinstance(hooks, dict) else None
        for entry in entries if isinstance(entries, list) else []:
            matcher = entry.get("matcher", "") if isinstance(entry, dict) else ""
            if not all(t in matcher for t in REQUIRED_MATCHER_TOOLS):
                continue
            for h in entry.get("hooks") or []:
                if isinstance(h, dict) and h.get("type") == "command" and "guard.py" in " ".join(
                        str(a) for a in (h.get("args") or [])) + str(h.get("command", "")):
                    return h
        return None

    # ---------------------------------------------------------- protegidas
    def snapshot_protected(self) -> dict:
        """Huella de los archivos de seguridad (SHA-256) y de templates/originals (huella ligera)."""
        snap: dict = {}
        claude = self.root / ".claude"
        for name in ("settings.json", "settings.local.json"):
            p = claude / name
            snap[".claude/" + name] = sha256_file(p) if p.is_file() else None
        hooks = claude / "hooks"
        if hooks.is_dir():
            for dp, _dn, fn in os.walk(hooks):
                for f in fn:
                    full = Path(dp) / f
                    snap[full.relative_to(self.root).as_posix()] = sha256_file(full)
        orig = self.root / "templates" / "originals"
        if orig.is_dir():
            for rel, fp in snapshot_tree(orig).items():
                snap["templates/originals/" + rel] = fp
        return snap

    def protected_changes(self, before: dict) -> list:
        d = diff_snapshots(before, self.snapshot_protected())
        return changed_paths(d)
