"""QR code generator for the public link of Towers of Defencopolis (no third-party QR service, no redirects).

Usage (Python 3, standard library + Pillow for PNG; OpenCV is used to verify the result when available):
    python -I make_qr.py https://your-public-url/ [--out DIR] [--ecc L|M|Q|H] [--module 40] [--name towers-of-defencopolis]
    python -I make_qr.py --selftest

- Only public HTTPS URLs are accepted (no http://, localhost or private IP addresses such as 192.168.x.x).
- Writes <name>.png (high resolution, white quiet zone of 4 modules) and <name>.svg (vector).
- Decodes the PNG with OpenCV and fails if the decoded text is not exactly the URL.

The encoder follows ISO/IEC 18004 (byte mode, versions 1-10, Reed-Solomon over GF(256), 8 masks with the
standard penalty rules); it is written from the specification so the project has no extra dependency.
"""
import argparse
import ipaddress
import os
import sys
import tempfile
from urllib.parse import urlparse

# --------------------------------------------------------------------------------------------- tables (versions 1-10)
# ECC -> version -> (ec codewords per block, [(block count, data codewords per block), ...])
EC_TABLE = {
    "L": {1: (7, [(1, 19)]), 2: (10, [(1, 34)]), 3: (15, [(1, 55)]), 4: (20, [(1, 80)]), 5: (26, [(1, 108)]),
          6: (18, [(2, 68)]), 7: (20, [(2, 78)]), 8: (24, [(2, 97)]), 9: (30, [(2, 116)]), 10: (18, [(2, 68), (2, 69)])},
    "M": {1: (10, [(1, 16)]), 2: (16, [(1, 28)]), 3: (26, [(1, 44)]), 4: (18, [(2, 32)]), 5: (24, [(2, 43)]),
          6: (16, [(4, 27)]), 7: (18, [(4, 31)]), 8: (22, [(2, 38), (2, 39)]), 9: (22, [(3, 36), (2, 37)]),
          10: (26, [(4, 43), (1, 44)])},
    "Q": {1: (13, [(1, 13)]), 2: (22, [(1, 22)]), 3: (18, [(2, 17)]), 4: (26, [(2, 24)]), 5: (18, [(2, 15), (2, 16)]),
          6: (24, [(4, 19)]), 7: (18, [(2, 14), (4, 15)]), 8: (22, [(4, 18), (2, 19)]), 9: (20, [(4, 16), (4, 17)]),
          10: (24, [(6, 19), (2, 20)])},
    "H": {1: (17, [(1, 9)]), 2: (28, [(1, 16)]), 3: (22, [(2, 13)]), 4: (16, [(4, 9)]), 5: (22, [(2, 11), (2, 12)]),
          6: (28, [(4, 15)]), 7: (26, [(4, 13), (1, 14)]), 8: (26, [(4, 14), (2, 15)]), 9: (24, [(4, 12), (4, 13)]),
          10: (28, [(6, 15), (2, 16)])},
}
FORMAT_BITS = {"L": 1, "M": 0, "Q": 3, "H": 2}
ALIGNMENT = {1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34], 7: [6, 22, 38], 8: [6, 24, 42],
             9: [6, 26, 46], 10: [6, 28, 50]}
REMAINDER_BITS = {1: 0, 2: 7, 3: 7, 4: 7, 5: 7, 6: 7, 7: 0, 8: 0, 9: 0, 10: 0}


# --------------------------------------------------------------------------------------------- Reed-Solomon
def gf_mul(x, y):
    z = 0
    for i in reversed(range(8)):
        z = (z << 1) ^ ((z >> 7) * 0x11D)
        z ^= ((y >> i) & 1) * x
    return z


def rs_divisor(degree):
    result = [0] * (degree - 1) + [1]
    root = 1
    for _ in range(degree):
        for j in range(degree):
            result[j] = gf_mul(result[j], root)
            if j + 1 < degree:
                result[j] ^= result[j + 1]
        root = gf_mul(root, 0x02)
    return result


