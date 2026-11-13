"""mitmproxy addon for fifazf.exe: logs ALL HTTP flows and raw TCP messages (hex) with secrets masked."""
import json, re, os, time, ctypes
import ctypes.wintypes as wt
from mitmproxy import http, tcp

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "game_capture_redacted.txt")
SENSITIVE = re.compile(r"pass|pwd|token|session|secret|cookie|auth|sign|key|ticket|otp|sid|hwid", re.I)
HEXTOK = re.compile(r"[0-9a-fA-F]{32,}")

def shape(v):
    s = str(v); return f"<REDACTED len={len(s)}>"

def scrub(s):  # long hex strings anywhere (tokens) -> placeholder
    return HEXTOK.sub(lambda m: f"<HEX len={len(m.group(0))}>", s)

def mask(o, key=""):
    if isinstance(o, dict): return {k: mask(v, k) for k, v in o.items()}
    if isinstance(o, list): return [mask(v, key) for v in o]
    if o is None or isinstance(o, bool): return o
    return shape(o) if SENSITIVE.search(key) else o

def body_text(msg, limit=400000):
    t = msg.get_text(strict=False) or ""
    t = t[:limit]
    try: return json.dumps(mask(json.loads(t)), ensure_ascii=False, indent=2)
    except Exception: return scrub(t)

def hdrs(h):
    return "\n".join(f"{k}: {shape(v) if SENSITIVE.search(k) else v}" for k, v in h.items(multi=True))

def w(s):
    with open(OUT, "a", encoding="utf-8") as f: f.write(s + "\n")

# ---- hand the live game session over to FO3SessionTool ------------------------------------------
# Only the two session cookies, encrypted with Windows DPAPI for the current user (same scheme as the
# tool's own session file), so nobody else on the machine can read them. Nothing is written to the log.
GAME_PORT = 18090   # the game API port; the host moved before (72.62.247.145 -> 216.236.2.193), so match the port, not the IP
COOKIE_FILE = os.path.join(os.environ.get("APPDATA", "."), "FO3SessionTool", "game_cookie.dat")
ENTROPY = b"FO3SessionTool/gamecookie/v1"
WANTED = ("connect.sid", "fo3sid")
_last_cookie = None

class _Blob(ctypes.Structure):
    _fields_ = [("cbData", wt.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

def _blob(data):
    buf = ctypes.create_string_buffer(data, len(data))
    return _Blob(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf

def _protect(data, entropy):
    b_in, k1 = _blob(data); b_ent, k2 = _blob(entropy); b_out = _Blob()
    if not ctypes.windll.crypt32.CryptProtectData(ctypes.byref(b_in), None, ctypes.byref(b_ent), None, None, 0, ctypes.byref(b_out)):
        raise ctypes.WinError()
    try: return ctypes.string_at(b_out.pbData, b_out.cbData)
    finally: ctypes.windll.kernel32.LocalFree(b_out.pbData)

def share_session(flow: http.HTTPFlow):
    global _last_cookie
    try:
        if flow.request.port != GAME_PORT or "/lang-" not in flow.request.path: return
        header = flow.request.headers.get("Cookie", "")
        found = {}
        for part in header.split(";"):
            k, _, v = part.strip().partition("=")
            if k in WANTED and v: found[k] = v
        if not found or found == _last_cookie: return
        os.makedirs(os.path.dirname(COOKIE_FILE), exist_ok=True)
        blob = _protect(json.dumps({"cookies": found, "at": time.strftime("%Y-%m-%dT%H:%M:%S")}).encode("utf-8"), ENTROPY)
        with open(COOKIE_FILE, "wb") as f: f.write(blob)
        _last_cookie = found
        w(f"[session shared with FO3SessionTool: {', '.join(sorted(found))}]")
    except Exception as e:
        w(f"[session share failed: {type(e).__name__}]")

def response(flow: http.HTTPFlow):
    share_session(flow)
    rq, rs = flow.request, flow.response
    w("=" * 70 + f"\nHTTP {time.strftime('%H:%M:%S')}  {rq.method} {scrub(rq.pretty_url)}\n{hdrs(rq.headers)}\n\n{body_text(rq)}\n" +
      f"{'-'*30}\n<- {rs.status_code} {rs.reason}  ({rs.headers.get('content-type','')})\n\n{body_text(rs)}\n")

def tcp_message(flow: tcp.TCPFlow):
    m = flow.messages[-1]
    d = m.content
    w(f"TCP {time.strftime('%H:%M:%S')} {flow.server_conn.address[0]}:{flow.server_conn.address[1]} "
      f"{'C->S' if m.from_client else 'S->C'} len={len(d)}\n  hex: {d[:200].hex()}\n  asc: {re.sub(r'[^ -~]', '.', d[:200].decode('latin1'))}")
