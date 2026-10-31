#!/usr/bin/env python3
import atexit, os; _OK = [False]; atexit.register(lambda: None if _OK[0] else os._exit(2))  # archivo truncado => bloquea
"""Hook PreToolUse de GameDevAgent (version 2.1).

Novedades de la 2.1 (principio: un destino que no se puede resolver NO se da por seguro):
  * Orden destructiva o de escritura con destino dinamico ($var, subexpresion, %VAR%) o que
    recibe sus rutas por tuberia/xargs: pide confirmacion, o BLOQUEA si la tuberia nace de
    un listado (Get-ChildItem, ls, find...) que contiene rutas protegidas.
  * Los comodines se expanden contra el disco y cada resultado se evalua con realpath
    (cubre nombres cortos 8.3 y enlaces).
  * Metodos .NET de escritura/borrado ([IO.File]::Delete, .Delete(), ...) con argumentos que no
    sean cadenas literales: confirmacion. Operador de llamada & con comando calculado: confirmacion.
  * Interpretes y ejecutables que corren codigo (python, node, bash, powershell -File, .ps1, make,
    dotnet run, instaladores...) piden confirmacion salvo scripts de solo lectura con SHA-256.

Se registra para Bash, PowerShell, Write, Edit, MultiEdit y NotebookEdit. Lee por la
entrada estandar el JSON del hook (campos documentados: tool_name,
tool_input.command, tool_input.file_path, cwd) y decide:

  * exit 2 + motivo en stderr  -> bloquea la llamada.
  * exit 0 + JSON "ask"        -> fuerza la confirmacion del usuario.
  * exit 0 sin salida          -> sin decision: sigue el flujo normal de permisos.

PROTEGIDAS (se BLOQUEA escribir, mover, borrar o cambiar permisos; LEER y usar como
ORIGEN de una copia, comprimir o calcular hashes se permite):
  * templates/originals/
  * .claude/settings.json y .claude/settings.local.json
  * .claude/hooks/ (incluye este archivo)
  * las rutas de .claude/hooks/protected_paths.local.txt (locales, no se publican)

PIDEN CONFIRMACION ("ask"):
  * Modificar CLAUDE.md, .claude/agents/, .claude/skills/, .gitignore, .gitattributes y
    los archivos de version/dependencias de Unity (manifest.json, packages-lock.json,
    ProjectVersion.txt).
  * git commit/push/merge/pull/rebase/cherry-pick/revert, checkout/restore/switch que
    descartan cambios, stash drop/clear, branch -D, tag -d, push --force-with-lease.
  * GitHub CLI: pr merge, repo delete/edit/archive/rename/transfer, release delete y
    gh api con metodo distinto de GET.

BLOQUEADAS siempre: git reset --hard/--merge/--keep, git clean (sin --dry-run),
push --force/-f/--mirror/--delete/--prune/+refspec/:refspec, filter-branch, filter-repo,
reflog expire/delete, update-ref -d, git add de templates/originals/ (tambien con -f) y
configuracion de git que ejecuta codigo (alias, core.hooksPath, core.fsmonitor...).

Fallo seguro: una entrada malformada, un archivo de configuracion local invalido o un
error interno terminan con exit 2. En settings.json el hook se registra con
"onFailure": "block" para que tampoco se convierta en autorizacion si no arranca, agota
el tiempo o devuelve otro codigo.

LIMITACION: analiza texto de comandos y rutas. No puede detectar escrituras hechas por
scripts o ejecutables que no nombren la ruta, rutas construidas en tiempo de ejecucion,
nombres cortos 8.3, alias de Git ya definidos en la configuracion, ni evitar que un
proceso con tus mismos permisos de Windows cambie las ACL. No es un aislamiento del
sistema de archivos: complementalo con permisos del sistema (ver protect_backups.ps1).
"""
import base64
import fnmatch
import glob
import hashlib
import json
import os
import posixpath
import re
import sys
from pathlib import Path

MAX_DEPTH = 4
MAX_LEN = 100_000

HOOKS_DIR = Path(__file__).resolve().parent
PROJECT_DIR = HOOKS_DIR.parents[1]


def _posix(p):
    return p.replace("\\", "/")


def _abs(p):
    return posixpath.normpath(_posix(str(p))).lower()


ORIG = _abs(PROJECT_DIR / "templates" / "originals")
HOOKS = _abs(HOOKS_DIR)
SETTINGS = [_abs(PROJECT_DIR / ".claude" / "settings.json"),
            _abs(PROJECT_DIR / ".claude" / "settings.local.json")]
TREES = [ORIG, HOOKS]          # directorios protegidos con todo su contenido
TARGETS = TREES + SETTINGS     # configure() anade las rutas locales
EXTRA_WORDS = []               # nombre final de las rutas locales (para variables)
SCRIPT_ALLOW = {}              # script de solo lectura -> sha256 (readonly_scripts.txt)

ASK_FILES = [_abs(PROJECT_DIR / p) for p in (
    "CLAUDE.md", ".gitignore", ".gitattributes", "Game/Packages/manifest.json",
    "Game/Packages/packages-lock.json", "Game/ProjectSettings/ProjectVersion.txt")]
ASK_TREES = [_abs(PROJECT_DIR / ".claude" / "agents"), _abs(PROJECT_DIR / ".claude" / "skills")]
ASK_TARGETS = ASK_TREES + ASK_FILES
ASK_DESC = ("modificar un archivo de instrucciones (CLAUDE.md, .claude/agents|skills, "
            ".gitignore, .gitattributes) o de version/dependencias de Unity")

MENTIONS = ("templates/originals", ".claude/settings.json",
            ".claude/settings.local.json", ".claude/hooks")
ORIG_MENTIONS = ("templates/originals",)
PROT_DESC = ("una ruta protegida (templates/originals/, .claude/settings*.json, "
             ".claude/hooks/ o las de protected_paths.local.txt)")
# Una mencion cuenta solo si no continua con un caracter de nombre (asi
# 'settings.json.bak' o 'originals-notas.md' no se confunden con la ruta).
_BOUND = r"(?![A-Za-z0-9_.\-])"


def _build_mention_re(names):
    return re.compile("(?:%s)%s" % ("|".join(re.escape(m) for m in names), _BOUND))


MENTION_RE = _build_mention_re(MENTIONS)
ORIG_MENTION_RE = _build_mention_re(ORIG_MENTIONS)
HOME = _abs(Path.home())
DYNAMIC = re.compile(r"[$%`]")
NULL_DEVICES = {"/dev/null", "nul", "/dev/zero", "/dev/stdin", "/dev/stdout", "/dev/stderr",
                "/dev/tty", "$null"}
FILE_TOOLS = ("Write", "Edit", "MultiEdit", "NotebookEdit")
SHELL_TOOLS = ("Bash", "PowerShell")


class Deny(Exception):
    pass


class Ctx:
    def __init__(self):
        self.tool = ""
        self.asks = []
        self.mentioned = False
        self.text_words = False  # el comando completo nombra una ruta protegida
        self.loop_anc = False    # un bucle for recorre un comodin/directorio que contiene rutas protegidas


def configure():
    """Lee la configuracion LOCAL (no se publica en Git). Un archivo invalido
    lanza ValueError: el hook falla cerrado en lugar de proteger menos."""
    global MENTION_RE
    f = HOOKS_DIR / "protected_paths.local.txt"
    names = list(MENTIONS)
    if f.exists():
        for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            s = line.strip()
            if not s or s.startswith("#"):
                continue
            if not re.match(r"^[A-Za-z]:[\\/]", s):
                raise ValueError("protected_paths.local.txt linea %d: se esperaba una ruta "
                                 "absoluta de Windows (p. ej. C:\\Backups\\): %r" % (n, s))
            a = _abs(s)
            if len(a) <= 3:
                raise ValueError("protected_paths.local.txt linea %d: no se puede proteger "
                                 "una unidad completa: %r" % (n, s))
            me = _abs(PROJECT_DIR)
            if me == a or me.startswith(a + "/"):
                raise ValueError("protected_paths.local.txt linea %d: %r contiene el propio "
                                 "proyecto y lo dejaria sin poder escribir" % (n, s))
            TREES.append(a)
            TARGETS.append(a)
            names += [a, "/%s%s" % (a[0], a[2:]), "/mnt/%s%s" % (a[0], a[2:]),
                      "/cygdrive/%s%s" % (a[0], a[2:])]
            w = a.rsplit("/", 1)[-1]
            if len(w) >= 5:
                EXTRA_WORDS.append(w)
    MENTION_RE = _build_mention_re(names)
    g = HOOKS_DIR / "readonly_scripts.txt"
    if g.exists():
        for n, line in enumerate(g.read_text(encoding="utf-8").splitlines(), 1):
            s = line.strip()
            if not s or s.startswith("#"):
                continue
            parts = s.split()
            if len(parts) != 2 or not re.fullmatch(r"[0-9a-fA-F]{64}", parts[1]) \
                    or re.match(r"^([A-Za-z]:|/|\\)", parts[0]):
                raise ValueError("readonly_scripts.txt linea %d: formato 'ruta/relativa.py "
                                 "<sha256>' esperado: %r" % (n, s))
            SCRIPT_ALLOW[_abs(PROJECT_DIR / parts[0])] = parts[1].lower()


