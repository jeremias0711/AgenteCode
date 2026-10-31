# orchestrator — núcleo del coordinador de GameDevAgent

Primera versión del núcleo Python que permite a GameDevAgent hablar con **Claude Code** (CLI, modo `-p`)
**sin usar la API de OpenAI**. Solo biblioteca estándar de Python 3.12; no hay dependencias que instalar.

> Estado: módulo 1 (ejecutor) + pruebas con un Claude Code ficticio. Verificado además con **Claude Code 2.1.314 real**
> en tareas de **solo lectura** (argumentos aceptados, JSON, `--max-turns`, `--tools`). **Las tareas de escritura no se han
> probado todavía con Claude real.**

## Requisito: un CLI de Claude Code funcional

El coordinador no incluye ni instala Claude Code. Necesita un `claude.exe` funcional y lo busca así:

1. argumento explícito: `Config.from_environment(claude=r"C:\ruta\claude.exe")`;
2. variable de entorno `GAMEDEVAGENT_CLAUDE`;
3. `claude` en el `PATH` (se consigue con `claude install`, acción del usuario).

No se usa la ruta de la extensión de VS Code porque cambia con cada versión. No se admiten scripts
(`.bat`, `.cmd`, `.ps1`...) como ejecutable. Comprobación rápida (no llama al modelo): `runner.check_cli()`.

## Componentes

| Archivo | Responsabilidad |
|---|---|
| `config.py` | `Config` (rutas, límites por defecto, hashes esperados), `locate_claude()`, lista blanca de variables de entorno. |
| `task.py` | `Task`: prompt + alcance. Valida herramientas, rutas de escritura, agente, modo de permisos y límites. |
| `claude_runner.py` | `ClaudeRunner`: construye y **valida** los argumentos, lanza `claude -p`, vigila tiempo, integridad y alcance, interpreta el JSON. |
| `integrity.py` | Integridad del hook (`IntegrityChecker`), huellas de rutas protegidas y del árbol, coincidencia de rutas con el alcance. |
| `results.py` | `RunResult` y `Status`. |
| `logger.py` | `RunLogger`: un registro JSON Lines por ejecución en `orchestrator/runs/` (con su propio `.gitignore`). |
| `errors.py` | Excepciones de configuración/programación. |

### `Task`
```python
Task(prompt="...",                      # va por stdin, nunca por la línea de comandos
     allowed_paths=["Game/Assets/Scripts/**"],   # dónde puede ESCRIBIR; vacío = solo lectura
     allowed_tools=("Read", "Grep", "Glob"),     # lista cerrada; el resto se rechaza
     agent="unity-game-developer",               # solo los cuatro agentes del proyecto
     max_turns=20, timeout_s=900, max_budget_usd=1.0,
     permission_mode="dontAsk")                  # dontAsk | plan | acceptEdits
```
* **Mínimo privilegio — `--tools` fija el conjunto EXACTO** de herramientas que existe para Claude Code (verificado con
  Claude Code 2.1.314: `--allowedTools` por sí solo solo *aprueba*, no restringe). `Task.tools()` lo deriva solo de campos validados:

  | Herramienta | Se habilita solo si... |
  |---|---|
  | `Read`, `Grep`, `Glob` | están en `allowed_tools` (por defecto las tres) |
  | `Edit` | hay `allowed_paths` válidas (regla `Edit(/ruta)` por cada una) |
  | `Write` | además `allow_write_tool=True` **y** `write_justification` (≥ 20 caracteres) |
  | `Bash` / `PowerShell` | `allowed_tools` declara al menos un patrón de ese shell, p. ej. `Bash(git status*)` |

  Una tarea de solo lectura recibe exactamente `--tools Read,Grep,Glob`. Nada se habilita por aparecer en argumentos externos:
  `validate_args` exige `--tools`, rechaza duplicados y herramientas desconocidas, y exige que `--allowedTools` sea coherente
  con `--tools` (sin `Edit`/`Bash`/`PowerShell` sin reglas acotadas, sin `Write` propio). `--disallowedTools` debe incluir
  siempre la lista fija. El ejecutor además **revalida la tarea** antes de usarla (por si se manipuló con `object.__setattr__`).
