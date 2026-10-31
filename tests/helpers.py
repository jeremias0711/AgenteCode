"""Utilidades de prueba: proyecto ficticio, configuracion con el Claude ficticio y escenarios."""
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from orchestrator import ClaudeRunner, Config

FAKE = str(Path(__file__).resolve().parent / "fake_claude.py")
MATCHER = "Bash|PowerShell|Write|Edit|MultiEdit|NotebookEdit"
GUARD_TEXT = "# guard.py de prueba\nprint('hook de prueba')\n"


def sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def settings_json(guard_sha: str, pinned: bool = True, on_failure="block", bypass="disable", matcher=MATCHER) -> dict:
    code = ("import hashlib,runpy,sys,os;p=sys.argv[1];d=open(p,'rb').read();"
            "sys.exit(2) if hashlib.sha256(d).hexdigest()!='%s' else runpy.run_path(p,run_name='__main__')" % guard_sha)
    args = ["-I", "-B", "-c", code, "${CLAUDE_PROJECT_DIR}/.claude/hooks/guard.py"] if pinned else \
        ["-I", "-B", "${CLAUDE_PROJECT_DIR}/.claude/hooks/guard.py"]
    hook = {"type": "command", "command": "python", "args": args, "timeout": 10}
    if on_failure:
        hook["onFailure"] = on_failure
    perms = {"defaultMode": "acceptEdits"}
    if bypass:
        perms["disableBypassPermissionsMode"] = bypass
    return {"permissions": perms, "hooks": {"PreToolUse": [{"matcher": matcher, "hooks": [hook]}]}}


def make_project(root: Path, **settings_kw) -> Path:
    for d in (".claude/hooks", "templates/originals", "Game/Assets/Scripts", "tests", "orchestrator"):
        (root / d).mkdir(parents=True, exist_ok=True)
    # write_bytes: write_text convertiria \n en \r\n en Windows y el hash no coincidiria con el fijado en settings
    (root / ".claude" / "hooks" / "guard.py").write_bytes(GUARD_TEXT.encode("utf-8"))
    (root / ".claude" / "hooks" / "protected_paths.local.txt").write_text("C:\\Backups\\\n", encoding="utf-8")
    (root / ".claude" / "settings.json").write_text(json.dumps(settings_json(sha(GUARD_TEXT), **settings_kw)), encoding="utf-8")
    (root / ".claude" / "settings.local.json").write_text("{}", encoding="utf-8")
    (root / "templates" / "originals" / "a.txt").write_text("original\n", encoding="utf-8")
    (root / "Game" / "Assets" / "Scripts" / "Existing.cs").write_text("class Existing {}\n", encoding="utf-8")
    (root / "CLAUDE.md").write_text("# prueba\n", encoding="utf-8")
    return root


def pid_alive(pid: int) -> bool:
    if os.name == "nt":
        out = subprocess.run(["tasklist", "/FI", "PID eq %d" % pid], capture_output=True, text=True).stdout
        return str(pid) in out
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


class ProjectCase(unittest.TestCase):
    """Cada prueba tiene su propio proyecto ficticio en un directorio temporal."""

    settings_kw: dict = {}

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory(prefix="gda_test_")
        self.addCleanup(self._tmp.cleanup)
        self.root = make_project(Path(self._tmp.name) / "proj", **self.settings_kw)
        self.scen_dir = Path(self._tmp.name) / "scen"
        self.scen_dir.mkdir()

    def scenario(self, **scen) -> Path:
        p = self.scen_dir / ("s%d.json" % len(list(self.scen_dir.iterdir())))
        p.write_text(json.dumps(scen), encoding="utf-8")
        return p

    def config(self, scen=None, **kw) -> Config:
        scen = scen if scen is not None else self.scenario()
        kw.setdefault("log_dir", self.root / "orchestrator" / "runs")
        return Config(project_root=self.root, claude_command=(sys.executable, "-I", FAKE, str(scen)), **kw)

    def runner(self, **scen) -> ClaudeRunner:
        return ClaudeRunner(self.config(self.scenario(**scen)))
