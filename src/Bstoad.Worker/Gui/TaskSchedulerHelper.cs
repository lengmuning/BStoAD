using System.Diagnostics;
using System.Text;

namespace Bstoad.Worker.Gui;

internal sealed record TaskInfo(string Name, string State, string LastRun, string LastResult, string NextRun, string Action);

/// <summary>
/// 计划任务管理：通过 PowerShell ScheduledTasks 模块完成（与部署手册中的命令一致）。
/// </summary>
internal static class TaskSchedulerHelper
{
    public const string SyncTask = "BSTOAD-Sync";
    public const string FullTask = "BSTOAD-FullSync";
    public static readonly string[] AllTasks = { SyncTask, FullTask };

    public static List<TaskInfo> Query()
    {
        var script = """
            $ErrorActionPreference = 'SilentlyContinue'
            foreach ($n in @('BSTOAD-Sync','BSTOAD-FullSync')) {
              $t = Get-ScheduledTask -TaskName $n
              if (-not $t) { "$n|未安装||||"; continue }
              $i = $t | Get-ScheduledTaskInfo
              $lr = ''; if ($i.LastRunTime -and $i.LastRunTime.Year -gt 2000) { $lr = $i.LastRunTime.ToString('yyyy-MM-dd HH:mm:ss') }
              $nr = ''; if ($i.NextRunTime) { $nr = $i.NextRunTime.ToString('yyyy-MM-dd HH:mm:ss') }
              $a = $t.Actions | Select-Object -First 1
              "$n|$($t.State)|$lr|$($i.LastTaskResult)|$nr|$($a.Execute) $($a.Arguments)"
            }
            """;
        var output = RunPowerShell(script, out _);
        var list = new List<TaskInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length < 6) continue;
            list.Add(new TaskInfo(p[0], StateText(p[1]), p[2], ResultText(p[3]), p[4], p[5]));
        }
        return list;
    }

    public static string Install(string exePath, int hourlyMinute, TimeSpan fullAt)
    {
        var exe = Ps(exePath);
        var dir = Ps(Path.GetDirectoryName(exePath)!);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
            Register-ScheduledTask -TaskName 'BSTOAD-Sync' -Description '北森→AD 每小时增量同步' -Force `
              -Action (New-ScheduledTaskAction -Execute {{exe}} -Argument 'sync --apply' -WorkingDirectory {{dir}}) `
              -Trigger (New-ScheduledTaskTrigger -Once -At ([DateTime]::Today.AddMinutes({{hourlyMinute}})) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration ([TimeSpan]::FromDays(3650))) `
              -Settings $settings -User 'SYSTEM' -RunLevel Highest | Out-Null
            Register-ScheduledTask -TaskName 'BSTOAD-FullSync' -Description '北森→AD 每日全量同步+离职差集' -Force `
              -Action (New-ScheduledTaskAction -Execute {{exe}} -Argument 'sync --apply --full' -WorkingDirectory {{dir}}) `
              -Trigger (New-ScheduledTaskTrigger -Daily -At ([DateTime]::Today.Add([TimeSpan]::FromMinutes({{(int)fullAt.TotalMinutes}})))) `
              -Settings $settings -User 'SYSTEM' -RunLevel Highest | Out-Null
            'OK'
            """;
        return RunChecked(script);
    }

    public static string Enable(bool enable)
    {
        var verb = enable ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        return RunChecked($"$ErrorActionPreference='Stop'; foreach($n in @('BSTOAD-Sync','BSTOAD-FullSync')){{ if(Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue){{ {verb} -TaskName $n | Out-Null }} }}; 'OK'");
    }

    public static string StartNow(string taskName) =>
        RunChecked($"$ErrorActionPreference='Stop'; Start-ScheduledTask -TaskName {Ps(taskName)}; 'OK'");

    public static string Uninstall() =>
        RunChecked("$ErrorActionPreference='Stop'; foreach($n in @('BSTOAD-Sync','BSTOAD-FullSync')){ if(Get-ScheduledTask -TaskName $n -ErrorAction SilentlyContinue){ Unregister-ScheduledTask -TaskName $n -Confirm:$false } }; 'OK'");

    private static string RunChecked(string script)
    {
        var output = RunPowerShell(script, out var error);
        if (!output.Contains("OK"))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output : error);
        return output;
    }

    private static string RunPowerShell(string script, out string error)
    {
        var full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8\n" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell");
        var errTask = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        error = errTask.Result.Trim();
        return output;
    }

    /// <summary>PowerShell 单引号字符串字面量。</summary>
    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    private static string StateText(string s) => s switch
    {
        "Ready" => "就绪",
        "Running" => "运行中",
        "Disabled" => "已停用",
        "Queued" => "排队中",
        _ => s,
    };

    public static string ResultText(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        if (!long.TryParse(code, out var c)) return code;
        return c switch
        {
            0 => "0 成功",
            1 => "1 执行异常",
            2 => "2 有失败或告警项",
            3 => "3 与其他实例重叠，已让路",
            267009 => "正在运行",
            267011 => "尚未运行过",
            267014 => "被手动终止",
            _ => code,
        };
    }
}
