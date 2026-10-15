"""beisen-webhook 健康检查：由 systemd timer 每分钟触发。

检查 systemd 服务状态、内网 /healthz、公网 /healthz（网关/证书/DNS）、内网拉取接口 8601、上次检查以来的 503（事件未确认）。
连续 FAIL_THRESHOLD 次异常后通过企业微信/钉钉/飞书群机器人告警；持续异常每 REPEAT_MINUTES 提醒一次；恢复时通知。
手动测试渠道：python3 healthcheck.py --test --env /etc/beisen-webhook-alert.env
"""
import base64, hashlib, hmac, json, os, socket, subprocess, sys, time, urllib.parse, urllib.request

TAG = '【北森事件接收】'  # 钉钉「自定义关键词」安全设置填「北森」即可匹配

# 部署相关的默认值，可在 /etc/beisen-webhook-alert.env 中覆盖；置空表示不检查该项
DEFAULT_LOCAL_HEALTH_URL = 'http://127.0.0.1:8600/healthz'
DEFAULT_FEED_HEALTH_URL = 'http://127.0.0.1:8601/healthz'
DEFAULT_PUBLIC_HEALTH_URL = ''  # 填公网回调域名的 /healthz，经网关检查证书与映射

def load_env(path):
    # systemd EnvironmentFile 的子集：KEY=VALUE，# 注释，值可带引号。不经过 shell，URL 里的 & 不会出问题。
    for line in open(path, encoding='utf-8'):
        line = line.strip()
        if not line or line.startswith('#') or '=' not in line: continue
        k, v = line.split('=', 1)
        v = v.strip()
        if len(v) >= 2 and v[0] == v[-1] and v[0] in '"\'': v = v[1:-1]
        os.environ[k.strip()] = v

def cfg(name, default=''):
    return os.environ.get(name, default).strip()

def log(level, msg):
    # <N> 前缀由 journald 解析为日志级别；服务配了 LogLevelMax=notice，正常轮次不刷日志
    print(f'<{level}>{msg}', flush=True)

# ---------------- 检查 ----------------

def http_ok(url, timeout):
    try:
        with urllib.request.urlopen(url, timeout=timeout) as r:
            if r.status == 200 and r.read(16) == b'ok': return None
            return f'HTTP {r.status}'
    except Exception as e:
        return f'{type(e).__name__}: {str(e)[:100]}'

def service_state(unit):
    try:
        return subprocess.run(['systemctl', 'is-active', unit], capture_output=True, text=True, timeout=10).stdout.strip()
    except FileNotFoundError:
        return None

def failed_requests_since(unit, since_ts):
    ns = cfg('JOURNAL_NAMESPACE', 'beisen-webhook')  # 服务日志写在独立的 journald 命名空间（30 天保留）
    try:
        out = subprocess.run(['journalctl'] + (['--namespace', ns] if ns else []) +
                             ['-u', unit, '--since', f'@{int(since_ts)}', '-o', 'cat', '-q', '--no-pager'],
                             capture_output=True, text=True, timeout=20).stdout
    except FileNotFoundError:
        return 0
    return sum(1 for line in out.splitlines() if 'request failed' in line)

def collect_problems(last_run):
    """返回 {问题类型: 描述}。是否「有变化」按类型比较，描述里的次数、报错细节变化不触发重复告警。"""
    unit = cfg('SERVICE_UNIT', 'beisen-webhook')
    problems = {}
    state = service_state(unit)
    if state is not None and state != 'active':
        problems['service'] = f'systemd 服务 {unit} 状态为 {state or "未知"}'
    err = http_ok(cfg('LOCAL_HEALTH_URL', DEFAULT_LOCAL_HEALTH_URL), 3)
    if err: problems['local'] = f'内网 /healthz 不通：{err}'
    public_url = cfg('PUBLIC_HEALTH_URL', DEFAULT_PUBLIC_HEALTH_URL)
    if public_url:
        err = http_ok(public_url, 10)
        if err: problems['public'] = f'公网 /healthz 不通（网关/证书/DNS）：{err}'
    feed_url = cfg('FEED_HEALTH_URL', DEFAULT_FEED_HEALTH_URL)
    if feed_url:
        err = http_ok(feed_url, 3)
        if err: problems['feed'] = f'内网事件拉取接口（8601）不通，AD 侧收不到事件：{err}'
    n = failed_requests_since(unit, last_run or time.time() - 90)
    if n: problems['503'] = f'上次检查以来 {n} 次事件处理失败（返回 503，北森会重试）'
    return problems

# ---------------- 发送（格式与 BSTOAD AlertSender 一致） ----------------

def truncate_utf8(text, max_bytes):
    b = text.encode('utf-8')
    return text if len(b) <= max_bytes else b[:max_bytes - 30].decode('utf-8', 'ignore') + '\n……（已截断）'

def post_json(url, payload):
    req = urllib.request.Request(url, data=json.dumps(payload, ensure_ascii=False).encode('utf-8'),
                                 headers={'Content-Type': 'application/json; charset=utf-8'})
    with urllib.request.urlopen(req, timeout=15) as r:
        body = r.read().decode('utf-8', 'replace')
    try:
        doc = json.loads(body)
    except ValueError:
        return  # HTTP 成功但非 JSON，视为成功
    for key in ('errcode', 'code', 'StatusCode'):
        if isinstance(doc.get(key), int):
            if doc[key] != 0: raise RuntimeError(f'机器人返回错误：{body[:300]}')
            return

def send_wecom(text):
    post_json(cfg('WECOM_WEBHOOK'), {'msgtype': 'text', 'text': {'content': truncate_utf8(text, 2000)}})