def rs_remainder(data, divisor):
    result = [0] * len(divisor)
    for b in data:
        factor = b ^ result.pop(0)
        result.append(0)
        for i, coef in enumerate(divisor):
            result[i] ^= gf_mul(coef, factor)
    return result


# --------------------------------------------------------------------------------------------- encoding
def data_capacity(version, ecc):
    ec, groups = EC_TABLE[ecc][version]
    return sum(n * d for n, d in groups)


def encode_codewords(payload, version, ecc):
    cap = data_capacity(version, ecc)
    bits = []

    def put(value, length):
        bits.extend((value >> i) & 1 for i in reversed(range(length)))

    put(0b0100, 4)                                   # byte mode
    put(len(payload), 8 if version < 10 else 16)     # character count
    for b in payload:
        put(b, 8)
    put(0, min(4, cap * 8 - len(bits)))              # terminator
    put(0, (-len(bits)) % 8)                         # byte alignment
    data = [int("".join(map(str, bits[i:i + 8])), 2) for i in range(0, len(bits), 8)]
    pad = 0xEC
    while len(data) < cap:
        data.append(pad)
        pad ^= 0xEC ^ 0x11
    ec, groups = EC_TABLE[ecc][version]
    divisor = rs_divisor(ec)
    blocks, k = [], 0
    for count, size in groups:
        for _ in range(count):
            block = data[k:k + size]
            k += size
            blocks.append((block, rs_remainder(block, divisor)))
    out = []
    for i in range(max(len(b[0]) for b in blocks)):
        for block, _ in blocks:
            if i < len(block):
                out.append(block[i])
    for i in range(ec):
        for _, ecw in blocks:
            out.append(ecw[i])
    return out


def choose_version(payload, ecc):
    for v in range(1, 11):
        header = 4 + (8 if v < 10 else 16)
        if header + 8 * len(payload) <= data_capacity(v, ecc) * 8:
            return v
    raise ValueError("URL too long for this generator (versions 1-10): shorten it")