# --------------------------------------------------------------------------
# Analizador lexico (comillas, separadores, redirecciones, sustituciones)
# --------------------------------------------------------------------------
class Seg:
    def __init__(self, sep):
        self.sep = sep          # start | seq | pipe | open | close
        self.tokens = []
        self.redirects = []
        self.subs = []
        self.heredocs = []
        self.raw = ""
        self.first_quoted = False   # el primer token venia entre comillas
        self.after_amp = False      # el segmento sigue a un '&' (operador de llamada)


def _match_paren(text, i):
    depth, q, j = 0, None, i
    while j < len(text):
        c = text[j]
        if q:
            if c == q:
                q = None
            elif c == "\\" and q == '"':
                j += 1
        elif c in "'\"":
            q = c
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return j
        j += 1
    return len(text)


class Lexer:
    def __init__(self, text):
        self.t = text
        self.i = 0
        self.segs = []
        self.cur = Seg("start")
        self.buf = []
        self.intok = False
        self.start = 0
        self.pending = []  # (delimitador, segmento) de heredocs

    def push(self, s):
        self.buf.append(s)
        self.intok = True

    def flush(self):
        if self.intok:
            self.cur.tokens.append("".join(self.buf))
        self.buf = []
        self.intok = False

    def cut(self, sep, adv, amp=False):
        self.flush()
        if self.cur.tokens or self.cur.redirects or self.cur.subs:
            self.cur.raw = self.t[self.start:self.i].strip()
            self.segs.append(self.cur)
        self.cur = Seg(sep)
        self.cur.after_amp = amp
        self.i += adv
        self.start = self.i

    def read_word(self):
        """Lee una palabra (con comillas) para el destino de una redireccion."""
        t, n = self.t, len(self.t)
        while self.i < n and t[self.i] in " \t":
            self.i += 1
        out = []
        while self.i < n and t[self.i] not in " \t\r\n;&|()<>":
            c = t[self.i]
            if c in "'\"":
                j = t.find(c, self.i + 1)
                j = n if j == -1 else j
                out.append(t[self.i + 1:j])
                self.i = j + 1
            else:
                out.append(c)
                self.i += 1
        return "".join(out)

    def read_heredoc_bodies(self):
        t, n = self.t, len(self.t)
        for delim, seg in self.pending:
            lines = []
            while self.i < n:
                j = t.find("\n", self.i)
                j = n if j == -1 else j
                line = t[self.i:j]
                self.i = min(j + 1, n)
                if line.strip("\r").strip() == delim:
                    break
                lines.append(line)
            seg.heredocs.append("\n".join(lines))
        self.pending = []
        self.start = self.i

    def run(self):
        t, n = self.t, len(self.t)
        while self.i < n:
            c = t[self.i]
            nxt = t[self.i + 1] if self.i + 1 < n else ""
            if c in " \t\r":
                self.flush()
                self.i += 1
            elif c == "\n":
                self.cut("seq", 1)
                if self.pending:
                    self.read_heredoc_bodies()
            elif c == ";":
                self.cut("seq", 1)
            elif c == "&":
                if nxt == "&":
                    self.cut("seq", 2)
                else:
                    self.cut("seq", 1, amp=True)
            elif c == "|":
                if nxt == "|":
                    self.cut("seq", 2)
                elif nxt == "&":
                    self.cut("pipe", 2)
                else:
                    self.cut("pipe", 1)
            elif c in "({":
                self.cut("open", 1)
            elif c in ")}":
                self.cut("close", 1)
            elif c == "$" and nxt == "(":
                j = _match_paren(t, self.i + 1)
                self.cur.subs.append(t[self.i + 2:j])
                self.push(t[self.i:j + 1])
                self.i = j + 1
            elif c == "$" and nxt == "{":
                j = t.find("}", self.i)
                j = n - 1 if j == -1 else j
                self.push(t[self.i:j + 1])
                self.i = j + 1
            elif c == "`":
                k = t.find("`", self.i + 1)
                if k != -1 and " " in t[self.i + 1:k]:
                    self.cur.subs.append(t[self.i + 1:k])
                    self.push(t[self.i:k + 1])
                    self.i = k + 1
                else:
                    self.push(t[self.i:self.i + 2])
                    self.i += 2
            elif c == "@" and nxt and nxt in "'\"" and not self.intok:
                end = t.find("\n" + nxt + "@", self.i + 2)
                if end == -1:
                    end = n
                self.cur.heredocs.append(t[self.i + 2:end])
                self.push("<aqui-cadena>")
                self.i = min(end + 3, n)
            elif c == "'":
                if not self.intok and not self.cur.tokens:
                    self.cur.first_quoted = True
                j = t.find("'", self.i + 1)
                j = n if j == -1 else j
                self.push(t[self.i + 1:j])
                self.i = j + 1
            elif c == '"':
                if not self.intok and not self.cur.tokens:
                    self.cur.first_quoted = True
                j, parts = self.i + 1, []
                while j < n and t[j] != '"':
                    if t[j] in "\\`" and j + 1 < n:
                        parts.append(t[j:j + 2])
                        j += 2
                    elif t[j] == "$" and j + 1 < n and t[j + 1] == "(":
                        k = _match_paren(t, j + 1)
                        self.cur.subs.append(t[j + 2:k])
                        parts.append(t[j:k + 1])
                        j = k + 1
                    else:
                        parts.append(t[j])
                        j += 1
                self.push("".join(parts))
                self.i = j + 1
            elif c == "<":
                if t.startswith("<<<", self.i):
                    self.flush()
                    self.i += 3
                elif nxt == "<":
                    self.flush()
                    self.i += 2
                    if self.i < n and t[self.i] == "-":
                        self.i += 1
                    delim = self.read_word()
                    if delim:
                        self.pending.append((delim, self.cur))
                else:
                    self.flush()
                    self.i += 1
            elif c == ">":
                # '2>' y '*>' (todos los flujos de PowerShell) son operadores, no argumentos
                if self.intok and ("".join(self.buf).isdigit() or "".join(self.buf) == "*"):
                    self.buf, self.intok = [], False
                self.flush()
                self.i += 1
                if self.i < n and t[self.i] in ">|":
                    self.i += 1
                if self.i < n and t[self.i] == "&":
                    self.i += 1
                    while self.i < n and (t[self.i].isdigit() or t[self.i] == "-"):
                        self.i += 1
                else:
                    target = self.read_word()
                    if target:
                        self.cur.redirects.append(target)
            else:
                self.push(c)
                self.i += 1
        self.cut("seq", 0)
        return self.segs


# --------------------------------------------------------------------------
# Rutas
# --------------------------------------------------------------------------
def _resolve(token, cwd, literal=False):
    """Ruta absoluta normalizada en minusculas, o None si es dinamica.
    literal=True: para herramientas de archivos, donde $ y % son caracteres normales."""
    t = _posix(token.strip().strip("\"'"))
    if not t or (not literal and DYNAMIC.search(t)):
        return None
    t = re.sub(r"^//\?/", "", t)
    m = re.match(r"^/(?:mnt/|cygdrive/)?([A-Za-z])(?:/(.*))?$", t)
    if m:
        t = "%s:/%s" % (m.group(1), m.group(2) or "")
    elif t == "~" or t.startswith("~/"):
        t = HOME + t[1:]
    if not re.match(r"^[A-Za-z]:", t):
        if t.startswith("/"):
            return posixpath.normpath(t).lower()
        t = cwd + "/" + t
    return posixpath.normpath(t).lower()


