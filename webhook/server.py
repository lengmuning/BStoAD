import base64, hashlib, hmac, json, os, socketserver, sqlite3, struct, subprocess, threading, time
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit, parse_qs

TOKEN = os.environ['BEISEN_TOKEN']
KEY = base64.b64decode(os.environ['BEISEN_AES_KEY'] + '=', validate=True)
DB = os.environ.get('BEISEN_DB', '/var/lib/beisen-webhook/events.sqlite3')
assert len(TOKEN) == 12 and len(KEY) == 32
BIND = os.environ.get('BEISEN_BIND', '0.0.0.0')
# Intranet pull API for the AD side (BSTOAD). Not mapped by the public gateway; bearer token required.
FEED_PORT = int(os.environ.get('BEISEN_FEED_PORT', '8601'))
FEED_TOKEN = os.environ.get('BEISEN_FEED_TOKEN', '')
RETENTION_DAYS = int(os.environ.get('BEISEN_RETENTION_DAYS', '30'))

class Server(ThreadingHTTPServer):
    # HTTPServer.server_bind() calls socket.getfqdn() between bind and listen. With the public DNS on this
    # host that reverse lookup of the intranet IP times out (~11 s per port), refusing connections meanwhile.
    def server_bind(self):
        socketserver.TCPServer.server_bind(self)
        self.server_name, self.server_port = self.server_address[:2]

def connect(timeout=0.3):
    c = sqlite3.connect(DB, timeout=timeout)
    c.execute('PRAGMA synchronous=FULL')
    return c

def decrypt(value):
    ciphertext = base64.b64decode(value, validate=True)
    if not ciphertext or len(ciphertext) % 16: raise ValueError('ciphertext')
    p = subprocess.run(['/usr/bin/openssl', 'enc', '-d', '-aes-256-cbc', '-K', KEY.hex(), '-iv', KEY[:16].hex(), '-nopad'], input=ciphertext, capture_output=True, timeout=0.5)
    if p.returncode: raise ValueError('decrypt')
    raw = p.stdout
    pad = raw[-1]
    if not 1 <= pad <= 32 or raw[-pad:] != bytes([pad]) * pad: raise ValueError('padding')
    raw = raw[:-pad]
    if len(raw) < 20: raise ValueError('length')
    length = struct.unpack('!I', raw[16:20])[0]
    if length != len(raw) - 20: raise ValueError('length')
    return raw[20:]

