"""Excepciones del coordinador.

Se lanzan para errores de PROGRAMACION o de CONFIGURACION (algo que el llamador debe corregir).
Las situaciones esperadas durante una ejecucion (tiempo agotado, permisos denegados, cambios fuera
de alcance...) NO son excepciones: ClaudeRunner.run() las devuelve como un RunResult con su estado.
"""


class OrchestratorError(Exception):
    """Base de todas las excepciones del coordinador."""


class ConfigError(OrchestratorError):
    """La configuracion es invalida o falta algo imprescindible (p. ej. el CLI de Claude Code)."""


class TaskError(OrchestratorError):
    """La tarea es invalida o pide algo que el coordinador no permite."""


class ForbiddenOptionError(OrchestratorError):
    """Se intento construir o ejecutar un comando con una opcion no permitida."""


class IntegrityError(OrchestratorError):
    """La comprobacion de integridad del hook o de las rutas protegidas ha fallado."""
