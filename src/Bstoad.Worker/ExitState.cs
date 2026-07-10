namespace Bstoad.Worker;

/// <summary>
/// 进程退出码：0=成功；1=执行异常；2=完成但有失败/告警项；3=另一实例正在运行。
/// 计划任务的「上次运行结果」非 0 即说明需要看日志。
/// </summary>
public static class ExitState
{
    public static int Code;
}
