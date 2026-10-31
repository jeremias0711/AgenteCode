"""Ejecutable FICTICIO de Claude Code para las pruebas. NO hace ninguna llamada de red ni usa ningun modelo.

Uso: python fake_claude.py <escenario.json> [argumentos que enviaria el coordinador...]
El escenario (JSON) controla el comportamiento:
  record      ruta donde guardar {args, stdin, cwd, env}
  write       lista de rutas relativas (a cwd) que se crean/modifican
  delete      lista de rutas relativas que se borran
  sleep       segundos de espera antes de responder
  spawn_child ruta donde guardar el PID de un proceso hijo que duerme (para probar que se mata el arbol)
  stdout_raw  texto exacto a imprimir (sustituye a la respuesta JSON)
  response    campos que se mezclan con la respuesta JSON por defecto
  noise       texto que se imprime antes del JSON
  stderr      texto para stderr
  exit        codigo de salida
"""
import json
import os
import subprocess
import sys
import time


def main() -> int:
    scenario = json.load(open(sys.argv[1], encoding="utf-8"))
    args = sys.argv[2:]
    if "--version" in args:
        print("0.0.0-fake")
        return 0
    stdin = sys.stdin.buffer.read().decode("utf-8")
    if scenario.get("record"):
        with open(scenario["record"], "w", encoding="utf-8") as f:
            json.dump({"args": args, "stdin": stdin, "cwd": os.getcwd(), "env": dict(os.environ)}, f)
    for rel in scenario.get("write", []):
        p = os.path.join(os.getcwd(), rel)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "a", encoding="utf-8") as f:
            f.write("cambio de prueba\n")
    for rel in scenario.get("delete", []):
        os.remove(os.path.join(os.getcwd(), rel))
    if scenario.get("spawn_child"):
        child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(120)"])
        with open(scenario["spawn_child"], "w") as f:
            f.write(str(child.pid))
    if scenario.get("sleep"):
        time.sleep(float(scenario["sleep"]))
    if scenario.get("noise"):
        print(scenario["noise"])
    if scenario.get("stderr"):
        sys.stderr.write(scenario["stderr"])
    if "stdout_raw" in scenario:
        sys.stdout.write(scenario["stdout_raw"])
    else:
        body = {"type": "result", "subtype": "success", "is_error": False, "result": "hecho",
                "permission_denials": [], "num_turns": 2, "total_cost_usd": 0.0123, "session_id": "fake-session"}
        body.update(scenario.get("response", {}))
        sys.stdout.write(json.dumps(body))
    sys.stdout.flush()
    return int(scenario.get("exit", 0))


if __name__ == "__main__":
    sys.exit(main())