def send_dingtalk(text):
    url, secret = cfg('DINGTALK_WEBHOOK'), cfg('DINGTALK_SECRET')
    if secret:
        # 钉钉加签：HmacSHA256(key=secret, data=timestamp+"\n"+secret)，Base64 后 URL 编码
        ts = str(int(time.time() * 1000))
        sign = base64.b64encode(hmac.new(secret.encode(), f'{ts}\n{secret}'.encode(), hashlib.sha256).digest()).decode()
        url += ('&' if '?' in url else '?') + f'timestamp={ts}&sign={urllib.parse.quote(sign, safe="")}'
    post_json(url, {'msgtype': 'text', 'text': {'content': truncate_utf8(text, 18000)}})

def send_feishu(text):
    secret = cfg('FEISHU_SECRET')
    payload = {'msg_type': 'text', 'content': {'text': truncate_utf8(text, 18000)}}
    if secret:
        # 飞书加签：HmacSHA256(key=timestamp+"\n"+secret, data=空)，Base64
        ts = str(int(time.time()))
        payload['timestamp'] = ts
        payload['sign'] = base64.b64encode(hmac.new(f'{ts}\n{secret}'.encode(), b'', hashlib.sha256).digest()).decode()
    post_json(cfg('FEISHU_WEBHOOK'), payload)

def channels():
    return [(name, fn) for name, key, fn in (('企业微信', 'WECOM_WEBHOOK', send_wecom),
                                             ('钉钉', 'DINGTALK_WEBHOOK', send_dingtalk),
                                             ('飞书', 'FEISHU_WEBHOOK', send_feishu)) if cfg(key)]

def notify(text):
    """各渠道独立发送，返回 (成功数, 结果列表)。日志里不出现 webhook 地址。"""
    results = []
    for name, fn in channels():
        try:
            fn(text); results.append((name, True, '发送成功'))
        except Exception as e:
            results.append((name, False, f'{type(e).__name__}: {str(e)[:200]}'))
    return sum(ok for _, ok, _ in results), results

# ---------------- 状态机 ----------------

def fmt_ts(ts):
    return time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(ts))

def minutes(sec):
    return max(1, round(sec / 60))

def load_state(path):
    try:
        with open(path, encoding='utf-8') as f: return json.load(f)
    except (OSError, ValueError):
        return {}

def save_state(path, st):
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f: json.dump(st, f, ensure_ascii=False)
    os.replace(tmp, path)

def run():
    state_path = os.path.join(cfg('STATE_DIRECTORY', '/var/lib/beisen-webhook-healthcheck'), 'state.json')
    threshold = int(cfg('FAIL_THRESHOLD', '2'))
    repeat = int(cfg('REPEAT_MINUTES', '60')) * 60
    host = socket.gethostname()
    now = time.time()
    st = load_state(state_path)
    problems = collect_problems(st.get('last_run'))
    st['last_run'] = now

    if problems:
        st['streak'] = st.get('streak', 0) + 1
        st.setdefault('since', now)
        log(4, f'检查异常（连续 {st["streak"]} 次）：' + '；'.join(problems.values()))
        changed = sorted(problems) != sorted(st.get('problems', []))
        due = not st.get('alerting') or changed or now - st.get('last_alert', 0) >= repeat
        if st['streak'] >= threshold and due:
            head = '服务异常' if not st.get('alerting') else ('异常情况有变化' if changed else '仍未恢复')
            text = (f'{TAG}{head}\n主机：{host}\n开始：{fmt_ts(st["since"])}（已持续约 {minutes(now - st["since"])} 分钟，'
                    f'连续 {st["streak"]} 次检查失败）\n问题：\n' + '\n'.join('- ' + p for p in problems.values()) +
                    '\n排查：systemctl status beisen-webhook；journalctl --namespace beisen-webhook -u beisen-webhook -n 50')
            sent, results = notify(text)
            for name, ok, msg in results: log(5 if ok else 3, f'告警 {name}：{msg}')
            if not results: log(3, '未配置任何告警渠道（/etc/beisen-webhook-alert.env），告警只写入日志')
            # 全部渠道失败时不标记已告警，下一轮重试
            if sent or not results:
                st.update(alerting=True, last_alert=now, problems=sorted(problems))
    else:
        if st.get('alerting'):
            text = (f'{TAG}已恢复\n主机：{host}\n异常时段：{fmt_ts(st["since"])} ~ {fmt_ts(now)}（约 {minutes(now - st["since"])} 分钟）\n'
                    '异常期间的推送北森会自动重试；超出重试窗口的变更由夜间全量同步兜底。')
            sent, results = notify(text)
            for name, ok, msg in results: log(5 if ok else 3, f'恢复通知 {name}：{msg}')
            if results and not sent:
                save_state(state_path, st)  # 恢复通知全部失败：保留告警状态，下一轮重试
                return
            log(5, '服务已恢复')
        for k in ('streak', 'since', 'alerting', 'last_alert', 'problems'): st.pop(k, None)
    save_state(state_path, st)

def test():
    if not channels():
        print('未配置任何告警渠道：请在环境文件里填写 WECOM_WEBHOOK / DINGTALK_WEBHOOK / FEISHU_WEBHOOK')
        return 1
    _, results = notify(f'{TAG}测试消息\n主机：{socket.gethostname()}\n时间：{fmt_ts(time.time())}\n收到这条说明告警渠道配置正确。')
    for name, ok, msg in results: print(f'{"✔" if ok else "✘"} {name}：{msg}')
    return 0 if all(ok for _, ok, _ in results) else 1

if __name__ == '__main__':
    args = sys.argv[1:]
    if '--env' in args: load_env(args[args.index('--env') + 1])
    if '--test' in args: sys.exit(test())
    try:
        run()
    except Exception as e:
        log(3, f'健康检查自身出错：{type(e).__name__}: {e}')
        sys.exit(1)
