namespace Bstoad.Worker;

public enum CommandKind { Sync, MatchAd, MigrateMarker, InitConfig, ProtectConfig, Gui, TestAlert, Listen, ProbeEmployee }

public sealed class CommandContext
{
    public CommandKind Command { get; init; } = CommandKind.Sync;
    public bool Apply { get; init; }
    public bool Full { get; init; }
    public bool OrgsOnly { get; init; }
    public bool UsersOnly { get; init; }
    public bool LeaversOnly { get; init; }
    public string[] OnlyEmployeeIds { get; init; } = Array.Empty<string>();
    public string[] ProbeUserIds { get; init; } = Array.Empty<string>();

    public static CommandContext Parse(string[] args)
    {
        var cmd = CommandKind.Sync;
        var apply = false;
        var full = false;
        var orgsOnly = false;
        var usersOnly = false;
        var leaversOnly = false;
        var only = new List<string>();
        var probeIds = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "sync": cmd = CommandKind.Sync; break;
                case "match-ad": cmd = CommandKind.MatchAd; break;
                case "migrate-marker": cmd = CommandKind.MigrateMarker; break;
                case "init-config": cmd = CommandKind.InitConfig; break;
                case "protect-config": cmd = CommandKind.ProtectConfig; break;
                case "gui": cmd = CommandKind.Gui; break;
                case "test-alert": cmd = CommandKind.TestAlert; break;
                case "listen": cmd = CommandKind.Listen; break;
                case "probe-employee": cmd = CommandKind.ProbeEmployee; break;
                case "--user-id":
                    if (i + 1 < args.Length)
                        probeIds.AddRange(args[++i].Split(',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--apply": apply = true; break;
                case "--full": full = true; break;
                case "--orgs-only": orgsOnly = true; break;
                case "--users-only": usersOnly = true; break;
                case "--leavers-only": leaversOnly = true; break;
                case "--only":
                    if (i + 1 < args.Length)
                        only.AddRange(args[++i].Split(',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }

        if (orgsOnly && usersOnly)
        {
            Console.Error.WriteLine("错误: --orgs-only 和 --users-only 不能同时使用");
            Environment.Exit(2);
        }

        return new CommandContext
        {
            Command = cmd,
            Apply = apply,
            Full = full,
            OrgsOnly = orgsOnly,
            UsersOnly = usersOnly,
            LeaversOnly = leaversOnly,
            OnlyEmployeeIds = only.ToArray(),
            ProbeUserIds = probeIds.ToArray(),
        };
    }

    public static void PrintHelp()
    {
        Console.WriteLine(@"
BSTOAD · 北森 → AD 同步

用法:
  bstoad sync [--apply] [--full] [--orgs-only | --users-only] [--only 工号[,工号...]]
      默认子命令。
      --apply       实际写 AD（默认仅打印决策）
      --full        全量拉员工 + 跑离职差集（增量模式下差集不会运行）
      --orgs-only   只同步组织架构（建 OU 树），跳过员工和差集 ← 分层测试用
      --users-only  只同步员工，跳过建 OU（假设 OU 树已就绪） ← 分层测试用
      --only        试点：只处理指定工号（可逗号分隔多个），其他员工全部跳过，
                    离职差集自动禁用 ← 单人/小批量验证用
      --leavers-only 只处理离职员工（离职人员部门/离职状态），在职全部跳过
                    ← 先归档禁用存量离职人员用；建议配合 --full

  bstoad listen [--apply] [--only 工号[,工号...]]
      事件监听（常驻）：从事件服务拉取北森事件，有变动时自动增量同步到 AD
      需先在配置 EventFeed 段（图形界面「事件订阅」页）启用并填写地址和令牌
      由计划任务 BSTOAD-Events 开机自启；与定时同步共用同一把锁，不会并发写 AD
      不加 --apply 为干跑（只打印决策、不保存处理进度）；--only 用于试点

  bstoad probe-employee --user-id userId[,userId...]
      核对「按 userId 精确查询」用的北森接口（只读北森，不碰 AD）
      打印响应结构（值已隐去，可转发）和解析出的工号姓名（仅本机查看）

  bstoad match-ad [--apply]
      上线前预匹配：P1 SAM=工号 / P2 邮箱 → 回填 employeeID 到老账号
      仅回填 employeeID，不打纳管标记
      默认 dry-run，加 --apply 才真写

  bstoad migrate-marker [--apply]
      扫北森同步根 OU 下所有 employeeID 非空 + 启用的账号补打纳管标记
      默认 dry-run，加 --apply 才真写

  bstoad init-config
      在 exe 同目录生成 appsettings.json 配置模板（已存在则不覆盖）
      单文件分发时的第一步

  bstoad protect-config
      用 DPAPI(本机) 加密配置里的密钥、密码、告警 Webhook、事件服务令牌并写回
      加密后文件被拷走也无法解密；换服务器需重填明文再跑一次

  bstoad gui        打开图形界面（直接双击 exe 也会打开）
  bstoad test-alert 向已启用的告警渠道发送一条测试消息

  bstoad --help    显示帮助

分层测试推荐顺序:
  1. bstoad sync --orgs-only                 # 干跑看会建哪些 OU
  2. bstoad sync --orgs-only --apply         # 真建 OU 树，停下来去 AD 检查
  3. bstoad sync --users-only                # 干跑看决策树
  4. bstoad sync --users-only --apply --full # 真同步人员（全量）
");
    }
}