class Handler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(3)
    def log_message(self, fmt, *args):
        # Query strings contain signatures; never log payloads or URLs.
        pass
    def reply(self, code, body=b''):
        self.send_response(code)
        self.send_header('Content-Type', 'text/plain; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        self.reply(200, b'ok') if self.path == '/healthz' else self.reply(404)
    def do_POST(self):
        parts = urlsplit(self.path)
        if parts.path not in ('/', '/beisen/events'): return self.reply(404)
        try:
            q = parse_qs(parts.query, keep_blank_values=True, max_num_fields=10)
            def param(k):
                v = q[k]
                if len(v) != 1 or not v[0]: raise ValueError(k)
                return v[0]
            timestamp, nonce, signature = param('timestamp'), param('nonce'), param('msg_signature')
            # Keep delayed vendor retries valid; deduplicate by tenant + biz_id.
            int(timestamp)
            echo = q.get('echostr', [''])[0]
            if echo:
                encrypted = echo
            else:
                size = int(self.headers.get('Content-Length', '0'))
                if not 0 < size <= 1048576: return self.reply(413)
                encrypted = json.loads(self.rfile.read(size))['msg_encrypt']
                if not isinstance(encrypted, str): raise ValueError('body')
            expected = hashlib.sha1(''.join(sorted([TOKEN, timestamp, nonce, encrypted])).encode()).hexdigest()
            if not hmac.compare_digest(expected, signature):
                print('rejected 403: bad signature', flush=True)
                return self.reply(403)
            plaintext = decrypt(encrypted)
            if echo:
                print('echostr challenge ok', flush=True)
                return self.reply(200, plaintext)
            event = json.loads(plaintext)
            tenant, event_id = str(event['tenant_id']), event['biz_id']
            if not isinstance(event_id, str) or not event_id: raise ValueError('biz_id')
            category = str(event.get('biz_typeCode', '')).strip()
            with connect() as c:
                cur = c.execute('INSERT OR IGNORE INTO events(tenant_id,biz_id,category,received_at,payload) VALUES(?,?,?,?,?)', (tenant,event_id,category,time.time(),plaintext.decode('utf-8')))
            # Category and event id only; payload may contain personal data.
            print(f"accepted {category or '<none>'} biz_id={event_id[:64]} {'new' if cur.rowcount else 'duplicate'}", flush=True)
            self.reply(200, b'ok')
        except (ValueError, KeyError, TypeError, UnicodeError, IndexError) as e:
            # Exception type and a truncated message only: field names / parse positions, not the decrypted payload.
            print(f'rejected 400: {type(e).__name__}: {str(e)[:120]}', flush=True)
            self.reply(400)
        except Exception:
            print('request failed; event not acknowledged', flush=True)
            self.reply(503)

# ---------------- Feed API (intranet, port 8601) ----------------
# seq = SQLite rowid: strictly increasing as long as the newest row is never deleted (see purge_once).

ORG_ID_KEYS = ('oId', 'OId', 'orgId', 'OrgId', 'organizationId')

def feed_event(seq, category, received_at, payload):
    e = json.loads(payload)
    d = e.get('biz_data')
    if isinstance(d, str):
        try: d = json.loads(d)
        except ValueError: d = {'raw': d}
    if not isinstance(d, dict): d = {}
    org = next((d[k] for k in ORG_ID_KEYS if d.get(k) not in (None, '')), None)
    user = d.get('userId')
    return {'seq': seq, 'bizId': e.get('biz_id'), 'category': category, 'type': d.get('type'),
            'userId': None if user in (None, '') else str(user), 'orgId': None if org is None else str(org),
            'receivedAtMs': int(received_at * 1000), 'data': d}

_auth_fail = {'n': 0, 'last': 0.0}
_auth_lock = threading.Lock()

def note_auth_failure(addr):
    # At most one log line per minute so scanners cannot flood the journal.
    with _auth_lock:
        _auth_fail['n'] += 1
        now = time.time()
        if now - _auth_fail['last'] >= 60:
            print(f"feed auth rejected x{_auth_fail['n']}, latest from {addr}", flush=True)
            _auth_fail.update(n=0, last=now)

class FeedHandler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(5)
    def log_message(self, fmt, *args):
        pass  # polled every few seconds; only failures are logged
    def reply(self, code, body=b'', ctype='text/plain; charset=utf-8'):
        self.send_response(code)
        self.send_header('Content-Type', ctype)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        parts = urlsplit(self.path)
        if parts.path == '/healthz': return self.reply(200, b'ok')
        if parts.path not in ('/v1/status', '/v1/events'): return self.reply(404)
        auth = self.headers.get('Authorization', '')
        if not auth.startswith('Bearer ') or not hmac.compare_digest(auth[7:].strip().encode(), FEED_TOKEN.encode()):
            note_auth_failure(self.client_address[0])
            return self.reply(401)
        try:
            q = parse_qs(parts.query, max_num_fields=5)
            with closing(connect()) as c:
                mx, mn, n, latest = c.execute('SELECT COALESCE(MAX(rowid),0), COALESCE(MIN(rowid),0), COUNT(*), MAX(received_at) FROM events').fetchone()
                body = {'maxSeq': mx, 'minSeq': mn, 'count': n, 'retentionDays': RETENTION_DAYS,
                        'latestReceivedAtMs': int(latest * 1000) if latest else None}
                if parts.path == '/v1/events':
                    after = int(q.get('after', ['0'])[0])
                    limit = max(0, min(int(q.get('limit', ['200'])[0]), 1000))
                    rows = c.execute('SELECT rowid, category, received_at, payload FROM events WHERE rowid > ? ORDER BY rowid LIMIT ?', (after, limit)).fetchall()
                    body['events'] = [feed_event(*r) for r in rows]
            self.reply(200, json.dumps(body, ensure_ascii=False).encode('utf-8'), 'application/json; charset=utf-8')
        except (ValueError, TypeError):
            self.reply(400)
        except Exception as e:
            print(f'feed request failed: {type(e).__name__}', flush=True)
            self.reply(503)

# ---------------- Retention ----------------

def purge_once(now=None):
    """Delete events older than RETENTION_DAYS. The newest row is always kept so rowid (feed seq) never restarts."""
    cutoff = (now or time.time()) - RETENTION_DAYS * 86400
    with closing(connect(timeout=10)) as c:
        with c:
            n = c.execute('DELETE FROM events WHERE received_at < ? AND rowid < (SELECT MAX(rowid) FROM events)', (cutoff,)).rowcount
        if n:
            c.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    return n

def retention_loop():
    while True:
        try:
            n = purge_once()
            if n: print(f'retention: removed {n} event(s) older than {RETENTION_DAYS} days', flush=True)
        except Exception as e:
            print(f'retention failed: {type(e).__name__}: {str(e)[:120]}', flush=True)
        time.sleep(3600)

if __name__ == '__main__':
    os.umask(0o077)
    with connect() as c:
        c.execute('PRAGMA journal_mode=WAL')
        c.execute('CREATE TABLE IF NOT EXISTS events(tenant_id TEXT NOT NULL,biz_id TEXT NOT NULL,category TEXT NOT NULL,received_at REAL NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(tenant_id,biz_id))')
    # Webhook first: it is the one Beisen pushes to, everything else is secondary.
    webhook = Server((BIND, 8600), Handler)
    print(f'webhook listening on {BIND}:8600, retention {RETENTION_DAYS} days', flush=True)
    threading.Thread(target=retention_loop, name='retention', daemon=True).start()
    # The feed is optional: any problem there must never take the webhook down.
    if len(FEED_TOKEN) >= 32:
        try:
            feed = Server((BIND, FEED_PORT), FeedHandler)
            threading.Thread(target=feed.serve_forever, name='feed', daemon=True).start()
            print(f'feed api listening on {BIND}:{FEED_PORT}', flush=True)
        except OSError as e:
            print(f'feed api failed to start: {e}', flush=True)
    else:
        print('feed api disabled: BEISEN_FEED_TOKEN not set (>= 32 chars required)', flush=True)
    webhook.serve_forever()
