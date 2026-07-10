# BSTOAD — 北森 iTalent → Active Directory 同步工具

> **B**ei**S**en **to** **AD**：把北森 iTalent HR 系统的组织架构与员工信息单向同步到 Windows Active Directory 的开源工具。
> 单文件 exe · 无需安装 .NET · 默认干跑 · 只移动/禁用，永不删除

[.NET 8](https://dotnet.microsoft.com/) · Windows Server · MIT License

---

## 它解决什么问题

企业用北森做 HR、用 AD 做统一身份认证时，组织架构和人员变动需要两边手工维护。BSTOAD 让 AD 自动跟随北森：

- **入职** → 自动在对应部门 OU 下建号（工号作登录名，同步邮箱/手机/部门/职位）
- **调岗** → 账号自动移动到新部门 OU（同一对象，SID/密码/组权限全保留）
- **部门改名/重组** → AD 的 OU 同步改名/移动（靠 OrgId 锚点追踪，不留孤儿 OU）
- **离职** → 账号自动禁用并归档到离职 OU
- **存量 AD 账号** → 通过工号/邮箱预匹配自动接管，不会重复建号

## 核心设计

### 两个安全契约

**纳管标记**：只有被本工具创建或接管过的账号才带 `adminDescription=beisen-sync:managed` 标记；离职差集**只处理有标记的账号**。服务账号、外包账号、任何没被同步碰过的账号——永远安全。

**OU 锚点**：每个同步创建的 OU 带 `description=beisen-org-id:<OrgId>` 锚点；同步认 OrgId 不认名字，所以部门改名是"同一个 OU 改名"而非"建新删旧"。没有锚点的 OU（手工建的）永远不被触碰。

### 多重安全阀

| 安全阀 | 行为 |
|---|---|
| 无删除设计 | 全代码库没有任何 LDAP 删除调用，最重操作 = 移动 + 禁用 |
| 差集熔断 | 单轮拟禁用数 > max(N, 纳管总数×比例) → 差集中止等人工 |
| 拉取量哨兵 | 全量员工数骤降（< 上轮 70%）→ 判定数据源异常，拒绝跑差集 |
| 特权保护 | `adminCount=1` 的账号 + 配置禁区（CN=Users 等）下的账号永不自动管理 |
| 姓名弱校验 | SAM 撞工号但姓名对不上 → 拒绝接管，防误认 |
| 复职不自动启用 | 离职账号对应员工复职 → 移回部门 OU 但保持禁用，人工确认 |
| 默认干跑 | 所有命令默认只打印决策，`--apply` 才写 AD |

### 存量账号接管决策树（C → A → B）

```
按 employeeID 全域搜 AD
 ├─ 命中 ──────────────► 接管：更新属性 / 按需移动 / 打标记
 └─ 未命中 → 按 sAMAccountName=工号 搜（employeeID 为空）
     ├─ 命中且姓名匹配 ─► 回填 employeeID 后接管
     └─ 未命中 ────────► 检查 SAM 未被占用后新建账号
```

配合 `match-ad` 预匹配命令（P1 按工号 / P2 按邮箱回填 employeeID），可平滑接管拼音命名等历史遗留账号。

## 架构

```
┌─────────────┐   HTTPS    ┌──────────────────────────┐   LDAP(签名加密)   ┌────────────┐
│ 北森 OpenAPI │ ─────────► │  Bstoad.Worker.exe       │ ────────────────► │  AD 域控    │
│ GetByTimeWindow 时间窗滚动 │  决策树 / 锚点 / 安全阀    │  建OU/建号/移动/禁用│            │
└─────────────┘            └──────────────────────────┘                   └────────────┘
     组织+员工                 单文件·计划任务调度              北森同步 OU 子树内
```

```
src/
├─ Bstoad.Core/             # 配置模型（Beisen / ActiveDirectory / Sync 三段）
├─ Bstoad.Beisen/           # 北森客户端：token 缓存、时间窗分片、scrollId 滚动分页
├─ Bstoad.ActiveDirectory/  # LDAP Provisioner：OU 锚点、账号 CRUD(无D)、保护判定
├─ Bstoad.Sync/             # 同步编排：决策树、差集、预匹配、标记迁移
└─ Bstoad.Worker/           # CLI 宿主：子命令、DPAPI 凭据加密、退出码、互斥
```

## 获取程序

两种方式任选：

### 方式 A · 直接下载（推荐）

到本仓库的 **[Releases](../../releases)** 页面下载最新的 `Bstoad.Worker.exe`（单文件，约 35MB，内含 .NET 运行时，目标服务器**无需安装任何东西**）。每个 Release 由 GitHub Actions 从对应版本源码自动构建并附带 SHA256 校验值，下载后可核验：

```powershell
Get-FileHash .\Bstoad.Worker.exe -Algorithm SHA256   # 与 Release 页的 .sha256 比对
```

### 方式 B · 从源码构建

任何装了 .NET 8 SDK 的机器都能出包（含 macOS/Linux 交叉编译 Windows 产物）：

```bash
dotnet publish src/Bstoad.Worker -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -o publish
```

> 维护者发版：`git tag v1.0.0 && git push --tags` 即触发自动构建与 Release 发布（见 `.github/workflows/release.yml`）。

## 部署指南

### 0. 前置条件

- 一台 7×24 在线的**域内 Windows Server**（域控本机亦可；程序只是 LDAP 客户端）
- 网络可达：北森 `openapi.italent.cn`（HTTPS 443）、域控 389 端口
- 北森开放平台已创建连接器，取得 AppKey/AppSecret，**并确认数据权限包含组织与员工/任职接口**
- AD 中已规划：同步根 OU（如 `OU=北森同步,DC=example,DC=com`）

### 1. 安装（5 分钟）

```powershell
mkdir C:\bstoad
Copy-Item <来源>\Bstoad.Worker.exe C:\bstoad\
cd C:\bstoad
Unblock-File .\Bstoad.Worker.exe        # 解除下载锁定
.\Bstoad.Worker.exe --help              # 验证可运行（输出中文帮助）

# 锁目录权限（计划任务以 SYSTEM 运行此 exe，目录必须锁）
icacls C:\bstoad /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F"
```

装有杀毒/EDR 的环境，请将该 exe 加入白名单。

### 2. AD 侧准备（一次性）

```powershell
# 专用服务账号（不要用域管账号跑同步）
New-ADUser bstoad-svc -AccountPassword (Read-Host -AsSecureString "密码") `
  -Enabled $true -PasswordNeverExpires $true -Path "CN=Users,DC=example,DC=com"
```

在 ADUC 中对同步根 OU **委派控制**给 `bstoad-svc`：创建/删除用户对象和组织单位对象 + 对后代对象完全控制。权限边界即此 OU 子树。

**初始密码策略**（新员工建号需要）：默认域策略会拒绝弱初始密码。推荐用**细粒度密码策略（PSO）**只对同步账号组放宽，不动全域策略；或将 `ForceChangePasswordAtNextLogon` 设为 `true` 配合强随机密码。

### 3. 配置

```powershell
.\Bstoad.Worker.exe init-config        # 生成 appsettings.json 模板
notepad .\appsettings.json
```

必填四项：`Beisen.AppKey`、`Beisen.AppSecret`、`ActiveDirectory.ServiceAccount`（格式 `EXAMPLE\\bstoad-svc`，JSON 中双反斜杠）、`ActiveDirectory.ServicePassword`。
按环境核对：`BaseUserOu`、`LeaverOu`、`UpnSuffix`、`NeverTouchOuDns`（禁区）、`Sync.RootOrgId`（可选，选定子树去掉顶层壳）、`Sync.LeaverOrgId`（北森"离职人员"部门 ID）。

> ⚠️ 编辑时保持**英文输入法**（全角逗号会导致 JSON 解析失败）、**UTF-8 编码**保存。

```powershell
# JSON 语法自检（无输出=正确）
Get-Content .\appsettings.json -Raw -Encoding UTF8 | ConvertFrom-Json | Out-Null

# DPAPI 加密凭据（密钥绑定本机，文件被拷走无法解密；换机需重填明文再跑）
.\Bstoad.Worker.exe protect-config
```

### 4. 分层测试上线（核心流程）

> 原则：**所有命令默认干跑（只打印决策不写 AD），确认无误再加 `--apply`；一层验收不过不进下一层。**

```powershell
# 层1 组织架构：预览 OU 树 → 真建 → ADUC 检查层级与锚点(OU描述=beisen-org-id:xxx)
.\Bstoad.Worker.exe sync --orgs-only
.\Bstoad.Worker.exe sync --orgs-only --apply

# 层2 存量账号预匹配（AD 已有员工账号时必做，防同人两账号）：
#     P1 按 SAM=工号 / P2 按邮箱回填 employeeID；逐条人工审核后 apply
.\Bstoad.Worker.exe match-ad
.\Bstoad.Worker.exe match-ad --apply

# 层3 单人试点：干跑看 [字段核对]（工号/邮箱/手机/目标OU）→ 真跑一个人 → ADUC 验收
.\Bstoad.Worker.exe sync --users-only --full --only 某工号
.\Bstoad.Worker.exe sync --users-only --full --only 某工号 --apply

# 层4 离职归档：只处理离职员工（移入离职OU+禁用；无账号者跳过不建）
.\Bstoad.Worker.exe sync --users-only --full --leavers-only
.\Bstoad.Worker.exe sync --users-only --full --leavers-only --apply
#     验证：离职 OU 下应全部为禁用
Get-ADUser -SearchBase "OU=离职人员,OU=北森同步,DC=example,DC=com" -Filter * -Properties Enabled |
  Format-Table Name,SamAccountName,Enabled

# 层5 全员：干跑核对统计数字 → 真跑 → 存量补标记
.\Bstoad.Worker.exe sync --users-only --full
.\Bstoad.Worker.exe sync --users-only --full --apply
.\Bstoad.Worker.exe migrate-marker --apply
```

若从其他同步工具（如飞书同步）迁移：**层 5 之前先停用旧同步的计划任务**，避免两套系统抢账号。

### 5. 注册计划任务（执行完 = 正式上线）

```powershell
# 每小时 :07 增量同步
Register-ScheduledTask -TaskName "BSTOAD-Sync" `
  -Action (New-ScheduledTaskAction -Execute "C:\bstoad\Bstoad.Worker.exe" -Argument "sync --apply" -WorkingDirectory "C:\bstoad") `
  -Trigger (New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddMinutes(7)) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration ([TimeSpan]::FromDays(3650))) `
  -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew) `
  -User "SYSTEM" -RunLevel Highest

