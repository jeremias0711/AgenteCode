"""GameDevAgent: nucleo del coordinador Python que habla con Claude Code (sin API de OpenAI).

Uso tipico (ver orchestrator/README.md):

    from orchestrator import Config, ClaudeRunner, Task
    runner = ClaudeRunner(Config.from_environment())
    result = runner.run(Task(prompt="...", allowed_paths=["Game/Assets/Scripts/**"]))
"""
from .claude_runner import ClaudeRunner, build_env, parse_claude_json, validate_args
from .config import Config, locate_claude
from .errors import (ConfigError, ForbiddenOptionError, IntegrityError, OrchestratorError, TaskError)
from .integrity import IntegrityChecker, IntegrityReport
from .logger import RunLogger
from .results import RunResult, Status
from .task import Task

__version__ = "0.1.0"
__all__ = [
    "ClaudeRunner", "Config", "ConfigError", "ForbiddenOptionError", "IntegrityChecker", "IntegrityError",
    "IntegrityReport", "OrchestratorError", "RunLogger", "RunResult", "Status", "Task", "TaskError",
    "build_env", "locate_claude", "parse_claude_json", "validate_args",
]