def _variants(tok):
    out = {tok}
    if "=" in tok:
        out.add(tok.split("=", 1)[1])
    if tok.startswith("-") and ":" in tok:
        out.add(tok.split(":", 1)[1])
    for v in list(out):
        if "," in v:
            out.update(p for p in v.split(",") if p)
    return out


def _match(p, targets=None):
    """'direct' | 'ancestor' | None de una ruta absoluta frente a los objetivos."""
    anc = False
    glob = any(ch in p for ch in "*?[")
    for t in (targets or TARGETS):
        if glob:
            if fnmatch.fnmatchcase(t, p) or fnmatch.fnmatchcase(t + "/x", p):
                return "direct"
        elif p == t or p.startswith(t + "/"):
            return "direct"
        elif t.startswith(p.rstrip("/") + "/"):
            anc = True
    return "ancestor" if anc else None


def _real(p):
    """Ruta real (resuelve enlaces simbolicos y junctions) o None si no cambia."""
    try:
        r = _abs(os.path.realpath(p))
    except Exception:
        return None
    return r if r != p else None


def prot_kind(tok, cwd, targets=None, mention_re=None):
    """'direct' si apunta a una ruta protegida (o dentro), 'ancestor' si es un
    directorio que contiene alguna, None en otro caso. targets limita las rutas;
    mention_re=False desactiva la busqueda textual."""
    mre = MENTION_RE if mention_re is None else mention_re
    anc = False
    for v in _variants(tok):
        v = v.strip().strip("\"'")
        if not v or v.lower() in NULL_DEVICES:
            continue
        if mre and mre.search(_posix(v).lower()):
            return "direct"
        p = _resolve(v, cwd)
        if p is None:
            continue
        # La ruta real solo se resuelve para rutas con unidad (C:/...): una ruta raiz de
        # Git Bash como /dev/null no es C:\dev\null.
        real = None if (any(c in p for c in "*?[") or not re.match(r"^[a-z]:", p)) else _real(p)
        for q in (p, real):
            if q is None:
                continue
            k = _match(q, targets)
            if k == "direct":
                return "direct"
            anc = anc or k == "ancestor"
        if re.match(r"^[a-z]:", p) and any(c in p for c in "*?["):
            hits = _glob_expand(p)
            if hits is None:          # demasiados resultados: no se puede demostrar que sea seguro
                anc = True
                continue
            for q in hits:
                k = _match(q, targets)
                if k == "direct":
                    return "direct"
                anc = anc or k == "ancestor"
    return "ancestor" if anc else None


GLOB_LIMIT = 1500


def _glob_expand(p):
    """Expande un comodin contra el disco. Devuelve las rutas encontradas y, de cada una, su ruta
    real (resuelve nombres 8.3, enlaces y junctions). None si hay demasiadas o falla."""
    out = []
    try:
        try:
            it = glob.iglob(p, recursive=True, include_hidden=True)
        except TypeError:             # Python < 3.11
            it = glob.iglob(p, recursive=True)
        for n, m in enumerate(it):
            if n >= GLOB_LIMIT:
                return None
            a = _abs(m)
            out.append(a)
            r = _real(a)
            if r:
                out.append(r)
    except Exception:
        return None
    return out


def ask_kind(tok, cwd):
    return prot_kind(tok, cwd, ASK_TARGETS, False) == "direct"


def asks_any(tokens, cwd):
    return any(ask_kind(t, cwd) for t in tokens)


def inside_protected(cwd):
    return any(cwd == t or cwd.startswith(t + "/") for t in TREES)


def _has_mention(text):
    return bool(MENTION_RE.search(_posix(text).lower()))


def _mentions_text(raw):
    n = _posix(raw).lower()
    if _has_mention(raw) or ("templates" in n and "originals" in n):
        return True
    return any(re.search(r"(?<![A-Za-z0-9_\-])%s%s" % (re.escape(w), _BOUND), n)
               for w in EXTRA_WORDS)


def classify(tokens, redirects, raw, cwd):
    hit = anc = False
    for t in list(tokens) + list(redirects):
        k = prot_kind(t, cwd)
        hit = hit or k == "direct"
        anc = anc or k == "ancestor"
    return hit, anc, _mentions_text(raw)


# --------------------------------------------------------------------------
# Herramientas de archivos (Write, Edit, MultiEdit, NotebookEdit)
# --------------------------------------------------------------------------
def check_file_tool(tool, tin, cwd, ctx):
    if not isinstance(tin, dict):
        raise ValueError("falta tool_input")
    paths = [tin[k] for k in ("file_path", "notebook_path")
             if isinstance(tin.get(k), str) and tin[k].strip()]
    if not paths:
        raise ValueError("falta tool_input.file_path")
    for path in paths:
        p = _resolve(path, cwd, literal=True)
        if p is None:
            raise ValueError("ruta no interpretable")
        for q in (p, _real(p)):
            if q is None:
                continue
            if _match(q) == "direct":
                raise Deny("%s sobre '%s' bloqueado: %s no se modifica con las "
                           "herramientas de archivos." % (tool, path, PROT_DESC))
            if _match(q, ASK_TARGETS) == "direct":
                ctx.asks.append(ASK_DESC)


# --------------------------------------------------------------------------
# Verbos
# --------------------------------------------------------------------------
WRAPPERS = {"env", "command", "builtin", "time", "nice", "nohup", "stdbuf", "exec",
            "sudo", "doas", "call", "xargs", "timeout", "setsid", "ionice", "flock",
            "watch", "start"}
# Palabras clave que van delante de una orden real (se saltan) o que no son ordenes.
PREFIX_KW = {"if", "elif", "elseif", "while", "until", "then", "else", "do", "!"}
HARMLESS_KW = {"for", "foreach", "case", "select", "function", "esac", "done", "fi", "in",
               "param", "return", "break", "continue", "try", "catch", "finally", "switch",
               "trap", "throw", "exit"}
ASSIGN_OPS = {"=", "+=", "-=", "*=", "/=", "%=", "??="}
SHELLS = {"bash", "sh", "zsh", "dash", "ksh", "fish", "powershell", "pwsh", "cmd", "wsl"}
EVALS = {"iex", "invoke-expression", "eval", "invoke-command", "icm", "start-process", "saps"}
CD = {"cd", "chdir", "pushd", "set-location", "sl", "push-location"}
PYTHONS = {"python", "python3", "py", "pythonw"}
READONLY = {
    "ls", "dir", "gci", "get-childitem", "cat", "type", "gc", "get-content", "head",
    "tail", "grep", "egrep", "fgrep", "rg", "select-string", "sls", "findstr", "wc",
    "stat", "du", "diff", "fc", "compare-object", "test-path", "get-item", "gi",
    "get-itemproperty", "gp", "get-filehash", "sha256sum", "sha1sum", "md5sum", "file",
    "pwd", "echo", "write-output", "write-host", "printf", "which", "where",
    "get-command", "gcm", "resolve-path", "rvpa", "split-path", "join-path",
    "convert-path", "measure-object", "measure", "sort", "sort-object", "select-object",
    "where-object", "?", "foreach-object", "%", "format-table", "ft",
    "format-list", "fl", "out-string", "out-null", "out-host", "tree", "realpath",
    "basename", "dirname", "readlink", "true", "false", "test", "[", "date", "whoami",
    "hostname", "printenv", "get-date", "get-location", "gl", "get-acl", "less", "more",
    "cmp", "convertto-json", "convertfrom-json",
}
DESTRUCTIVE = {
    "rm", "del", "erase", "rd", "rmdir", "remove-item", "ri", "mv", "move", "move-item",
    "mi", "rename-item", "ren", "rni", "unlink", "shred", "truncate", "attrib", "icacls",
    "takeown", "cacls", "chmod", "chown", "chattr", "set-acl", "clear-content", "clc",
    "remove-itemproperty",
}
COPY = {"cp", "copy", "copy-item", "cpi", "xcopy", "rsync", "install", "robocopy", "ditto",
        "compress-archive", "expand-archive"}
