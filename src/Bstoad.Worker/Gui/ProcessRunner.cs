using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Bstoad.Worker.Gui;

/// <summary>
/// 以子进程方式运行本 exe 的命令行（sync / match-ad ...），实时回传输出。
/// 走子进程而不是在界面进程里直接同步：与计划任务完全同一条执行路径，且共享进程互斥锁。
/// </summary>
internal sealed class ProcessRunner
{
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    private Process? _proc;

    public bool IsRunning => _proc is { HasExited: false };
    public string? CurrentArgs { get; private set; }

    /// <summary>参数：文本、是否错误流。在线程池线程上触发。</summary>
    public event Action<string, bool>? Output;
    /// <summary>参数：退出码。在线程池线程上触发。</summary>
    public event Action<int>? Exited;

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Bstoad.Worker.exe");

    public void Start(string args)
    {
        if (IsRunning) throw new InvalidOperationException("已有任务在运行，请等待完成或先停止");

        var psi = new ProcessStartInfo(ExePath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(Ansi.Replace(e.Data, ""), false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(Ansi.Replace(e.Data, ""), true); };
        p.Exited += (_, _) =>
        {
            try { p.WaitForExit(); } catch { /* 确保异步输出读完 */ }
            Exited?.Invoke(p.ExitCode);
        };

        CurrentArgs = args;
        _proc = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    public void Stop()
    {
        try { if (IsRunning) _proc!.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
    }

    public static string ExitCodeText(int code) => code switch
    {
        0 => "成功",
        1 => "执行异常",
        2 => "完成，但有失败或告警项",
        3 => "另一个实例正在运行（可能是计划任务），本次未执行",
        _ => "退出码 " + code,
    };
}
