namespace Bstoad.Worker;

/// <summary>
/// 写 AD 的全局互斥：定时同步（每次一个进程）与事件监听（常驻进程）共用同一把锁，任何时刻只有一个同步在写 AD。
/// 命令行同步在进程启动时拿锁、退出时由系统释放；事件监听只在处理一批事件的那几秒里持有，平时不占锁，
/// 否则夜间全量会因为拿不到锁而让路。
/// </summary>
internal static class SyncLock
{
    public const string SyncMutexName = @"Global\BSTOAD-Worker";
    public const string ListenerMutexName = @"Global\BSTOAD-Listener";

    /// <summary>当前线程获取锁并一直持有（进程退出时释放）。拿不到时先提示再等待 wait。</summary>
    public static bool AcquireForProcess(Mutex mutex, TimeSpan wait, string waitingMessage)
    {
        if (TryWait(mutex, TimeSpan.Zero)) return true;
        if (wait <= TimeSpan.Zero) return false;
        Console.Error.WriteLine(waitingMessage);
        return TryWait(mutex, wait);
    }

    /// <summary>
    /// 只在 work 执行期间持有同步锁。Mutex 归属于获取它的线程，而 work 是异步的（await 之后可能换线程），
    /// 所以在一个专用线程上获取、阻塞等待 work 完成、再释放；work 本身照常在线程池上异步执行。
    /// 等待超过 wait 抛 TimeoutException。
    /// </summary>
    public static Task<T> RunExclusiveAsync<T>(Func<Task<T>> work, TimeSpan wait, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, SyncMutexName);
            bool owned;
            try
            {
                owned = WaitHandle.WaitAny(new[] { mutex, ct.WaitHandle }, wait) == 0;
            }
            catch (AbandonedMutexException)
            {
                owned = true; // 上一个持有者异常退出：锁已归本线程
            }
            if (!owned)
            {
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                else tcs.TrySetException(new TimeoutException("等待同步锁超时：另一个同步（定时任务或手动运行）仍在写 AD"));
                return;
            }
            try
            {
                tcs.TrySetResult(work().GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true,
            Name = "bstoad-sync-lock",
        };
        thread.Start();
        return tcs.Task;
    }

    private static bool TryWait(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }
}
