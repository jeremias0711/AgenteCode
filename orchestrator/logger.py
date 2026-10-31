"""Registro de ejecuciones en JSON Lines (un archivo por dia: runs-YYYYMMDD.jsonl).

Cada ejecucion produce UN registro, tambien cuando falla antes de lanzar Claude Code (p. ej. integridad).
Por defecto NO se guarda el prompt completo: solo su SHA-256, su longitud y un extracto (configurable
con Config.log_prompt_preview_chars; 0 = sin extracto). Nunca se registra el entorno ni claves.

El directorio de registros contiene un `.gitignore` propio (`*`) que se crea automaticamente para que los
registros no acaben en Git por accidente.
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
import threading
from pathlib import Path

SCHEMA_VERSION = 1
MAX_FIELD_CHARS = 20_000


def _clip(value, limit=MAX_FIELD_CHARS):
    if isinstance(value, str) and len(value) > limit:
        return value[:limit] + "...[recortado %d caracteres]" % (len(value) - limit)
    return value


class RunLogger:
    def __init__(self, log_dir: Path, prompt_preview_chars: int = 200):
        self.log_dir = Path(log_dir)
        self.preview = max(0, int(prompt_preview_chars))
        self._lock = threading.Lock()

    def _ensure_dir(self) -> None:
        self.log_dir.mkdir(parents=True, exist_ok=True)
        ignore = self.log_dir / ".gitignore"
        if not ignore.exists():
            ignore.write_text("*\n!.gitignore\n", encoding="utf-8")

    def path_for(self, when: _dt.datetime) -> Path:
        return self.log_dir / ("runs-%s.jsonl" % when.strftime("%Y%m%d"))

    def prompt_info(self, prompt: str) -> dict:
        info = {"sha256": hashlib.sha256(prompt.encode("utf-8")).hexdigest(), "chars": len(prompt)}
        if self.preview:
            info["preview"] = prompt[:self.preview]
        return info

    def log_run(self, record: dict) -> Path:
        """Anade un registro. Devuelve el archivo en el que se escribio."""
        now = _dt.datetime.now(_dt.timezone.utc)
        clean = {"schema": SCHEMA_VERSION, "logged_at": now.isoformat()}
        for k, v in record.items():
            clean[k] = _clip(v) if isinstance(v, str) else v
        line = json.dumps(clean, ensure_ascii=False, default=str)
        with self._lock:
            self._ensure_dir()
            path = self.path_for(now)
            with open(path, "a", encoding="utf-8", newline="\n") as f:
                f.write(line + "\n")
        return path

    @staticmethod
    def read_records(path: Path) -> list:
        out = []
        for ln in Path(path).read_text(encoding="utf-8").splitlines():
            if ln.strip():
                out.append(json.loads(ln))
        return out