ARCHIVERS = {"tar", "7z", "7za", "7zr", "zip", "unzip"}
# Ordenes que escriben/crean pero cuyo destino son sus argumentos (no consumen rutas por tuberia).
WRITERS = {
    "set-content", "sc", "add-content", "ac", "out-file", "tee-object", "tee", "new-item", "ni",
    "mkdir", "md", "touch", "ln", "mklink", "dd", "set-itemproperty", "sp", "new-itemproperty",
    "export-csv", "export-clixml", "fsutil",
}
# Ordenes que listan rutas: si una tuberia nace de ellas y acaba en una orden destructiva, lo
# que se borra es lo que listan.
LISTERS = {"gci", "ls", "dir", "get-childitem", "find", "fd", "tree"}
PASSTHRU = {"where-object", "?", "select-object", "select", "sort-object", "sort", "head", "tail",
            "uniq", "grep", "egrep", "fgrep", "rg", "measure-object", "foreach-object", "%",
            "out-null", "out-string", "format-table", "ft", "format-list", "fl"}
# Interpretes, gestores de compilacion e instaladores: ejecutan codigo que el hook no ve.
CODE_RUNNERS = {
    "node", "nodejs", "deno", "bun", "ruby", "perl", "php", "lua", "julia", "rscript", "tclsh",
    "make", "gmake", "nmake", "ninja", "msbuild", "gradle", "gradlew", "mvn", "npm", "npx", "yarn",
    "pnpm", "cargo", "go", "unity", "cscript", "wscript", "mshta", "rundll32", "regsvr32",
    "patch",
}
INSTALLERS = {"pip", "pip3", "pipx", "winget", "choco", "scoop", "apt", "apt-get", "brew", "gem",
              "conda", "nuget", "pacman", "yum", "dnf"}
DOTNET_RUN = {"run", "exec", "script", "fsi", "tool", "watch", "msbuild", "new", "workload"}
VERSION_ONLY = {"--version", "-v", "-version", "--help", "-h", "-?", "/?", "version"}
SCRIPT_EXT = (".ps1", ".sh", ".bat", ".cmd", ".py", ".js", ".mjs", ".vbs", ".wsf", ".rb", ".pl",
              ".exe", ".com", ".msi")
# Metodos .NET de PowerShell que escriben, mueven o borran.
DOTNET_WRITE = re.compile(
    r"(?i)(::\s*(?:delete|move|copy|writeall\w*|appendall\w*|create\w*|open(?!read|text\b)\w*|"
    r"set(?:attributes|creation\w*|lastwrite\w*|lastaccess\w*|accesscontrol))(?=\s*\()"
    r"|::\s*[('\"$]"
    r"|\.(?:delete|moveto|copyto|createtext|appendtext|openwrite|setaccesscontrol|"
    r"setattributevalue|invokescript|addscript)\s*\("
    r"|\.invokecommand\b|\[powershell\]::create"
    r"|\[(?:system\.)?io\.(?:streamwriter|filestream|binarywriter)\]"
    r"|new-object\s+(?:system\.)?io\.(?:streamwriter|filestream|binarywriter)"
    r"|\badd-type\b|\[scriptblock\]|\[activator\]|\[(?:system\.)?reflection\.|new-object\s+-com)")
CALL_OP = re.compile(r"(?<![>&\w$\-])&(?!&)\s*[\(\$'\"{]")
PIPE_VAR = re.compile(r"^[\"']?\$(?:_|psitem)(?:\.[A-Za-z]+)*[\"']?$", re.I)
SAFE_VARS = re.compile(r"(?i)\$(?:true|false|null)\b")
INPLACE = re.compile(r"^-[A-Za-z]*i")
GIT_PASS = {
    "fetch", "status", "diff", "log", "show", "ls-files", "check-ignore", "check-attr",
    "rev-parse", "remote", "fsck", "bundle", "cat-file", "blame", "describe",
    "ls-tree", "rev-list", "shortlog", "diff-tree", "hash-object", "version", "help",
    "for-each-ref", "var",
}
# Claves de configuracion de git que pueden ejecutar codigo o alterar comandos.
RISKY_CFG = re.compile(
    r"(?i)^(alias\.|core\.(hookspath|fsmonitor|sshcommand|editor|pager|askpass)$|"
    r"credential\.|filter\.|include\.|includeif\.|diff\..*\.(textconv|command)$|"
    r"merge\..*\.driver$)")
ASSIGN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*=")


def verb_name(tok):
    b = _posix(tok).rsplit("/", 1)[-1].lower()
    for ext in (".exe", ".cmd", ".bat", ".ps1", ".com"):
        if b.endswith(ext):
            return b[:-len(ext)]
    return b


def resolve_verb(tokens):
    i = 0
    while i < len(tokens):
        t = tokens[i]
        if ASSIGN.match(t) or t in ("&", "."):
            i += 1
            continue
        v = verb_name(t)
        if v in PREFIX_KW:
            i += 1
            continue
        if v in WRAPPERS:
            i += 1
            while i < len(tokens) and (
                tokens[i].startswith("-") or ASSIGN.match(tokens[i])
                or (v == "timeout" and re.match(r"^[\d.]+[smhd]?$", tokens[i]))
            ):
                i += 1
            continue
        return v, tokens[i + 1:]
    return None, []


def find_writes(args):
    bad = {"-delete", "-exec", "-execdir", "-ok", "-okdir", "-fprint", "-fprint0",
           "-fprintf", "-fls"}
    return any(a.lower() in bad for a in args)


def _special_readonly(verb, args):
    """Herramientas que solo leen cuando se usan de una forma concreta."""
    if verb == "certutil":
        return bool(args) and args[0].lower() == "-hashfile"
    return False


# --------------------------------------------------------------------------
# Git y GitHub CLI
# --------------------------------------------------------------------------
def _force_flag(rest):
    for r in rest:
        if r in ("-f", "--force"):
            return True
        if re.match(r"^-[A-Za-z]+$", r) and "f" in r[1:]:
            return True
    return False


def check_git_add(sub, rest, gcwd):
    force = _force_flag(rest)
    if force and any(r.startswith("--pathspec-from-file") for r in rest):
        raise Deny("git %s --force con --pathspec-from-file: no se pueden comprobar "
                   "las rutas." % sub)
    specs, after = [], False
    for r in rest:
        if r == "--" and not after:
            after = True
        elif after or not r.startswith("-"):
            specs.append(r)
    for s in specs:
        k = prot_kind(s, gcwd, [ORIG], ORIG_MENTION_RE)
        if k == "direct":
            raise Deny("git %s de templates/originals/ esta bloqueado (ni siquiera con "
                       "-f/--force)." % sub)
        if force and (k == "ancestor" or s.startswith(":") or any(c in s for c in "*?[")):
            raise Deny("git %s --force con '%s' podria incorporar rutas protegidas "
                       "(directorio que las contiene, comodin o pathspec magico)."
                       % (sub, s))


def _git_discards(sub, rest, gcwd):
    """True si checkout/restore/switch pueden descartar cambios sin confirmar."""
    if sub == "restore":
        staged = "--staged" in rest or "-S" in rest
        worktree = "--worktree" in rest or "-W" in rest
        return not (staged and not worktree)
    if sub == "switch":
        return any(r in ("--discard-changes", "-f", "--force") for r in rest)
    if "--" in rest or "." in rest:
        return True
    if any(r in ("-f", "--force", "--ours", "--theirs", "-p", "--patch") for r in rest):
        return True
    for r in rest:
        if r.startswith("-"):
            continue
        p = _resolve(r, gcwd)
        if p and os.path.exists(p):
            return True   # existe como ruta: restaura archivos
    return False