# 每日 02:00 全量对账 + 离职差集
Register-ScheduledTask -TaskName "BSTOAD-FullSync" `
  -Action (New-ScheduledTaskAction -Execute "C:\bstoad\Bstoad.Worker.exe" -Argument "sync --apply --full" -WorkingDirectory "C:\bstoad") `
  -Trigger (New-ScheduledTaskTrigger -Daily -At "02:00") `
  -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew) `
  -User "SYSTEM" -RunLevel Highest
```

（或直接使用 `scripts\install-tasks.ps1`。）任务身份用 SYSTEM 即可——AD 写权限走配置里的 bind 凭据，与任务身份无关。

```powershell
# 验证首轮
Start-ScheduledTask BSTOAD-Sync
Get-ScheduledTaskInfo BSTOAD-Sync | Select TaskName, LastRunTime, LastTaskResult   # 应为 0
Get-Content C:\bstoad\logs\bstoad-$(Get-Date -Format yyyyMMdd).log -Tail 20 -Encoding UTF8
```

### 6. 上线后观察（建议一周）

```powershell
Get-ScheduledTaskInfo BSTOAD-Sync, BSTOAD-FullSync | Select TaskName, LastRunTime, LastTaskResult
Import-Csv C:\bstoad\state\stats.csv | Select-Object -Last 5 | Format-Table
```

结果码为 0、stats.csv 数字平稳即健康。日志中的 `[保护]`/`[部门撤销]`/`[场景E]`/熔断告警都需要人工看一眼（含义见"安全阀"表）。

## 命令参考

| 命令 | 说明 |
|---|---|
| `sync` | 同步（默认干跑；组织+人员，员工按 7 天时间窗增量） |
| `sync --apply` | 实际写入 AD |
| `sync --full` | 员工全量拉取 + 离职差集对账 |
| `sync --orgs-only` / `--users-only` | 分层：只同步组织 / 只同步人员 |
| `sync --only 工号[,工号...]` | 试点：只处理指定员工 |
| `sync --leavers-only` | 只处理离职员工（归档+禁用） |
| `match-ad [--apply]` | 预匹配存量账号，回填 employeeID（不打标记） |
| `migrate-marker [--apply]` | 给存量纳管账号补标记 |
| `init-config` | 生成配置模板 |
| `protect-config` | DPAPI 加密配置中的密钥与密码 |

**退出码**：`0` 成功 · `1` 异常 · `2` 完成但有失败项 · `3` 另一实例运行中（计划任务重叠自动让路）

## 主要配置项

| 键 | 说明 |
|---|---|
| `Beisen.AppKey / AppSecret` | 北森开放平台连接器凭据 |
| `Beisen.EmployeeExtraParams` | 追加到员工接口的参数（如 `empStatus`、`withDisabled` — 不传则北森默认不返回离职记录） |
| `ActiveDirectory.BaseUserOu` | 同步根 OU，所有部门 OU 和账号都在它之下 |
| `ActiveDirectory.LeaverOu` | 离职归档 OU |
| `ActiveDirectory.DefaultInitialPassword` | 新账号初始密码（注意域密码策略；建议配合 PSO 或首登强制改密） |
| `ActiveDirectory.NeverTouchOuDns` | 禁区容器列表，其下账号永不自动管理 |
| `Sync.RootOrgId` | 选定北森某组织作为同步根（去掉顶层壳），留空同步整树 |
| `Sync.LeaverOrgId` | 北森"离职人员"部门 ID：挂此部门 = 离职 |
| `Sync.MaxLeaversPerRun / MaxLeaverRatio` | 差集熔断阈值 |
| `Beisen.LogRawSamples` | 是否输出原始响应样本（含 PII，默认关闭，仅排查字段映射时开启） |

## 运维

- **日志**：`logs/bstoad-yyyyMMdd.log`，按天滚动，自动保留 14 天（UTF-8，PowerShell 查看加 `-Encoding UTF8`）
- **统计**：`state/stats.csv` 每轮追加一行（拉取量/各动作计数/失败数/耗时），Excel 直接看趋势
- **升级**：停任务 → 覆盖 exe → 干跑验证 → 恢复任务；配置与日志不动

## 故障排查

| 症状 | 处理 |
|---|---|
| 双击 exe 窗口一闪而过 | 正常——命令行程序，请在 PowerShell 中运行 |
| `Could not parse the JSON file` / `'0xEF' is invalid` | 配置混入全角符号，`Select-String -Pattern "，|：|“|”"` 定位后改半角 |
| token 401/403 | AppKey/AppSecret 错误或连接器未启用 |
| `AD 绑定失败 credential is invalid` | 账号/密码错误；确认 `DOMAIN\\user` 双反斜杠 |
| 建号报 `0x52d` (ConstraintViolation) | 初始密码不满足域密码策略（见部署指南第 2 节） |
| 员工拉取 0 条 | 缺 `EmployeeExtraParams`（北森默认不返回离职记录）或连接器无员工数据权限 |
| 字段解析不对 | 临时开 `Beisen.LogRawSamples: true` 看原始响应比对字段名，查完关闭并清理日志 |
| DPAPI 解密失败 | 配置是在其他机器上加密的——重填明文后在本机重跑 `protect-config` |
| 任务结果码 3 | 上一轮尚未结束，本轮自动让路（进程互斥），无需处理 |
| 日志中文乱码 | 显示问题：`Get-Content` 加 `-Encoding UTF8` |

## 已知边界

- 单向同步：北森 → AD；AD 侧手工改动会在下轮被北森值覆盖（未纳管账号除外）
- 北森侧字段为空时不清空 AD 对应属性（保守设计）
- 特权账号（adminCount=1）不自动管理，其离职回收需人工流程
- 员工接口的字段解析采用多候选键容错，不同租户如字段命名差异可通过日志样本比对调整

## 免责声明

本项目为社区开源工具，与北森（Beisen）官方无任何关联。使用前请充分理解同步行为并在测试环境验证；对生产 AD 的任何操作请遵循"先干跑、后 apply、分层上线"的流程。

## License

[MIT](LICENSE)
