# 北森组织与员工事件接收服务

部署在一台北森能访问到（经网关/反向代理映射）、域控也能访问到的 Linux 服务器上（实测 Rocky 9 / systemd 252 / Python 3.9）。只用系统自带的 Python 3、OpenSSL、SQLite，不安装任何依赖，不影响同机其他业务。

下文以内网地址 `192.0.2.10`、公网回调域名 `events.example.com` 为例。监听地址由 `/etc/beisen-webhook.env` 的 `BEISEN_BIND` 决定，默认 `0.0.0.0`（所有网卡）；**服务器有公网网卡时请设为内网地址**，避免 8601 暴露。公网健康检查需在告警配置里设置 `PUBLIC_HEALTH_URL`。

| 端口 | 用途 | 谁访问 | 认证 |
|---|---|---|---|
| 8600 | 北森事件回调：`POST /beisen/events`（兼容 `/`）；`GET /healthz` | 北森（经公网网关 `events.example.com` 映射） | 北森签名 + AES 加密 |
| 8601 | 内网拉取接口：`GET /v1/status`、`GET /v1/events?after=<seq>&limit=<n>`；`GET /healthz` | AD 侧 BSTOAD 事件监听 | `Authorization: Bearer <BEISEN_FEED_TOKEN>` |

依据北森文档：
- https://users.italent.cn/#/noAuthDocument?pageId=167151702
- https://users.italent.cn/#/noAuthDocument?pageId=168001586
- https://users.italent.cn/#/noAuthDocument?pageId=167151696

实现 SHA-1 签名验证、AES-256-CBC 解密、32 字节 PKCS#7 填充、POST echostr 验证、事件持久化、tenant_id + biz_id 幂等。使用系统 Python 3、OpenSSL、SQLite，不安装全局依赖。

**事件只是通知，不含员工/组织数据。** 实测载荷：`biz_data = {tenantId, userId, type}`（type 已见 `Entry`、`EditInfo`）。AD 侧收到后按时间窗回查北森 OpenAPI 取完整档案。

## 拉取接口（8601）

```
GET /v1/events?after=<seq>&limit=<n≤1000>
→ {"maxSeq":4,"minSeq":1,"count":4,"retentionDays":30,"latestReceivedAtMs":…,
   "events":[{"seq":3,"bizId":"…","category":"EmpEvent","type":"EditInfo",
              "userId":"…","orgId":null,"receivedAtMs":…,"data":{…biz_data 原样…}}]}
```

- `seq` 严格递增（SQLite rowid）。消费方自己保存游标，服务端无状态，多个消费方互不影响。
- 长轮询：`&wait=N`（最大 30 秒）。没有新事件时请求挂起，有新事件立即返回；同时挂起的请求最多 4 个，超出的立即返回。不带 `wait` 的旧客户端行为不变。
- `GET /v1/status` 返回同样的统计字段，不含事件。
- 令牌错误返回 401，日志每分钟最多记一条。
- 公网网关只映射 8600，8601 从公网不可达。防火墙可按需只对域控开放 8601，否则仅靠令牌认证。

## 保留与日志

- **事件**：每小时清理 `received_at` 早于 30 天（`BEISEN_RETENTION_DAYS`）的事件，**但永远保留最新一条**——否则表被清空后 rowid 会从 1 重来，消费方游标就会跳过新事件。数据库体积稳定在 30 天事件量（每条约 0.5KB）。
- **日志**：两个服务写入独立的 journald 命名空间 `beisen-webhook`（`/etc/systemd/journald@beisen-webhook.conf`）：持久化、`MaxRetentionSec=30day`、按天切分，**不影响系统和其他业务的日志策略**（即使系统日志本身未持久化）。
- 日志只有：启动、每条事件一行（类别 + biz_id，不含载荷）、拒绝原因、清理结果、告警。拉取接口的正常请求不记日志。

## 服务器文件