def check_git(args, cwd, seg, ctx):
    i, gcwd = 0, cwd
    while i < len(args):
        a = args[i]
        if a == "-C" and i + 1 < len(args):
            r = _resolve(args[i + 1], gcwd)
            gcwd = r if r else gcwd
            i += 2
        elif a == "-c":
            val = args[i + 1] if i + 1 < len(args) else ""
            if RISKY_CFG.match(val.split("=", 1)[0]):
                raise Deny("git -c %s: la configuracion en linea puede ejecutar "
                           "codigo u ocultar operaciones destructivas." % val.split("=", 1)[0])
            i += 2
        elif a in ("--git-dir", "--work-tree", "--namespace", "--exec-path",
                   "--super-prefix", "--config-env"):
            i += 2
        elif a.startswith("-"):
            i += 1
        else:
            break
    sub = args[i].lower() if i < len(args) else ""
    rest = args[i + 1:]
    lrest = [r.lower() for r in rest]

    if sub in ("filter-branch", "filter-repo"):
        raise Deny("git %s esta bloqueado: reescribe el historial. Si es imprescindible, "
                   "hazlo tu manualmente." % sub)
    if sub == "reset":
        if any(r in ("--hard", "--merge", "--keep") for r in lrest):
            raise Deny("git reset --hard/--merge/--keep esta bloqueado: descarta cambios "
                       "del arbol de trabajo. Si es imprescindible, hazlo tu manualmente.")
        return   # --soft, --mixed y 'reset <ruta>' no tocan el arbol de trabajo
    if sub == "clean":
        dry = any(r in ("-n", "--dry-run")
                  or (re.match(r"^-[dfinqxX]+$", r) and "n" in r) for r in rest)
        if not dry:
            raise Deny("git clean esta bloqueado salvo con -n/--dry-run: borra archivos "
                       "sin versionar de forma irreversible.")
        return
    if sub == "rebase":
        if any(r in ("--abort", "--continue", "--skip", "--quit") for r in lrest):
            return
        ctx.asks.append("git rebase")
        return
    if sub == "reflog" and any(r in ("expire", "delete") for r in lrest):
        raise Deny("git reflog expire/delete esta bloqueado: elimina historial recuperable.")
    if sub == "update-ref" and any(r in ("-d", "--delete") for r in lrest):
        raise Deny("git update-ref -d esta bloqueado: elimina referencias.")
    if sub == "config":
        pos = [r for r in rest if not r.startswith("-")]
        if len(pos) >= 2 and RISKY_CFG.match(pos[0]):
            raise Deny("git config %s: esa clave puede ejecutar codigo u ocultar "
                       "operaciones destructivas." % pos[0])
        return
    if sub in ("add", "stage"):
        check_git_add(sub, rest, gcwd)
        return
    if sub == "push":
        for r in rest:
            lr = r.lower()
            if lr.startswith("--force-with-lease") or lr == "--force-if-includes":
                ctx.asks.append("git push --force-with-lease")
                continue
            short_force = (re.match(r"^-[A-Za-z]+$", r) and "f" in r[1:]
                           and not r.startswith("--"))
            if (lr in ("-f", "--force", "--mirror", "--delete", "-d", "--prune")
                    or short_force or r.startswith("+")
                    or (r.startswith(":") and len(r) > 1)):
                raise Deny("git push con --force/--delete/--mirror/--prune o refspec '+' / ':' "
                           "esta bloqueado: reescribe o borra historial remoto.")
        ctx.asks.append("git push")
        return
    if sub == "commit":
        if "--" in rest:
            for s in rest[rest.index("--") + 1:]:
                if prot_kind(s, gcwd, [ORIG], ORIG_MENTION_RE) == "direct":
                    raise Deny("git commit de templates/originals/ esta bloqueado.")
        ctx.asks.append("git commit")
        return
    if sub in ("merge", "pull", "cherry-pick", "revert"):
        ctx.asks.append("git " + sub)
        return
    if sub == "branch":
        delete_force = ("--delete" in lrest and ("--force" in lrest or "-f" in lrest))
        if (any(r == "-D" or (re.match(r"^-[A-Za-z]+$", r) and "D" in r[1:]) for r in rest)
                or delete_force or any(r in ("-df", "-fd") for r in lrest)):
            ctx.asks.append("git branch -D")
        return
    if sub == "tag":
        if any(r in ("-d", "--delete") or (re.match(r"^-[A-Za-z]+$", r) and "d" in r[1:])
               for r in rest):
            ctx.asks.append("git tag -d")
        return
    if sub == "stash":
        action = next((r.lower() for r in rest if not r.startswith("-")), "push")
        if action in ("drop", "clear"):
            ctx.asks.append("git stash " + action)
            return
    if sub in GIT_PASS:
        return
    if sub in ("apply", "am"):
        ctx.asks.append("git %s (aplica parches que pueden tocar cualquier ruta)" % sub)
    # Resto de subcomandos (checkout, restore, switch, rm, mv, stash, apply...): pueden
    # modificar el arbol de trabajo o el indice, asi que no deben tocar rutas protegidas.
    if inside_protected(gcwd):
        raise Deny("git %s dentro de %s esta bloqueado." % (sub or "", PROT_DESC))
    hit, _anc, words = classify(rest, [], seg.raw, gcwd)
    if hit or words:
        raise Deny("git %s sobre %s esta bloqueado." % (sub, PROT_DESC))
    if sub in ("checkout", "restore", "switch") and _git_discards(sub, rest, gcwd):
        ctx.asks.append("git %s (puede descartar cambios)" % sub)
    elif asks_any(rest, gcwd):
        ctx.asks.append(ASK_DESC)


def check_gh(args, ctx):
    pos = [a for a in args if not a.startswith("-")]
    low = [a.lower() for a in args]
    g = pos[0].lower() if pos else ""
    s = pos[1].lower() if len(pos) > 1 else ""
    if g == "pr" and s == "merge":
        ctx.asks.append("gh pr merge")
    elif g == "repo" and s in ("delete", "edit", "archive", "unarchive", "rename", "transfer"):
        ctx.asks.append("gh repo " + s)
    elif g == "release" and s in ("delete", "delete-asset"):
        ctx.asks.append("gh release " + s)
    elif g == "api":
        method = None
        for k, a in enumerate(low):
            if a in ("-x", "--method") and k + 1 < len(low):
                method = low[k + 1]
            elif a.startswith("--method="):
                method = a.split("=", 1)[1]
        body = any(a in ("-f", "-F", "--field", "--raw-field", "--input") for a in args)
        if (method and method != "get") or (body and method is None):
            ctx.asks.append("gh api (modifica datos en GitHub)")


# --------------------------------------------------------------------------
# Destinos de copia / compresion
# --------------------------------------------------------------------------
def _copy_dest(verb, args):
    pos, dest, i = [], None, 0
    while i < len(args):
        a, la = args[i], args[i].lower()
        if la in ("-destination", "-destinationpath", "-dest", "-d", "-t",
                  "--target-directory") and i + 1 < len(args):
            dest = args[i + 1]
            i += 2
            continue
        if la.startswith("--target-directory="):
            dest = a.split("=", 1)[1]
        elif la.startswith("-destination:") or la.startswith("-destinationpath:"):
            dest = a.split(":", 1)[1]
        elif a.startswith("-") or (verb in ("robocopy", "xcopy")
                                   and re.match(r"^/[A-Za-z?][^/]*$", a)):
            pass
        else:
            pos.append(a)
        i += 1
    if dest is not None:
        return dest, pos
    if verb == "robocopy":
        return (pos[1] if len(pos) > 1 else None), pos
    return (pos[-1] if len(pos) > 1 else None), pos


def _tar(args):
    mode, archive, chdir, pos, i, first = "", None, None, [], 0, True
    while i < len(args):
        a = args[i]
        if a.startswith("--"):
            n, _, v = a.partition("=")
            if n == "--create":
                mode += "c"
            elif n in ("--extract", "--get"):
                mode += "x"
            elif n == "--list":
                mode += "t"
            elif n in ("--append", "--update", "--delete"):
                mode += "r"
            elif n in ("--file", "--directory"):
                if not v and i + 1 < len(args):
                    v = args[i + 1]
                    i += 1
                if n == "--file":
                    archive = v
                else:
                    chdir = v
        elif a == "-C" and i + 1 < len(args):
            chdir = args[i + 1]
            i += 1
        elif (a.startswith("-") and len(a) > 1) or (first and re.match(r"^[A-Za-z]+$", a)):
            letters = a.lstrip("-")
            for ch in letters:
                if ch in "cxtru":
                    mode += "r" if ch in "ru" else ch
            if "f" in letters and i + 1 < len(args):
                archive = args[i + 1]
                i += 1
        else:
            pos.append(a)
        first = False
        i += 1
    if "x" in mode:
        return (chdir or "."), pos
    if "c" in mode or "r" in mode:
        return archive, pos
    return None, pos


