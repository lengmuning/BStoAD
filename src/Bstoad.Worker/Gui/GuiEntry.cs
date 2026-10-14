using System.Runtime.InteropServices;

namespace Bstoad.Worker.Gui;

internal static class GuiEntry
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("图形界面仅支持 Windows。命令行用法见 --help。");
            return 2;
        }

        // 双击启动时控制台是为本进程新建的（只有自己挂在上面）→ 释放掉，只留界面窗口；
        // 从 PowerShell 启动时控制台是共享的 → 保留，不影响用户的终端
        try
        {
            var list = new uint[8];
            if (GetConsoleProcessList(list, (uint)list.Length) <= 1) FreeConsole();
        }
        catch { /* 忽略 */ }

        var exitCode = 0;
        var ui = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (_, e) =>
                    MessageBox.Show(e.Exception.Message + "\n\n" + e.Exception, "BSTOAD 出现错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                exitCode = 1;
                MessageBox.Show(ex.ToString(), "BSTOAD 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        return exitCode;
    }
}