- `/opt/beisen-webhook/server.py`、`healthcheck.py`：程序；`backup-<时间>/`：升级前的备份
- `/etc/beisen-webhook.env`：`BEISEN_TOKEN`、`BEISEN_AES_KEY`（北森回调）、`BEISEN_FEED_TOKEN`（拉取接口），root 600
- `/etc/beisen-webhook-alert.env`：告警渠道，root 600
- `/etc/systemd/system/beisen-webhook.service`、`beisen-webhook-healthcheck.{service,timer}`
- `/etc/systemd/journald@beisen-webhook.conf`：日志命名空间
- `/var/lib/beisen-webhook/events.sqlite3`：解密后的事件 JSON，服务账号专用目录

## 运维

```sh
systemctl status beisen-webhook
journalctl --namespace beisen-webhook -u beisen-webhook          # 注意要带 --namespace
curl http://192.0.2.10:8600/healthz; curl http://192.0.2.10:8601/healthz
sudo grep BEISEN_FEED_TOKEN /etc/beisen-webhook.env               # 填到 BSTOAD「事件订阅」页
```

读取事件（在服务器执行）：

```sh
sudo python3 - <<'PY'
import sqlite3
c=sqlite3.connect('file:/var/lib/beisen-webhook/events.sqlite3?mode=ro',uri=True)
for row in c.execute("SELECT rowid, category, received_at, payload FROM events ORDER BY rowid DESC LIMIT 20"):
    print(row)
PY
```

北森后台应配置「事件订阅器」，填入可从北森访问的回调 URL 与配置文件中的 Token/AESKey，勾选组织与员工事件后验证。「事件接收器」与北森向本服务推送的方向不同。

服务设置 128MB 内存、25% 单核 CPU 配额、32 任务限制。

**重启只中断约 1 秒。** `HTTPServer` 默认在 bind 与 listen 之间做反向 DNS（`getfqdn`），服务器 DNS 无法反查内网 IP 时要超时约 10 秒，期间端口拒绝连接。`server.py` 的 `Server` 类跳过了这一步。

## 健康检查与告警

`beisen-webhook-healthcheck.timer` 每分钟运行一次 `healthcheck.py`，检查：systemd 服务状态、内网 8600 `/healthz`、公网 `/healthz`（网关/证书/DNS；`PUBLIC_HEALTH_URL` 置空则不查）、内网 8601 `/healthz`、上次检查以来的 503（事件未确认）。连续 2 次异常时向企业微信/钉钉/飞书群机器人告警，持续异常每 60 分钟提醒一次，异常类型有变化时立即补发，恢复时发通知。正常运行时不写日志。

- 渠道配置：`/etc/beisen-webhook-alert.env`（模板见 `beisen-webhook-alert.env.example`），留空的渠道不发送
- 测试渠道：`python3 /opt/beisen-webhook/healthcheck.py --test --env /etc/beisen-webhook-alert.env`
- 查看检查记录：`journalctl --namespace beisen-webhook -u beisen-webhook-healthcheck`
- 运行身份：DynamicUser（附加 systemd-journal 组以读取本服务日志），状态在 `/var/lib/private/beisen-webhook-healthcheck/`

盲区：检查跑在同一台服务器上，**整台服务器宕机或断网时不会告警**；BSTOAD 事件监听连不上 8601 时会从 AD 侧告警，可作为外部探测。

停用告警：`systemctl disable --now beisen-webhook-healthcheck.timer`

## 部署 / 停用

首次部署：把本目录文件拷到 `/opt/beisen-webhook/` 后以 root 运行 `deploy.sh`（可重复执行，不覆盖已有密钥）。

停用（只影响本服务，数据与配置保留以便恢复）：

```sh
systemctl disable --now beisen-webhook beisen-webhook-healthcheck.timer
firewall-cmd --remove-port=8600/tcp --remove-port=8601/tcp
firewall-cmd --permanent --remove-port=8600/tcp --remove-port=8601/tcp
```

时间戳格式会验证，但不以时间窗口拒绝事件，以兼容北森延迟重试；已落库的事件 ID 不重复保存。

`verify.py` 是对正在运行的服务的冒烟测试（回调地址验证、签名拒绝）。启用拉取接口后不再做落库测试：合成事件会被 AD 侧监听当成真实事件，删除它们又会让序号被下一条真实事件复用，导致监听跳过那条事件。