def _7z(args):
    pos = [a for a in args if not a.startswith("-")]
    cmd = pos[0].lower() if pos else ""
    if cmd in ("a", "u", "d", "rn"):
        return (pos[1] if len(pos) > 1 else None), pos[2:]
    if cmd in ("x", "e"):
        o = next((a[2:] for a in args if a.lower().startswith("-o") and len(a) > 2), None)
        return (o or "."), pos[1:2]
    return None, pos


def _unzip(args):
    low = [a.lower() for a in args]
    if any(a in ("-l", "-t", "-v", "-z") for a in low):
        return None, []
    pos, dest, i = [], None, 0
    while i < len(args):
        a = args[i]
        if a == "-d" and i + 1 < len(args):
            dest = args[i + 1]
            i += 1
        elif a.startswith("-d") and len(a) > 2:
            dest = a[2:]
        elif not a.startswith("-"):
            pos.append(a)
        i += 1
    return (dest or "."), pos[:1]


def _dest_and_sources(verb, args):
    if verb == "tar":
        return _tar(args)
    if verb in ("7z", "7za", "7zr"):
        return _7z(args)
    if verb == "zip":
        pos = [a for a in args if not a.startswith("-")]
        return (pos[0] if pos else None), pos[1:]
    if verb == "unzip":
        return _unzip(args)
    return _copy_dest(verb, args)


# --------------------------------------------------------------------------
# Segmentos y comandos
# --------------------------------------------------------------------------
def inline_code(verb, args):
    """Devuelve el texto de -c / -Command / -EncodedCommand / /c de una shell."""
    for idx, a in enumerate(args):
        la = a.lower()
        if verb in ("powershell", "pwsh") and la in ("-encodedcommand", "-ec", "-enc", "-e"):
            if idx + 1 >= len(args):
                raise Deny("-EncodedCommand sin contenido.")
            try:
                return base64.b64decode(args[idx + 1]).decode("utf-16-le")
            except Exception:
                raise Deny("-EncodedCommand no se puede decodificar para analizarlo.")
        if verb in ("powershell", "pwsh") and la in ("-command", "-c"):
            return " ".join(args[idx + 1:])
        if verb == "cmd" and la in ("/c", "/k", "/r"):
            return " ".join(args[idx + 1:])
        if verb in ("bash", "sh", "zsh", "dash", "ksh", "fish") and la == "-c":
            return " ".join(args[idx + 1:])
    if verb == "wsl":
        return " ".join(args)
    return None


FEEDERS = {"echo", "printf", "write-output", "write-host", "cat", "type", "get-content", "gc"}


def _runs_hook_script(args, cwd):
    """python [-flags] <script de .claude/hooks> [args sin rutas protegidas]: ejecutar
    el propio hook (p. ej. para probarlo) no lo modifica."""
    idx = next((i for i, a in enumerate(args) if not a.startswith("-")), None)
    if idx is None:
        return False
    script = _resolve(args[idx], cwd)
    if script is None or not script.endswith(".py") or not script.startswith(HOOKS + "/"):
        return False
    others = [a for i, a in enumerate(args) if i != idx and not a.startswith("-")]
    return not any(prot_kind(a, cwd) for a in others)


def _python_readonly(args, cwd):
    """python -m json.tool [archivo] (solo lee; con dos archivos escribiria el segundo) o un
    script de solo lectura listado en readonly_scripts.txt cuyo SHA-256 coincide."""
    i = 0
    while i < len(args) and args[i].startswith("-") and args[i] != "-m":
        i += 1
    if i + 1 < len(args) and args[i] == "-m":
        if args[i + 1].lower() == "json.tool":
            pos, skip = [], False
            for a in args[i + 2:]:
                if skip:
                    skip = False
                elif a in ("--indent",):
                    skip = True
                elif not a.startswith("-"):
                    pos.append(a)
            return len(pos) <= 1
        return False
    idx = next((k for k, a in enumerate(args) if not a.startswith("-")), None)
    if idx is None:
        return False
    script = _resolve(args[idx], cwd)
    want = SCRIPT_ALLOW.get(script) if script else None
    if not want:
        return False
    try:
        got = hashlib.sha256(Path(script).read_bytes()).hexdigest()
    except Exception:
        return False
    return got == want


LITERAL_ARGS = re.compile(
    r"""\s*(?:'[^'$]*'|"[^"$`]*")\s*(?:,\s*(?:'[^'$]*'|"[^"$`]*"|-?\d+|\$true|\$false|"""
    r"""\[[\w.]+\]::\w+)\s*)*""", re.I)


def _is_dynamic(a):
    """True si el argumento depende de una variable o expansion que el hook no puede resolver."""
    return bool(DYNAMIC.search(SAFE_VARS.sub("", a))) or bool(re.match(r"^@[A-Za-z_(]", a))


def _lister_paths(verb, args, cwd="."):
    """(rutas, es_recursivo) de una orden de listado. Si la recursion parte de un patron sin
    directorio (p. ej. '*.txt') se anade el directorio actual como origen."""
    paths, rec, i = [], False, 0
    if verb in ("find", "fd"):
        for a in args:
            if a.startswith("-") or a in ("(", "!", ")"):
                break
            paths.append(a)
        return (paths or ["."]), True
    value_flags = {"-filter", "-include", "-exclude", "-depth", "-attributes"}
    while i < len(args):
        a, la = args[i], args[i].lower()
        if la in ("-path", "-literalpath") and i + 1 < len(args):
            paths.append(args[i + 1])
            i += 2
            continue
        if la in value_flags:
            i += 2
            continue
        if la in ("-recurse", "-r", "-s", "--recursive") or (
                a.startswith("-") and not a.startswith("--") and "R" in a):
            rec = True
        elif not a.startswith("-"):
            paths.append(a)
        i += 1
    def dir_like(p):
        if "/" in p or "\\" in p:
            return True
        r = _resolve(p, cwd)
        return bool(r) and not any(c in r for c in "*?[") and os.path.isdir(r)
    if not paths or (rec and not any(dir_like(p) for p in paths)):
        paths.append(".")
    return paths, rec


def _chain_verdict(chain, cwd):
    """Que se sabe de las rutas que entrega una tuberia: 'deny' si nace de un listado que
    contiene rutas protegidas, 'ok' si todos sus orígenes son listados seguros, 'ask' si no
    se puede saber."""
    listed = other = False
    for s in chain:
        v, a = resolve_verb(s.tokens)
        if v in LISTERS:
            listed = True
            paths, _rec = _lister_paths(v, a, cwd)
            for p in paths:
                if _is_dynamic(p):
                    other = True
                elif prot_kind(p, cwd) in ("direct", "ancestor"):
                    return "deny"
        elif v is None or v in PASSTHRU or v in HARMLESS_KW or v.startswith("$"):
            continue
        else:
            other = True
    return "ask" if (other or not listed) else "ok"


def _gate(seg, verb, args, cwd, ctx, chain, consumer):
    """Orden destructiva/de escritura: si su destino no se puede resolver (variable, xargs,
    rutas que llegan por tuberia) no se da por seguro: confirmacion, o bloqueo si el origen
    es un listado que incluye rutas protegidas."""
    idx = len(seg.tokens) - len(args) - 1
    xargs = any(verb_name(t) == "xargs" for t in seg.tokens[:max(idx, 0)])
    positional = [a for a in args if not a.startswith("-") and not re.match(r"^/[A-Za-z?]$", a)]
    dyn = [a for a in args if _is_dynamic(a)]
    fed = xargs or (consumer and not positional)
    for sub in seg.subs:      # rm $(find . -name x): el origen de las rutas es la sustitucion
        if _chain_verdict(Lexer(sub).run(), cwd) == "deny":
            raise Deny("%s recibe sus rutas de una sustitucion que lista %s (o un directorio "
                       "que las contiene): bloqueado." % (verb, PROT_DESC))
    if not (dyn or fed):
        return
    if ctx.loop_anc and dyn:
        raise Deny("%s con un destino variable dentro de un bucle que recorre un directorio "
                   "o comodin que contiene %s: bloqueado." % (verb, PROT_DESC))
    v = _chain_verdict(chain, cwd) if chain else "ask"
    if v == "deny":
        raise Deny("%s recibe sus rutas de un listado que incluye %s (o un directorio que las "
                   "contiene): bloqueado." % (verb, PROT_DESC))
    if v == "ok" and all(PIPE_VAR.match(a) or not _is_dynamic(a) for a in args):
        return
    ctx.asks.append("%s con un destino que no se puede resolver (variable, tuberia, xargs)" % verb)