* Patrones de shell aceptados (lista cerrada): `git status/diff/log/show/ls-files/rev-parse`, `dotnet build/test`,
  `python -m unittest`. Nada de `Bash` sin patrón, `WebFetch`, `WebSearch`...
* `allowed_paths` rechaza rutas absolutas, `..`, comodines al principio y las zonas protegidas (`.claude`, `.git`,
  `templates/originals`, `CLAUDE.md`, `.gitignore`, `.gitattributes`, `manifest.json`, `packages-lock.json`, `ProjectVersion.txt`).
  Use patrones concretos como `Game/Assets/Scripts/**`.

### `ClaudeRunner.run(task) -> RunResult`
1. Construye los argumentos y los pasa por `validate_args` (**lista blanca** de opciones). Siempre incluye
   `-p --output-format json --permission-prompts none --max-turns N --max-budget-usd X --strict-mcp-config --no-session-persistence`
   y `--disallowedTools` con lo que ninguna tarea puede quitar (git commit/push/merge/reset/clean, `gh`, instaladores, web).
2. **Antes**: `IntegrityChecker.verify()` (hook registrado con `onFailure=block`, SHA-256 de `guard.py` igual al fijado en
   `settings.json` y a los hashes externos opcionales). Si falla, **Claude Code no se lanza**.
3. Toma huellas de las rutas protegidas y del proyecto; lanza `claude` con `subprocess.Popen(lista, shell=False)`, el
   prompt por stdin, `cwd` = raíz del proyecto y un entorno saneado (lista blanca; sin `OPENAI_*` ni `CLAUDE_CODE_SIMPLE`).
4. Tiempo máximo: si se agota, mata el **árbol** de procesos (`taskkill /T /F`).
5. **Después**: repite la verificación y las huellas; los cambios fuera de `allowed_paths` → `SCOPE_VIOLATION`.
6. Interpreta el JSON (`result`, `is_error`, `subtype`, `permission_denials`, `num_turns`, `total_cost_usd`, `session_id`).
7. Escribe **un registro** JSONL (también si falló antes de lanzar Claude).

Nunca usa `--dangerously-skip-permissions`, `--bare` (desactiva los hooks), `--settings`, `--setting-sources`, `--add-dir`,
`--mcp-config`, `--agents`, `--system-prompt`, `--resume`... y no pasa `--permission-mode bypassPermissions`/`auto`.

### Confianza de la carpeta (avisos)
Claude Code ignora las reglas `permissions.allow` del proyecto hasta que se acepta el diálogo de confianza y lo dice en
stderr (*"Ignoring N permissions.allow entries ... this workspace has not been trusted"*). El ejecutor lo **detecta y registra**
(`RunResult.trust_warning`, `RunResult.warnings` y el registro JSONL) sin cambiar el estado de la ejecución. **No** modifica la
configuración de confianza ni debilita permisos ni usa `--dangerously-skip-permissions`: aceptar la confianza es una acción
del usuario en una sesión interactiva. Con el aviso presente siguen aplicando `--tools`, `--disallowedTools` y el hook.

### Otros campos del resultado
`terminal_reason` (`completed`, `max_turns`...), `stop_reason`, `usage`, `model_usage`, `tools` (valor exacto de `--tools`).
Nota: con `--max-turns N`, Claude Code puede informar `num_turns = N + 1` al detenerse por límite.

### Estados (`Status`) — de mayor a menor gravedad
`INTEGRITY_FAILURE` > `SCOPE_VIOLATION` > `TIMEOUT` > `ERROR` > `DENIED` > `OK`.
`DENIED` (con `needs_authorization=True`) significa que Claude pidió algo que no está concedido (**también cuando lo bloquea el
hook**). El coordinador **se detiene, no reintenta y no escala permisos**: lo decide una persona. Un `SCOPE_VIOLATION` o un
`INTEGRITY_FAILURE` se **informan**; el coordinador no revierte ni borra nada por su cuenta.

## Uso desde el futuro coordinador principal

