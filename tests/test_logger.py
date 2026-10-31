import datetime as dt
import json
import tempfile
import threading
import unittest
from pathlib import Path

from orchestrator import RunLogger


class LoggerTests(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = Path(self._tmp.name) / "logs"

    def test_creates_dir_gitignore_and_jsonl(self):
        lg = RunLogger(self.dir)
        path = lg.log_run({"run_id": "r1", "texto": "ñ é"})
        self.assertEqual((self.dir / ".gitignore").read_text(encoding="utf-8"), "*\n!.gitignore\n")
        self.assertRegex(path.name, r"^runs-\d{8}\.jsonl$")
        rec = json.loads(path.read_text(encoding="utf-8").strip())
        self.assertEqual((rec["schema"], rec["run_id"], rec["texto"]), (1, "r1", "ñ é"))
        self.assertIn("logged_at", rec)

    def test_appends_one_line_per_record(self):
        lg = RunLogger(self.dir)
        p = lg.log_run({"n": 1})
        lg.log_run({"n": 2})
        self.assertEqual([r["n"] for r in RunLogger.read_records(p)], [1, 2])

    def test_long_strings_are_clipped(self):
        p = RunLogger(self.dir).log_run({"salida": "a" * 50_000})
        v = RunLogger.read_records(p)[0]["salida"]
        self.assertLess(len(v), 21_000)
        self.assertIn("recortado", v)

    def test_prompt_info_never_contains_full_prompt(self):
        info = RunLogger(self.dir, 10).prompt_info("0123456789ABCDEF")
        self.assertEqual((info["preview"], info["chars"]), ("0123456789", 16))
        self.assertEqual(len(info["sha256"]), 64)
        self.assertNotIn("preview", RunLogger(self.dir, 0).prompt_info("secreto"))

    def test_non_serializable_values_do_not_crash(self):
        p = RunLogger(self.dir).log_run({"ruta": Path("x"), "fecha": dt.date(2026, 1, 1)})
        self.assertEqual(RunLogger.read_records(p)[0]["ruta"], "x")

    def test_threads_do_not_interleave_lines(self):
        lg = RunLogger(self.dir)
        ts = [threading.Thread(target=lambda i=i: [lg.log_run({"t": i, "k": k}) for k in range(20)]) for i in range(5)]
        [t.start() for t in ts]
        [t.join() for t in ts]
        self.assertEqual(len(RunLogger.read_records(lg.path_for(dt.datetime.now(dt.timezone.utc)))), 100)


if __name__ == "__main__":
    unittest.main()