def _allowlisted_script(args, cwd):
    idx = next((k for k, a in enumerate(args) if not a.startswith("-")), None)
    if idx is None:
        return False
    script = _resolve(args[idx], cwd)
    want = SCRIPT_ALLOW.get(script) if script else None
    if not want:
        return False
    try:
        return hashlib.sha256(Path(script).read_bytes()).hexdigest() == want
    except Exception:
        return False


def _code_runner(seg, verb, args, cwd):
    """Descripcion si la orden ejecuta codigo que el hook no puede ver (scripts, interpretes,
    compiladores de tareas, instaladores); None en otro caso."""
    if args and all(a.lower() in VERSION_ONLY for a in args):
        return None
    if seg.first_quoted and not seg.after_amp:
        return None     # una cadena al inicio no es una orden (el operador & se trata en _scan_text)
    if _allowlisted_script(args, cwd):
        return None
    idx = len(seg.tokens) - len(args) - 1
    raw0 = _posix(seg.tokens[idx]) if 0 <= idx < len(seg.tokens) else verb
    if verb in PYTHONS:
        low = [a.lower() for a in args]
        if "-m" in low and low.index("-m") + 1 < len(low) and low[low.index("-m") + 1] == "pytest":
            return None     # cubierto por la regla allow de settings.json
        return "python"
    if verb in SHELLS:
        return "%s ejecutando un script" % verb
    if verb in CODE_RUNNERS:
        return verb
    if verb in INSTALLERS:
        return "instalador %s" % verb
    if verb == "dotnet":
        sub = next((a.lower() for a in args if not a.startswith("-")), "")
        return ("dotnet " + sub) if sub in DOTNET_RUN else None
    known = (READONLY | DESTRUCTIVE | COPY | ARCHIVERS | WRITERS | CD
             | {"git", "gh", "find", "sed", "certutil", "awk", "xargs", "cmd", "wsl"})
    pathlike = bool(re.match(r"^(?:\.{1,2}/|/|[A-Za-z]:/|~/)", raw0)) or (
        "/" in raw0 and not re.search(r"[\[\]()'\"$:@]", raw0))
    if verb not in known and (raw0.lower().endswith(SCRIPT_EXT) or pathlike):
        return "ejecutable o script %s" % raw0
    return None


def _scan_text(text, cwd, ctx):
    """Patrones de PowerShell que escriben o ejecutan codigo sin pasar por un verbo analizable."""
    for m in DOTNET_WRITE.finditer(text):
        tok = m.group(0)
        if not tok.startswith("::") or re.match(r"::\s*[('\"$]", tok):
            ctx.asks.append("metodo .NET que escribe/borra o carga codigo (%s)" % tok.strip())
            continue
        j = m.end()
        j += len(text[j:]) - len(text[j:].lstrip())
        if j >= len(text) or text[j] != "(":
            ctx.asks.append("metodo .NET de escritura sin argumentos literales (%s)" % tok)
            continue
        argtxt = text[j + 1:_match_paren(text, j)]
        if not LITERAL_ARGS.fullmatch(argtxt):
            ctx.asks.append("metodo .NET de escritura con una ruta que no es literal (%s)" % tok)
        else:
            first = re.match(r"""\s*['"]([^'"]*)['"]""", argtxt)
            if first and prot_kind(first.group(1), cwd) == "direct":
                raise Deny("Metodo .NET de escritura sobre %s bloqueado." % PROT_DESC)
    if CALL_OP.search(text):
        ctx.asks.append("operador de llamada & con un comando o script calculado")