# --------------------------------------------------------------------------------------------- matrix
class Matrix:
    def __init__(self, version):
        self.version = version
        self.size = version * 4 + 17
        self.mod = [[False] * self.size for _ in range(self.size)]
        self.fn = [[False] * self.size for _ in range(self.size)]

    def set_fn(self, x, y, dark):
        self.mod[y][x] = dark
        self.fn[y][x] = True

    def draw_function_patterns(self):
        s = self.size
        for i in range(s):
            self.set_fn(6, i, i % 2 == 0)
            self.set_fn(i, 6, i % 2 == 0)
        for cx, cy in ((3, 3), (s - 4, 3), (3, s - 4)):
            for dy in range(-4, 5):
                for dx in range(-4, 5):
                    x, y = cx + dx, cy + dy
                    if 0 <= x < s and 0 <= y < s:
                        self.set_fn(x, y, max(abs(dx), abs(dy)) not in (2, 4))
        pos = ALIGNMENT[self.version]
        n = len(pos)
        for i in range(n):
            for j in range(n):
                if (i == 0 and j == 0) or (i == 0 and j == n - 1) or (i == n - 1 and j == 0):
                    continue
                for dy in range(-2, 3):
                    for dx in range(-2, 3):
                        self.set_fn(pos[i] + dx, pos[j] + dy, max(abs(dx), abs(dy)) != 1)
        self.draw_format(FORMAT_BITS["M"], 0)  # reserve (overwritten later)
        if self.version >= 7:
            rem = self.version
            for _ in range(12):
                rem = (rem << 1) ^ ((rem >> 11) * 0x1F25)
            bits = self.version << 12 | rem
            for i in range(18):
                bit = (bits >> i) & 1 == 1
                a, b = s - 11 + i % 3, i // 3
                self.set_fn(a, b, bit)
                self.set_fn(b, a, bit)

    def draw_format(self, ecc_bits, mask):
        data = ecc_bits << 3 | mask
        rem = data
        for _ in range(10):
            rem = (rem << 1) ^ ((rem >> 9) * 0x537)
        bits = (data << 10 | rem) ^ 0x5412
        get = lambda i: (bits >> i) & 1 == 1
        s = self.size
        for i in range(6):
            self.set_fn(8, i, get(i))
        self.set_fn(8, 7, get(6))
        self.set_fn(8, 8, get(7))
        self.set_fn(7, 8, get(8))
        for i in range(9, 15):
            self.set_fn(14 - i, 8, get(i))
        for i in range(8):
            self.set_fn(s - 1 - i, 8, get(i))
        for i in range(8, 15):
            self.set_fn(8, s - 15 + i, get(i))
        self.set_fn(8, s - 8, True)  # dark module

    def draw_codewords(self, data):
        s, i = self.size, 0
        total = len(data) * 8
        right = s - 1
        while right >= 1:
            if right == 6:
                right = 5
            for vert in range(s):
                for j in range(2):
                    x = right - j
                    upward = ((right + 1) & 2) == 0
                    y = s - 1 - vert if upward else vert
                    if not self.fn[y][x] and i < total:
                        self.mod[y][x] = (data[i >> 3] >> (7 - (i & 7))) & 1 == 1
                        i += 1
            right -= 2

    def apply_mask(self, mask):
        f = [lambda x, y: (x + y) % 2 == 0, lambda x, y: y % 2 == 0, lambda x, y: x % 3 == 0,
             lambda x, y: (x + y) % 3 == 0, lambda x, y: (x // 3 + y // 2) % 2 == 0,
             lambda x, y: x * y % 2 + x * y % 3 == 0, lambda x, y: (x * y % 2 + x * y % 3) % 2 == 0,
             lambda x, y: ((x + y) % 2 + x * y % 3) % 2 == 0][mask]
        for y in range(self.size):
            for x in range(self.size):
                if not self.fn[y][x] and f(x, y):
                    self.mod[y][x] = not self.mod[y][x]

    def penalty(self):
        s, m, score = self.size, self.mod, 0
        lines = [m[y] for y in range(s)] + [[m[y][x] for y in range(s)] for x in range(s)]
        for line in lines:
            run, prev = 0, None
            for v in line:
                if v == prev:
                    run += 1
                else:
                    if run >= 5:
                        score += 3 + run - 5
                    run, prev = 1, v
            if run >= 5:
                score += 3 + run - 5
            text = "".join("1" if v else "0" for v in line)
            for pat in ("10111010000", "00001011101"):
                start = text.find(pat)
                while start >= 0:
                    score += 40
                    start = text.find(pat, start + 1)
        for y in range(s - 1):
            for x in range(s - 1):
                if m[y][x] == m[y][x + 1] == m[y + 1][x] == m[y + 1][x + 1]:
                    score += 3
        dark = sum(v for row in m for v in row)
        total = s * s
        score += ((abs(dark * 20 - total * 10) + total - 1) // total - 1) * 10
        return score


def make_qr(text, ecc="M"):
    payload = text.encode("utf-8")
    version = choose_version(payload, ecc)
    data = encode_codewords(payload, version, ecc)
    best, best_score = None, None
    for mask in range(8):
        q = Matrix(version)
        q.draw_function_patterns()
        q.draw_codewords(data)
        q.apply_mask(mask)
        q.draw_format(FORMAT_BITS[ecc], mask)
        score = q.penalty()
        if best_score is None or score < best_score:
            best, best_score = q, score
    return best


# --------------------------------------------------------------------------------------------- output + checks
def check_public_https(url):
    p = urlparse(url)
    if p.scheme != "https":
        raise ValueError("the QR must contain an https:// URL")
    host = (p.hostname or "").lower()
    if not host or host == "localhost" or host.endswith(".local") or "." not in host:
        raise ValueError("the URL must use a public host name, not '" + host + "'")
    try:
        ip = ipaddress.ip_address(host)
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved:
            raise ValueError("private / local IP address: " + host)
    except ValueError as e:
        if "private" in str(e):
            raise
    if any(c.isspace() for c in url):
        raise ValueError("the URL contains spaces")


def write_png(q, path, module, border=4):
    from PIL import Image
    n = q.size + 2 * border
    img = Image.new("L", (n, n), 255)
    px = img.load()
    for y in range(q.size):
        for x in range(q.size):
            if q.mod[y][x]:
                px[x + border, y + border] = 0
    img = img.resize((n * module, n * module), Image.NEAREST)
    img.save(path, optimize=True)
    return img.size


def write_svg(q, path, border=4):
    n = q.size + 2 * border
    parts = []
    for y in range(q.size):
        for x in range(q.size):
            if q.mod[y][x]:
                parts.append("M%d %dh1v1h-1z" % (x + border, y + border))
    svg = ('<?xml version="1.0" encoding="UTF-8"?>\n'
           '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d" shape-rendering="crispEdges">\n'
           '<rect width="100%%" height="100%%" fill="#FFFFFF"/>\n<path d="%s" fill="#000000"/>\n</svg>\n') % (n, n, n * 10, n * 10, "".join(parts))
    with open(path, "w", encoding="utf-8") as f:
        f.write(svg)


def decode(path):
    import cv2
    img = cv2.imread(path)
    for size in (900, 600, 400):
        small = cv2.resize(img, (size, size), interpolation=cv2.INTER_AREA)
        text, _, _ = cv2.QRCodeDetector().detectAndDecode(small)
        if text:
            return text
    return ""


def generate(url, out_dir, name, ecc, module, check=True):
    if check:
        check_public_https(url)
    q = make_qr(url, ecc)
    os.makedirs(out_dir, exist_ok=True)
    png, svg = os.path.join(out_dir, name + ".png"), os.path.join(out_dir, name + ".svg")
    w, h = write_png(q, png, module)
    write_svg(q, svg)
    decoded = decode(png)
    if decoded != url:
        raise RuntimeError("verification failed: decoded '%s' instead of '%s'" % (decoded, url))
    return q.version, (w, h), png, svg


def selftest():
    """Encodes sample URLs into a temporary folder, decodes them with OpenCV and deletes them."""
    samples = ["https://example.org/", "https://towers-of-defencopolis.pages.dev/",
               "https://usuario.itch.io/towers-of-defencopolis?utm_source=qr",
               "https://example.com/" + "leyendas-colombianas/" * 5]
    with tempfile.TemporaryDirectory() as tmp:
        for i, url in enumerate(samples):
            for ecc in ("L", "M", "Q", "H"):
                try:
                    version, size, _, _ = generate(url, tmp, "t%d%s" % (i, ecc), ecc, 12, check=False)
                    print("ok  ecc=%s v%-2d %-70s" % (ecc, version, url))
                except ValueError as e:
                    print("skip ecc=%s %s (%s)" % (ecc, url, e))
    for bad in ("http://example.org/", "https://192.168.10.10:8765/", "https://localhost/"):
        try:
            check_public_https(bad)
            raise SystemExit("selftest: accepted " + bad)
        except ValueError:
            print("rejected %s" % bad)
    print("selftest passed")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("url", nargs="?")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "qr"))
    ap.add_argument("--name", default="towers-of-defencopolis-qr")
    ap.add_argument("--ecc", default="Q", choices=["L", "M", "Q", "H"], help="Q (25%% recovery) suits printed material")
    ap.add_argument("--module", type=int, default=40, help="pixels per module in the PNG")
    ap.add_argument("--selftest", action="store_true")
    a = ap.parse_args()
    if a.selftest:
        selftest()
        return
    if not a.url:
        ap.error("give the public HTTPS URL")
    version, size, png, svg = generate(a.url, os.path.abspath(a.out), a.name, a.ecc, a.module)
    print("QR version %d, ECC %s, PNG %dx%d px" % (version, a.ecc, size[0], size[1]))
    print("verified: decodes exactly to " + a.url)
    print(png)
    print(svg)


if __name__ == "__main__":
    main()