```python
from orchestrator import ClaudeRunner, Config, Status, Task

runner = ClaudeRunner(Config.from_environment())     # GAMEDEVAGENT_CLAUDE o claude en el PATH
runner.check_cli()                                   # falla pronto si no hay CLI

result = runner.run(Task(
    prompt="Revisa Game/Assets/Scripts y resume los problemas.",
    agent="code-reviewer", max_turns=10, max_budget_usd=0.5, label="revision-scripts"))

if result.status is Status.OK:
    print(result.result_text)
elif result.needs_authorization:
    print("Detenido: hace falta autorización manual", result.denials)   # no reintentar solo
else:
    print(result.status.value, result.summary, result.problems)
```
* Lo debe lanzar **el usuario desde su terminal**, no Claude Code: si Claude lo ejecutara, cada ejecución pediría confirmación
  y el hook trata los scripts de Python como código no analizable.
* Una cola de tareas, planes y aprobaciones irán por encima de `ClaudeRunner`; este módulo es solo el ejecutor seguro.
* Para fijar hashes fuera del proyecto: `GAMEDEVAGENT_EXPECTED_GUARD_SHA256` y `GAMEDEVAGENT_EXPECTED_SETTINGS_SHA256`.

## Pruebas
```
python -m unittest discover -s tests -t . -v        # desde la raíz del proyecto
```
Usan `tests/fake_claude.py` (un ejecutable ficticio): **ninguna llamada real a Claude**, sin red ni modelo.
Cubren argumentos, stdin, JSON, errores, tiempo máximo (incluido el proceso hijo), opciones prohibidas, cambios fuera
de alcance, integridad y registro.

## Alcance validado y limitaciones pendientes

**Alcance validado (Fase 2.1):** ejecución de tareas de **solo lectura** con Claude Code 2.1.314 real
(`--tools Read,Grep,Glob`): argumentos aceptados, JSON devuelto, `--max-turns`, integridad del hook antes y después,
y sin cambios en el proyecto. Las tareas con escritura **no** están validadas.

**Limitaciones pendientes** (no se resuelven en la Fase 2.1; no debe darse por probado lo que aquí figura como pendiente):

1. **Falta de validación de escritura con Claude real.** Nunca se ha ejecutado una tarea con `allowed_paths`, `Edit` o
   `Write` contra Claude Code real: solo contra el ejecutable ficticio. Hasta hacerlo no se sabe cómo se comportan en la
   práctica `Edit(/ruta)` ni `Write` dentro de las rutas autorizadas, ni cómo reacciona el hook a esas escrituras reales.
2. **Efecto de `dontAsk` pendiente de comprobar.** El CLI acepta `--permission-mode dontAsk`, pero su efecto propio no se ha
   observado: distinguirlo de los demás modos exige una escritura y, con `--permission-prompts none`, lo que requeriría
   confirmación se deniega igualmente. Hasta entonces la restricción real de herramientas la da `--tools`, no el modo.
3. **Reglas `allow` ignoradas por falta de confianza de la carpeta.** Mientras no se acepte el diálogo de confianza en una
   sesión interactiva, Claude Code ignora las reglas `permissions.allow` del proyecto (el ejecutor lo detecta y lo registra
   en `trust_warning`/`warnings`). El coordinador no modifica la configuración de confianza. `--tools`, `--disallowedTools`
   y el hook siguen aplicándose.
4. **Verificación incompleta del contenido de archivos superiores a 1 MiB.** La huella de un archivo de más de 1 MiB es solo
   (tamaño, mtime): un cambio que conserve ambos no se detectaría. El alcance de escritura se comprueba **después** de la
   ejecución (huellas antes/después); durante ella solo lo limitan los permisos de Claude Code y el hook.
5. **Ausencia de cola, reintentos y CLI propio.** Tampoco hay paralelismo, planes ni aprobaciones: este módulo es solo el
   ejecutor seguro de una tarea cada vez.

Otras notas:
* `--strict-mcp-config`: con `--tools` no aparecieron herramientas `mcp__*`; es un indicio, no una prueba de que no se cargue
  ningún servidor MCP.
* Recorrer proyectos grandes (Unity con miles de archivos) puede tardar; se omiten `Library`, `Temp`, `.git`, etc.
* Un `claude` cuyo JSON cambie de formato dará `ERROR` (la salida se trata como no interpretable), no un falso `OK`.
