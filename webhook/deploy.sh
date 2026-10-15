# 在服务器上以 root 运行（文件已拷到 /opt/beisen-webhook/）。可重复执行：已有的密钥和配置不会被覆盖。
set -e
cd /opt/beisen-webhook
id beisen-webhook >/dev/null 2>&1 || useradd --system --no-create-home --shell /sbin/nologin beisen-webhook

# 北森回调 Token / EncodingAESKey，以及给 AD 侧（BSTOAD）拉取事件用的令牌
python3 - <<'REMOTE'
import secrets, string, os
chars = string.ascii_letters + string.digits
rnd = lambda n: ''.join(secrets.choice(chars) for _ in range(n))
os.umask(0o077)
path = '/etc/beisen-webhook.env'
text = open(path).read() if os.path.exists(path) else ''
add = ''
if 'BEISEN_TOKEN=' not in text: add += 'BEISEN_TOKEN=' + rnd(12) + '\n'
if 'BEISEN_AES_KEY=' not in text: add += 'BEISEN_AES_KEY=' + rnd(43) + '\n'
if 'BEISEN_FEED_TOKEN=' not in text: add += 'BEISEN_FEED_TOKEN=' + rnd(48) + '\n'
if add:
    with open(path, 'a') as f: f.write(add)
REMOTE
[ -e /etc/beisen-webhook-alert.env ] || install -m 600 beisen-webhook-alert.env.example /etc/beisen-webhook-alert.env

# 专用日志命名空间：持久化、最多保留 30 天，不影响系统其他日志
install -m 644 'journald@beisen-webhook.conf' /etc/systemd/journald@beisen-webhook.conf
install -m 644 beisen-webhook.service beisen-webhook-healthcheck.service beisen-webhook-healthcheck.timer /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now beisen-webhook.service
systemctl restart beisen-webhook.service
systemctl enable --now beisen-webhook-healthcheck.timer

# 8600：北森回调（经公网网关映射）；8601：内网拉取接口（令牌认证，网关不映射）
firewall-cmd --add-port=8600/tcp --add-port=8601/tcp
firewall-cmd --permanent --add-port=8600/tcp --add-port=8601/tcp

# 监听地址：/etc/beisen-webhook.env 的 BEISEN_BIND，未设置时为 server.py 的默认值
H=$(sed -n 's/^BEISEN_BIND=//p' /etc/beisen-webhook.env); H=${H:-0.0.0.0}; [ "$H" = 0.0.0.0 ] && H=127.0.0.1
for i in $(seq 10); do curl -fsS --max-time 2 "http://$H:8600/healthz" && break; sleep 1; done; echo
curl --fail --max-time 3 "http://$H:8601/healthz"; echo
systemctl is-active beisen-webhook beisen-webhook-healthcheck.timer
