"""Resultado de una ejecucion de Claude Code."""
from __future__ import annotations

import enum
from dataclasses import asdict, dataclass, field
from typing import Optional


class Status(str, enum.Enum):
    OK = "ok"                              # termino bien, sin denegaciones y dentro del alcance
    DENIED = "denied"                      # Claude necesito permisos no concedidos: hay que decidir a mano
    ERROR = "error"                        # fallo del CLI, salida invalida, limite de turnos o de presupuesto
    TIMEOUT = "timeout"                    # se agoto el tiempo y se mato el arbol de procesos
    SCOPE_VIOLATION = "scope_violation"    # hay cambios fuera de las rutas autorizadas
    INTEGRITY_FAILURE = "integrity_failure"  # el hook o las rutas protegidas no estan integros
    # Orden de gravedad (mayor primero) usado para resolver varios problemas a la vez.

    @classmethod
    def severity_order(cls) -> list:
        return [cls.INTEGRITY_FAILURE, cls.SCOPE_VIOLATION, cls.TIMEOUT, cls.ERROR, cls.DENIED, cls.OK]


@dataclass
class RunResult:
    run_id: str
    task_id: str
    status: Status
    summary: str = ""                         # mensaje legible para el humano / el coordinador
    result_text: str = ""                      # campo `result` devuelto por Claude Code
    needs_authorization: bool = False          # True: detenerse y comunicarlo; el coordinador no reintenta
    denials: list = field(default_factory=list)
    problems: list = field(default_factory=list)
    changed_files: list = field(default_factory=list)
    out_of_scope_files: list = field(default_factory=list)
    protected_changes: list = field(default_factory=list)
    returncode: Optional[int] = None
    timed_out: bool = False
    session_id: Optional[str] = None
    num_turns: Optional[int] = None
    cost_usd: Optional[float] = None
    duration_s: float = 0.0
    stderr_tail: str = ""
    args: list = field(default_factory=list)
    tools: list = field(default_factory=list)        # valor exacto de --tools que recibio Claude Code
    warnings: list = field(default_factory=list)     # avisos que no cambian el estado (p. ej. falta de confianza)
    trust_warning: bool = False                      # Claude Code ignoro reglas del proyecto por falta de confianza
    terminal_reason: Optional[str] = None            # completed, max_turns, ...
    stop_reason: Optional[str] = None
    usage: dict = field(default_factory=dict)
    model_usage: dict = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return self.status is Status.OK

    def to_dict(self) -> dict:
        d = asdict(self)
        d["status"] = self.status.value
        return d
