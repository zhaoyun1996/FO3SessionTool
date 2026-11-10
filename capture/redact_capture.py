"""mitmproxy addon: logs 3sea.online traffic to capture_redacted.txt with secrets masked.
Keeps field names, structure, and value shape (length/charset) so the protocol can be reverse-engineered."""
import json, re, os
from mitmproxy import http

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "capture_redacted.txt")
SENSITIVE = re.compile(r"pass|pwd|token|session|secret|cookie|auth|sign|key|ticket|otp|sid", re.I)
HOSTS = ("3sea.online",)

def shape(v):
    s = str(v)
    kind = "hex" if re.fullmatch(r"[0-9a-fA-F]+", s) else "b64/url" if re.fullmatch(r"[A-Za-z0-9+/=_\-\.]+", s) else "text"
    return f"<REDACTED len={len(s)} {kind}>"

def mask(o, key=""):
    if isinstance(o, dict):
        return {k: mask(v, k) for k, v in o.items()}
    if isinstance(o, list):
        return [mask(v, key) for v in o]
    if o is None or isinstance(o, bool):
        return o
    return shape(o) if SENSITIVE.search(key) else o

def mask_text(body, ctype):
    if not body: return ""
    try:
        if "json" in ctype or body.lstrip()[:1] in "{[":
            return json.dumps(mask(json.loads(body)), ensure_ascii=False, indent=2)
    except Exception: pass
    if "x-www-form-urlencoded" in ctype:
        parts = []
        for kv in body.split("&"):
            k, _, v = kv.partition("=")
            parts.append(f"{k}={shape(v) if SENSITIVE.search(k) else v}")
        return "&".join(parts)
    return body[:2000]

def hdrs(h):
    out = []
    for k, v in h.items(multi=True):
        if SENSITIVE.search(k):
            if k.lower() in ("cookie", "set-cookie"):
                v = "; ".join(f"{c.split('=')[0]}={shape(c.split('=',1)[1]) if '=' in c else ''}" for c in v.split(";"))
            else: v = shape(v)
        out.append(f"{k}: {v}")
    return "\n".join(out)

def response(flow: http.HTTPFlow):
    if not flow.request.pretty_host.endswith(HOSTS): return
    rq, rs = flow.request, flow.response
    txt = [
        "=" * 70,
        f"{rq.method} {rq.pretty_url}  HTTP/{rq.http_version}",
        hdrs(rq.headers), "",
        mask_text(rq.get_text(strict=False) or "", rq.headers.get("content-type", "")),
        "-" * 30,
        f"<- {rs.status_code} {rs.reason}",
        hdrs(rs.headers), "",
        mask_text((rs.get_text(strict=False) or "")[:20000], rs.headers.get("content-type", "")),
        "",
    ]
    with open(OUT, "a", encoding="utf-8") as f:
        f.write("\n".join(txt) + "\n")
