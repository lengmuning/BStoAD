using Bstoad.ActiveDirectory;
using Bstoad.Beisen;
using Bstoad.Core.Options;
using Bstoad.Sync;
using Bstoad.Worker;
using Bstoad.Worker.Alerts;
using Bstoad.Worker.Gui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;

// 输出被重定向时（图形界面「运行」页、PowerShell 管道）统一用 UTF-8，避免中文乱码
if (Console.IsOutputRedirected)
{
    try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { /* 忽略 */ }
}

// 不带参数（双击 exe）或 gui → 图形界面；带参数 → 原有命令行（计划任务走这里，行为不变）
if (args.Length == 0 || (args.Length == 1 && args[0] == "gui"))
    return GuiEntry.Run();

var ctx = CommandContext.Parse(args);

// init-config 不需要宿主：生成模板后直接退出
if (ctx.Command == CommandKind.InitConfig)
{
    ConfigInitializer.Run();
    return 0;
}

var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"未找到配置文件: {configPath}");
    Console.Error.WriteLine("请先运行: Bstoad.Worker.exe init-config 生成模板（或双击 exe 用图形界面配置）。");
    return 2;
}

// protect-config 不需要宿主：加密凭据写回后直接退出
if (ctx.Command == CommandKind.ProtectConfig)
{
    ConfigProtector.Run(configPath);
    return 0;
}

if (ctx.Command == CommandKind.TestAlert)
    return await TestAlertCommand.RunAsync(configPath);

// 进程互斥：防止定时任务重叠（如 02:00 全量未跑完，02:07 增量又启动）导致并发写 AD
using var instanceMutex = new Mutex(initiallyOwned: true, @"Global\BSTOAD-Worker", out var mutexOwned);
if (!mutexOwned)
{
    Console.Error.WriteLine("检测到另一个 BSTOAD 实例正在运行，为防止并发写 AD，本次退出（退出码 3）。");
    return 3;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddEnvironmentVariables(prefix: "BSTOAD_")
    .AddUserSecrets<Program>(optional: true);

// Serilog 用代码配置而非 ReadFrom.Configuration：
// 1) 单文件发布下配置驱动的 sink 程序集扫描不可靠；
// 2) 日志路径锚定 exe 目录，计划任务工作目录设错也不丢日志。
var levelText = builder.Configuration["Serilog:MinimumLevel:Default"] ?? "Information";
if (!Enum.TryParse<LogEventLevel>(levelText, ignoreCase: true, out var minLevel))
    minLevel = LogEventLevel.Information;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "bstoad-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();
builder.Services.AddSerilog();

builder.Services.AddBeisenClients(builder.Configuration);
builder.Services.AddActiveDirectory(builder.Configuration);
builder.Services.AddSyncOrchestration(builder.Configuration);

// 告警（默认关闭；失败不影响同步）
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));
builder.Services.PostConfigure<AlertOptions>(AlertSecrets.Decrypt);
builder.Services.AddSingleton<AlertSender>();
builder.Services.AddSingleton<IAlertService, AlertService>();

// 透明解密 protect-config 加密过的凭据（明文值原样通过）
builder.Services.PostConfigure<BeisenOptions>(o => o.AppSecret = ConfigProtector.Unprotect(o.AppSecret));
builder.Services.PostConfigure<ActiveDirectoryOptions>(o => o.ServicePassword = ConfigProtector.Unprotect(o.ServicePassword));

builder.Services.AddSingleton(ctx);

builder.Services.PostConfigure<SyncOptions>(o =>
{
    if (ctx.Apply) o.DryRun = false;
    if (ctx.Full)
    {
        o.FullPull = true;
        o.ReconcileLeavers = true;
    }
    if (ctx.OrgsOnly) o.OrgsOnly = true;
    if (ctx.UsersOnly) o.UsersOnly = true;
    if (ctx.LeaversOnly) o.LeaversOnly = true;
    if (ctx.OnlyEmployeeIds.Length > 0) o.OnlyEmployeeIds = ctx.OnlyEmployeeIds;
});

builder.Services.AddHostedService<SyncWorker>();

Log.Information("启动: command={Cmd} apply={Apply} full={Full} orgsOnly={OrgsOnly} usersOnly={UsersOnly}",
    ctx.Command, ctx.Apply, ctx.Full, ctx.OrgsOnly, ctx.UsersOnly);

try
{
    await builder.Build().RunAsync();
}
finally
{
    Log.CloseAndFlush();
}

return ExitState.Code;
