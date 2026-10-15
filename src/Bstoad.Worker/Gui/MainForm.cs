using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace Bstoad.Worker.Gui;

internal sealed partial class MainForm : Form
{
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string ConfigPath = Path.Combine(BaseDir, "appsettings.json");
    private static readonly string StateDir = Path.Combine(BaseDir, "state");
    private static readonly string LogDir = Path.Combine(BaseDir, "logs");

    private ConfigDocument? _doc;
    private bool _dirty;
    private bool _loading;

    private readonly ProcessRunner _runner = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
    private readonly List<TabPage> _configPages = new();
    private readonly Dictionary<TextBox, string[]> _secretPaths = new();
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 9, 3, 3) };
    private readonly Button _saveBtn = new() { Text = "保存配置", AutoSize = true, Padding = new Padding(10, 3, 10, 3) };
    private readonly Button _revertBtn = new() { Text = "放弃修改", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Font = new Font("Microsoft YaHei UI", 9f);
        Text = $"BSTOAD 北森→AD 同步 v{AppVersion}";
        Size = new Size(1180, 820);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bstoad.Worker.app.ico");
            if (s != null) Icon = new Icon(s);
        }
        catch { /* 无图标不影响使用 */ }

        BuildBeisenTab();
        BuildAdTab();
        BuildSyncTab();
        BuildAlertTab();
        BuildEventTab();
        BuildRunTab();
        BuildPendingTab();
        BuildStatsTab();
        BuildTasksTab();
        BuildLogsTab();

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Color.FromArgb(245, 245, 245),
        };
        bottom.Controls.Add(_saveBtn);
        bottom.Controls.Add(_revertBtn);
        bottom.Controls.Add(_status);
        _saveBtn.Click += (_, _) =>
        {
            if (SaveConfig()) OfferListenerRestart();
        };
        _revertBtn.Click += (_, _) =>
        {
            if (!_dirty || Confirm("放弃所有未保存的修改，重新读取配置文件？")) LoadConfig();
        };

        Controls.Add(_tabs);
        Controls.Add(bottom);
        if (!IsAdmin()) Controls.Add(BuildAdminBanner());

        foreach (var page in _configPages) HookDirty(page);
        _tabs.SelectedIndexChanged += (_, _) => OnTabActivated(_tabs.SelectedTab);
        WireRunner();

        Load += (_, _) => LoadConfig();
        FormClosing += OnClosing;
    }

    // ---------------- 页面与通用 ----------------

    private TabPage NewPage(string title, bool isConfig)
    {
        var page = new TabPage(title) { UseVisualStyleBackColor = true, Padding = new Padding(4) };
        _tabs.TabPages.Add(page);
        if (isConfig) _configPages.Add(page);
        return page;
    }

    private TextBox SecretBox(FormBuilder f, string label, params string[] path)
    {
        var tb = f.Secret(label);
        _secretPaths[tb] = path;
        return tb;
    }

    private Control BuildAdminBanner()
    {
        var p = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(255, 244, 206),
            Padding = new Padding(10, 6, 10, 4),
        };
        p.Controls.Add(new Label
        {
            Text = "当前不是管理员身份：保存配置、管理计划任务可能因权限不足而失败。",
            AutoSize = true,
            Margin = new Padding(3, 5, 10, 3),
        });
        var b = new Button { Text = "以管理员身份重新打开", AutoSize = true };
        b.Click += (_, _) =>
        {
            if (_dirty && !Confirm("有未保存的修改，重新打开后会丢失。继续？")) return;
            try
            {
                Process.Start(new ProcessStartInfo(ProcessRunner.ExePath, "gui") { UseShellExecute = true, Verb = "runas" });
                _dirty = false;
                Close();
            }
            catch (Exception ex) { ShowError("未能以管理员身份启动：" + ex.Message); }
        };
        p.Controls.Add(b);
        return p;
    }

    private static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }

    private void HookDirty(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case TextBox tb: tb.TextChanged += (_, _) => MarkDirty(); break;
                case CheckBox cb: cb.CheckedChanged += (_, _) => MarkDirty(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => MarkDirty(); break;
                case ComboBox co: co.SelectedIndexChanged += (_, _) => MarkDirty(); break;
            }
            HookDirty(c);
        }
    }

    private void MarkDirty()
    {
        if (_loading || _doc == null) return;
        SetDirty(true);
    }

    private void SetDirty(bool dirty)
    {
        _dirty = dirty;
        var t = Text.TrimEnd(' ', '*');
        Text = dirty ? t + " *" : t;
        _saveBtn.Font = new Font(Font, dirty ? FontStyle.Bold : FontStyle.Regular);
        if (_doc != null)
            _status.Text = (dirty ? "有未保存的修改 · " : "") + "配置文件：" + ConfigPath;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_runner.IsRunning && !Confirm("还有任务在运行，关闭窗口会中止它。确定关闭？"))
        {
            e.Cancel = true;
            return;
        }
        if (_dirty)
        {
            var r = MessageBox.Show(this, "配置有未保存的修改，是否保存？", "BSTOAD",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes && !SaveConfig()) { e.Cancel = true; return; }
        }
        _runner.Stop();
    }

    private void OnTabActivated(TabPage? page)
    {
        if (page == _pendingPage && _pendingRows.Count == 0) LoadPending();
        else if (page == _statsPage) LoadStats();
        else if (page == _tasksPage) RefreshTasks();
        else if (page == _logsPage) LoadLogFiles();
    }

    // ---------------- 配置读写 ----------------

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            if (!Confirm($"没有找到配置文件：\n{ConfigPath}\n\n是否生成一份配置模板？"))
            {
                SetConfigEnabled(false, "未找到配置文件");
                return;
            }
            try { ConfigInitializer.WriteTemplate(ConfigPath); }
            catch (Exception ex)
            {
                ShowError("生成配置模板失败：" + ex.Message);
                SetConfigEnabled(false, "未找到配置文件");
                return;
            }
        }

        try
        {
            _doc = ConfigDocument.Load(ConfigPath);
        }
        catch (JsonException ex)
        {
            _doc = null;
            SetConfigEnabled(false, "配置文件格式错误");
            var msg = "配置文件格式有误，图形界面不会覆盖它：\n\n" + ex.Message +
                      "\n\n最常见的原因是手工编辑时混入了中文全角符号（，：“”）。\n是否用记事本打开修正？";
            if (Confirm(msg))
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{ConfigPath}\"") { UseShellExecute = true });
            return;
        }
        catch (Exception ex)
        {
            _doc = null;
            SetConfigEnabled(false, "读取配置失败");
            ShowError("读取配置失败：" + ex.Message);
            return;
        }

        _loading = true;
        try
        {
            LoadBeisen();
            LoadAd();
            LoadSync();
            LoadAlerts();
            LoadEvents();
            foreach (var (box, path) in _secretPaths)
            {
                box.Text = "";
                var raw = _doc.GetString("", path);
                box.PlaceholderText = !_doc.IsSecretSet(path) ? "未设置"
                    : ConfigProtector.IsProtected(raw) ? "已设置（已加密），留空表示不修改"
                    : "已设置（明文，保存时自动加密），留空表示不修改";
            }
        }
        finally
        {
            _loading = false;
        }
        SetConfigEnabled(true, null);
        SetDirty(false);
    }

    private void SetConfigEnabled(bool enabled, string? reason)
    {
        foreach (var p in _configPages)
            foreach (Control c in p.Controls) c.Enabled = enabled;
        _saveBtn.Enabled = enabled;
        _revertBtn.Enabled = true;
        if (reason != null) _status.Text = reason + "：" + ConfigPath;
    }

    /// <summary>把界面上的值写入内存中的配置文档（不落盘）。测试连接前也会调用。</summary>
    private bool CollectAll()
    {
        if (_doc == null)
        {
            ShowError("配置文件未能加载，请先修正配置文件。");
            return false;
        }
        CollectBeisen();
        CollectAd();
        CollectSync();
        CollectAlerts();
        CollectEvents();

        foreach (var (box, path) in _secretPaths)
        {
            if (box.Text.Length == 0) continue;
            _doc.SetSecret(box.Text, path);
            box.Text = "";
            box.PlaceholderText = "已更新（保存后生效）";
        }

        // 配置里残留的明文密钥一并加密
        foreach (var path in ConfigProtector.SecretPaths)
        {
            var raw = _doc.GetString("", path);
            if (!string.IsNullOrWhiteSpace(raw) && !ConfigProtector.IsProtected(raw) &&
                !raw.StartsWith("<PLACEHOLDER", StringComparison.Ordinal))
                _doc.SetSecret(raw, path);
        }
        return true;
    }

    private bool SaveConfig()
    {
        if (!CollectAll()) return false;

        var problems = ValidateConfig();
        if (problems.Count > 0 &&
            !Confirm("发现以下问题：\n\n· " + string.Join("\n· ", problems) + "\n\n仍要保存吗？"))
            return false;

        try
        {
            _doc!.Save();
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("没有写入配置文件的权限。请以管理员身份运行本程序后再保存。");
            return false;
        }
        catch (Exception ex)
        {
            ShowError("保存失败：" + ex.Message);
            return false;
        }

        LoadConfig();
        _status.Text = $"已保存 {DateTime.Now:HH:mm:ss}（原文件备份为 appsettings.json.bak）· 配置文件：{ConfigPath}";
        return true;
    }

    private List<string> ValidateConfig()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(_bAppKey.Text)) p.Add("北森 AppKey 未填写");
        if (!_doc!.IsSecretSet("Beisen", "AppSecret")) p.Add("北森 AppSecret 未填写");
        if (string.IsNullOrWhiteSpace(_aAccount.Text)) p.Add("AD 服务账号未填写");
        if (!_doc.IsSecretSet("ActiveDirectory", "ServicePassword")) p.Add("AD 服务账号密码未填写");
        if (!LooksLikeDn(_aBaseOu.Text)) p.Add("同步根 OU 不像有效的 DN（应形如 OU=xx,DC=corp,DC=com）");
        if (!LooksLikeDn(_aLeaverOu.Text)) p.Add("离职归档 OU 不像有效的 DN");
        if (string.Equals(_aBaseOu.Text.Trim(), _aLeaverOu.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            p.Add("离职归档 OU 不能与同步根 OU 相同");
        foreach (var line in _aNeverTouch.Lines.Where(l => l.Trim().Length > 0))
            if (!LooksLikeDn(line)) p.Add("禁区中有一行不像有效的 DN：" + line.Trim());
        if (_alEnabled.Checked && !_wcOn.Checked && !_dtOn.Checked && !_fsOn.Checked)
            p.Add("已启用告警，但没有启用任何渠道");
        ValidateEvents(p);
        return p;
    }

    private static bool LooksLikeDn(string s)
    {
        s = s.Trim();
        return s.Contains("DC=", StringComparison.OrdinalIgnoreCase) && s.Contains('=') &&
               !s.Contains('，') && !s.Contains('＝');
    }

    // ---------------- 后台执行 ----------------

    /// <summary>在后台线程执行耗时操作（网络/LDAP），结果显示在 result 标签上。</summary>
    private async void RunBackground(Button? button, Label? result, Func<(bool Ok, string Message)> work)
    {
        if (button != null) button.Enabled = false;
        UseWaitCursor = true;
        if (result != null)
        {
            result.ForeColor = FormBuilder.HintColor;
            result.Text = "处理中…";
        }
        try
        {
            var (ok, msg) = await Task.Run(work);
            if (result != null) FormBuilder.SetResult(result, ok, msg);
            else if (!ok) ShowError(msg);
        }
        catch (Exception ex)
        {
            if (result != null) FormBuilder.SetResult(result, false, Describe(ex));
            else ShowError(Describe(ex));
        }
        finally
        {
            if (button != null) button.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private static string Describe(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        var msg = ex.Message;
        if (ex.InnerException != null && !msg.Contains(ex.InnerException.Message))
            msg += "（" + ex.InnerException.Message + "）";
        return msg;
    }

    private bool Confirm(string text) =>
        MessageBox.Show(this, text, "BSTOAD", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void ShowError(string text) =>
        MessageBox.Show(this, text, "BSTOAD", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void ShowInfo(string text) =>
        MessageBox.Show(this, text, "BSTOAD", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