def check_segment(seg, cwd, depth, ctx, tainted, pipe_docs, chain=()):
    verb, args = resolve_verb(seg.tokens)
    hit, anc, words = classify(seg.tokens, seg.redirects, seg.raw, cwd)
    if hit or words:
        ctx.mentioned = True
    mention = hit or words

    for r in seg.redirects:
        if prot_kind(r, cwd) == "direct" or _has_mention(r):
            raise Deny("Redireccion de salida hacia %s bloqueada." % PROT_DESC)
        if ask_kind(r, cwd):
            ctx.asks.append(ASK_DESC)
        elif r.lower() not in NULL_DEVICES and _is_dynamic(r):
            ctx.asks.append("redireccion de salida a un destino que no se puede resolver")

    if verb in ("for", "foreach", "select") and (
            hit or anc or any(_chain_verdict(Lexer(s).run(), cwd) == "deny" for s in seg.subs)):
        ctx.loop_anc = True
    if verb is None or verb in HARMLESS_KW:
        return cwd, mention

    # Expresiones de PowerShell que no son ordenes: cadenas y variables.
    if ctx.tool == "PowerShell" and not seg.after_amp:
        # Una cadena al inicio de una sentencia es un dato (no una orden); si va por una
        # tuberia, check_command la entrega a la orden siguiente (p. ej. | Invoke-Expression).
        # Dentro de parentesis ('open') son los argumentos de una llamada: se analizan.
        if seg.first_quoted and seg.sep != "open":
            return cwd, mention
        if verb.startswith("$"):
            lead = args[0].lower() if args else ""
            rest = args[1:]
            if (lead in ASSIGN_OPS or lead == "in") and len(rest) >= 2:
                sub = Seg("seq")
                sub.tokens = rest
                sub.raw = " ".join(rest)
                if lead == "in" and _chain_verdict([sub], cwd) == "deny":
                    ctx.loop_anc = True
                return check_segment(sub, cwd, depth, ctx, tainted, pipe_docs, chain)
            return cwd, mention

    if verb in SHELLS:
        code = inline_code(verb, args)
        if code is not None:
            check_command(code, cwd, depth + 1, ctx)
        else:
            # Sin -c: la shell lee el codigo de stdin (heredoc o tuberia).
            for h in list(seg.heredocs) + pipe_docs:
                check_command(h, cwd, depth + 1, ctx)
            if seg.sep == "pipe" and any(
                    resolve_verb(s.tokens)[0] not in FEEDERS for s in chain):
                ctx.asks.append("%s ejecuta codigo recibido por tuberia desde una orden que lo "
                                "transforma o descarga (no se puede analizar)" % verb)
        if code is not None or seg.heredocs or pipe_docs:
            return cwd, mention
    if verb in EVALS:
        check_command(" ".join(args), cwd, depth + 1, ctx)
        for h in pipe_docs:
            check_command(h, cwd, depth + 1, ctx)
        if not pipe_docs and any(_is_dynamic(a) or "(" in a for a in args):
            ctx.asks.append("%s con codigo calculado en tiempo de ejecucion" % verb)
        if any(_is_dynamic(h) or re.search(r"\s[+*]\s|\s-f\s|\s-join\s", h, re.I) for h in pipe_docs):
            ctx.asks.append("%s recibe por tuberia un texto construido en tiempo de ejecucion" % verb)

    if verb == "git":
        check_git(args, cwd, seg, ctx)
        return cwd, mention
    if verb == "gh":
        check_gh(args, ctx)
        return cwd, mention

    if verb in CD:
        target = next((a for a in args if not a.startswith("-")), "~")
        new = _resolve(target, cwd)
        if new is None:
            return cwd, mention
        if inside_protected(new):
            raise Deny("Cambiar el directorio a %s esta bloqueado: permitiria "
                       "escrituras con rutas relativas." % PROT_DESC)
        return new, mention

    if verb == "find" and not find_writes(args):
        return cwd, mention
    if verb == "find":
        verb = "rm"
    if verb in READONLY or _special_readonly(verb, args):
        return cwd, mention
    if verb in PYTHONS and (_runs_hook_script(args, cwd) or _python_readonly(args, cwd)):
        return cwd, mention

    # A partir de aqui el comando puede escribir: escritores y desconocidos.
    if inside_protected(cwd):
        raise Deny("El directorio actual esta dentro de %s: comandos que pueden "
                   "escribir bloqueados." % PROT_DESC)

    runner = _code_runner(seg, verb, args, cwd)
    if runner:
        ctx.asks.append("ejecutar codigo que el hook no puede analizar (%s); solo los scripts "
                        "de solo lectura de readonly_scripts.txt (con SHA-256) se exceptuan" % runner)
    inplace = verb in ("sed", "perl", "ruby", "awk") and any(
        a == "--in-place" or a.startswith("--in-place=") or INPLACE.match(a)
        or (verb == "awk" and a == "inplace") for a in args)
    if inplace:
        if hit or anc or words:
            raise Deny("Edicion in situ (%s -i) sobre %s (o un directorio que las contiene) "
                       "bloqueada." % (verb, PROT_DESC))
        _gate(seg, verb, args, cwd, ctx, chain, False)
    elif verb in WRITERS:
        _gate(seg, verb, args, cwd, ctx, chain, False)

    if verb in COPY or verb in ARCHIVERS:
        dest, pos = _dest_and_sources(verb, args)
        if dest is not None:
            if prot_kind(dest, cwd) == "direct" or _has_mention(dest):
                raise Deny("Escribir/copiar/comprimir/extraer HACIA %s esta bloqueado "
                           "(se permite usarlas como origen)." % PROT_DESC)
            if ask_kind(dest, cwd):
                ctx.asks.append(ASK_DESC)
            if _is_dynamic(dest):
                ctx.asks.append("%s hacia un destino que no se puede resolver" % verb)
            elif prot_kind(dest, cwd) == "ancestor":
                low = [a.lower() for a in args]
                # un archivo comprimido nunca es 'ancestro': si el destino lo es, se extrae en el
                extracts = verb in ("expand-archive", "unzip", "7z", "7za", "7zr", "tar")
                tree_src = any(a in ("-recurse", "-r", "/e", "/s", "/mir", "-a", "-ar")
                               for a in low) or any(
                    (lambda r: bool(r) and os.path.isdir(r))(_resolve(p, cwd)) for p in pos)
                if extracts or (verb in COPY and tree_src):
                    ctx.asks.append("%s hacia un directorio que contiene rutas protegidas: el "
                                    "contenido (archivo o arbol) podria sobrescribirlas" % verb)
        low = [a.lower() for a in args]
        if any(a in ("/mov", "/move", "--remove-source-files", "--remove-files", "-sdel")
               for a in low):
            if any(prot_kind(p, cwd) == "direct" for p in pos):
                raise Deny("Mover/eliminar origenes protegidos esta bloqueado.")
        return cwd, mention

    if verb in DESTRUCTIVE:
        if hit or anc or words:
            raise Deny("Borrar, mover o cambiar atributos/permisos de %s (o de un "
                       "directorio que las contiene) esta bloqueado." % PROT_DESC)
        positional = [a for a in args if not a.startswith("-") and not re.match(r"^/[A-Za-z?]$", a)]
        dynamic = any(DYNAMIC.search(a) for a in positional)
        if (tainted or ctx.mentioned or ctx.text_words) and (dynamic or not positional):
            raise Deny("Comando de borrado/movimiento encadenado con una lectura de "
                       "una ruta protegida: bloqueado por precaucion.")
        if verb in ("del", "erase") and any(a.lower() == "/s" for a in args):
            # cmd: 'del /s <nombre>' borra ese nombre en todo el arbol que cuelga del directorio
            for a in [x for x in args if not x.startswith("/")] or ["."]:
                d = _posix(a).rsplit("/", 1)[0] if "/" in _posix(a) else "."
                if prot_kind(d or "/", cwd) in ("direct", "ancestor"):
                    raise Deny("del /s en un directorio que contiene %s: bloqueado." % PROT_DESC)
        _gate(seg, verb, args, cwd, ctx, chain, True)
        if asks_any(args, cwd):
            ctx.asks.append(ASK_DESC)
        return cwd, mention

    # Desconocido: si menciona una ruta protegida no se puede demostrar que sea de solo lectura.
    if mention:
        raise Deny("El comando '%s' menciona %s y no se puede comprobar que sea de "
                   "solo lectura. Usa herramientas de lectura (ls, cat, Get-Content, "
                   "Get-FileHash, git status...)." % (verb, PROT_DESC))
    if asks_any(args, cwd):
        ctx.asks.append(ASK_DESC)
    return cwd, mention


def check_command(text, cwd, depth, ctx):
    if depth > MAX_DEPTH:
        raise Deny("Anidamiento de comandos demasiado profundo para analizarlo con seguridad.")
    tainted = False
    pipe_docs = []  # texto que una tuberia/heredoc entrega a la siguiente orden
    chain = []      # segmentos de la misma tuberia (origen de las rutas que recibe una orden)
    writer_open = False   # la sentencia actual es una orden que escribe/borra/copia
    _scan_text(text, cwd, ctx)
    for seg in Lexer(text).run():
        for sub in seg.subs:
            check_command(sub, cwd, depth + 1, ctx)
        if seg.sep in ("start", "seq"):
            tainted = False
            pipe_docs = []
            chain = []
        if seg.sep in ("start", "seq", "pipe"):
            writer_open = False
        elif seg.sep == "open" and writer_open and ctx.tool == "PowerShell":
            ctx.asks.append("argumento calculado por una expresion en una orden que escribe o borra")
        cwd, mention = check_segment(seg, cwd, depth, ctx, tainted, pipe_docs, tuple(chain))
        wv, _wa = resolve_verb(seg.tokens)
        if wv in DESTRUCTIVE or wv in WRITERS or wv in COPY or wv in EVALS:
            writer_open = True
        chain.append(seg)
        tainted = tainted or mention
        pipe_docs = pipe_docs + list(seg.heredocs)
        verb, args = resolve_verb(seg.tokens)
        if verb in FEEDERS and args:
            pipe_docs.append(" ".join(a for a in args if not a.startswith("-")))
        elif ctx.tool == "PowerShell" and seg.first_quoted and not seg.after_amp \
                and seg.sep != "open":
            pipe_docs.append(" ".join(seg.tokens))


# --------------------------------------------------------------------------
# Punto de entrada
# --------------------------------------------------------------------------
def main():
    try:
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    ctx = Ctx()
    try:
        raw = sys.stdin.buffer.read()
        data = json.loads(raw.decode("utf-8-sig"))   # tolera un BOM inicial (PowerShell lo anade)
        if not isinstance(data, dict):
            raise ValueError("el JSON de entrada no es un objeto")
        tool = data.get("tool_name")
        if not isinstance(tool, str):
            raise ValueError("falta tool_name")
        if tool not in FILE_TOOLS and tool not in SHELL_TOOLS:
            return 0
        ctx.tool = tool
        configure()
        cwd_raw = data.get("cwd")
        cwd = _abs(cwd_raw) if isinstance(cwd_raw, str) and cwd_raw else _abs(PROJECT_DIR)
        if tool in FILE_TOOLS:
            check_file_tool(tool, data.get("tool_input"), cwd, ctx)
        else:
            tin = data.get("tool_input")
            cmd = tin.get("command") if isinstance(tin, dict) else None
            if not isinstance(cmd, str):
                raise ValueError("falta tool_input.command")
            if len(cmd) > MAX_LEN:
                raise Deny("Comando demasiado largo para analizarlo con seguridad.")
            ctx.text_words = _mentions_text(cmd)
            check_command(cmd, cwd, 0, ctx)
    except Deny as e:
        print("BLOQUEADO por .claude/hooks/guard.py: %s" % e, file=sys.stderr)
        return 2
    except Exception as e:  # entrada malformada o error interno: fallar cerrado
        print("BLOQUEADO por .claude/hooks/guard.py (fallo seguro): %s: %s"
              % (type(e).__name__, e), file=sys.stderr)
        return 2
    if ctx.asks:
        print(json.dumps({"hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "ask",
            "permissionDecisionReason":
                "Requiere confirmacion expresa: %s." % "; ".join(sorted(set(ctx.asks))),
        }}))
    return 0


if __name__ == "__main__":
    _rc = main()
    _OK[0] = True        # solo se llega aqui si el archivo esta completo
    sys.exit(_rc)
