using System.Reflection;

namespace Bstoad.Worker;

public static class ConfigInitializer
{
    public const string TemplateResourceName = "Bstoad.Worker.appsettings.template.json";

    public static void Run()
    {
        var target = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(target))
        {
            Console.WriteLine($"配置已存在，不覆盖: {target}");
            Console.WriteLine("如需重新生成，请先删除或改名现有文件。");
            return;
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(TemplateResourceName)
            ?? throw new InvalidOperationException("未找到内嵌配置模板: " + TemplateResourceName);
        using var file = File.Create(target);
        stream.CopyTo(file);

        Console.WriteLine($"已生成配置模板: {target}");
        Console.WriteLine();
        Console.WriteLine("请填写 4 个占位符后再运行同步:");
        Console.WriteLine("  Beisen.AppKey / Beisen.AppSecret            ← 北森「我的连接器」");
        Console.WriteLine("  ActiveDirectory.ServiceAccount / ServicePassword ← AD 服务账号");
        Console.WriteLine();
        Console.WriteLine("建议随后收紧文件权限:");
        Console.WriteLine("  icacls appsettings.json /inheritance:r");
        Console.WriteLine("  icacls appsettings.json /grant \"SYSTEM:R\" \"Administrators:F\"");
    }
}
