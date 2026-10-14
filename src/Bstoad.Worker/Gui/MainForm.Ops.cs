using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bstoad.ActiveDirectory;
using Bstoad.Core.Models;
using Bstoad.Core.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker.Gui;

internal sealed record PendingRow(string Category, string Level, string Name, string Detail, string? Dn, string Key);

internal sealed partial class MainForm
{
    private const string LiveDisabledCategory = "部门 OU 中的禁用账号";

    // ---- 运行 ----
    private TabPage _runPage = null!;
    private RadioButton _rsAll = null!, _rsOrgs = null!, _rsUsers = null!, _rsLeavers = null!;
    private CheckBox _rFull = null!, _rApply = null!;
    private TextBox _rOnly = null!;
    private Button _rStart = null!, _rMatch = null!, _rMigrate = null!, _rStop = null!;
    private Label _rCmd = null!;
    private RichTextBox _rOut = null!;
    private Action<int>? _onRunExit;

    // ---- 待处理 ----
    private TabPage _pendingPage = null!;
    private DataGridView _pGrid = null!;
    private ComboBox _pFilter = null!;
    private Label _pSource = null!;
    private readonly List<PendingRow> _pendingRows = new();
    private readonly List<PendingRow> _liveRows = new();

    // ---- 统计 ----
    private TabPage _statsPage = null!;
    private StatsChart _chart = null!;
    private DataGridView _stGrid = null!;
    private CheckBox _stDry = null!;
    private ComboBox _stDays = null!;

    // ---- 计划任务 ----
    private TabPage _tasksPage = null!;
    private DataGridView _tGrid = null!;
    private NumericUpDown _tMinute = null!;
    private DateTimePicker _tFullTime = null!;
    private Label _tResult = null!;

    // ---- 日志 ----
    private TabPage _logsPage = null!;
    private ComboBox _lFiles = null!, _lLevel = null!;
    private TextBox _lSearch = null!;
    private RichTextBox _lView = null!;

    // =====================================================================
    // 运行
    // =====================================================================

    private void BuildRunTab()
    {
        _runPage = NewPage("运行", isConfig: false);
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 6),
        };

        var r1 = Row();
        r1.Controls.Add(Lbl("同步范围："));
        _rsAll = Radio("组织 + 人员", true);
        _rsOrgs = Radio("仅组织架构");
        _rsUsers = Radio("仅人员");
        _rsLeavers = Radio("仅离职人员（归档并禁用）");
        r1.Controls.AddRange(new Control[] { _rsAll, _rsOrgs, _rsUsers, _rsLeavers });

        var r2 = Row();
        _rFull = new CheckBox { Text = "全量（拉取全部员工，并做离职对账）", AutoSize = true, Margin = new Padding(3, 6, 16, 3) };
        r2.Controls.Add(_rFull);
        r2.Controls.Add(Lbl("试点工号："));
        _rOnly = new TextBox { Width = 220, PlaceholderText = "多个用逗号分隔；留空 = 全部" };
        r2.Controls.Add(_rOnly);

        var r3 = Row();
        _rApply = new CheckBox
        {
            Text = "写入 AD（不勾选 = 干跑，只预览将要做什么，不做任何修改）",
            AutoSize = true,
            ForeColor = Color.FromArgb(200, 30, 30),
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 6, 3, 3),
        };
        r3.Controls.Add(_rApply);

        var r4 = Row();
        _rStart = Btn("开始同步");
        _rMatch = Btn("预匹配老账号（match-ad）");
        _rMigrate = Btn("补纳管标记（migrate-marker）");
        _rStop = Btn("停止");
        var clear = Btn("清空输出");
        _rStop.Enabled = false;
        r4.Controls.AddRange(new Control[] { _rStart, _rMatch, _rMigrate, _rStop, clear });

        _rCmd = new Label { AutoSize = true, ForeColor = FormBuilder.HintColor, Margin = new Padding(6, 4, 3, 2) };

        top.Controls.AddRange(new Control[] { r1, r2, r3, r4, _rCmd });

        _rOut = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Microsoft YaHei UI", 9f),
            WordWrap = false,
            DetectUrls = false,
        };
        _runPage.Controls.Add(_rOut);
        _runPage.Controls.Add(top);

        foreach (var c in new Control[] { _rsAll, _rsOrgs, _rsUsers, _rsLeavers, _rFull, _rOnly, _rApply })
        {
            if (c is RadioButton rb) rb.CheckedChanged += (_, _) => UpdateRunPreview();
            else if (c is CheckBox cb) cb.CheckedChanged += (_, _) => UpdateRunPreview();
            else c.TextChanged += (_, _) => UpdateRunPreview();
        }
        _rStart.Click += (_, _) => StartRun(BuildSyncArgs());
        _rMatch.Click += (_, _) => StartRun("match-ad" + (_rApply.Checked ? " --apply" : ""));
        _rMigrate.Click += (_, _) => StartRun("migrate-marker" + (_rApply.Checked ? " --apply" : ""));
        _rStop.Click += (_, _) => _runner.Stop();
        clear.Click += (_, _) => _rOut.Clear();
        UpdateRunPreview();

        static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
        static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(3, 7, 3, 3) };
        static RadioButton Radio(string t, bool on = false) => new() { Text = t, AutoSize = true, Checked = on, Margin = new Padding(3, 5, 12, 3) };
        static Button Btn(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
    }

    private string BuildSyncArgs()
    {
        var a = new List<string> { "sync" };
        var only = string.Join(",", SplitList(_rOnly.Text));
        if (_rsLeavers.Checked)
        {
            a.AddRange(new[] { "--users-only", "--full", "--leavers-only" });
        }
        else
        {
            if (_rsOrgs.Checked) a.Add("--orgs-only");
            if (_rsUsers.Checked) a.Add("--users-only");
            // 试点工号需要全量拉取才能保证找到人（增量只看最近变动）
            if (!_rsOrgs.Checked && (_rFull.Checked || only.Length > 0)) a.Add("--full");
        }
        if (only.Length > 0 && !_rsOrgs.Checked) { a.Add("--only"); a.Add(only); }
        if (_rApply.Checked) a.Add("--apply");
        return string.Join(" ", a);
    }

    private void UpdateRunPreview()
    {
        _rFull.Enabled = !_rsOrgs.Checked && !_rsLeavers.Checked;
        _rOnly.Enabled = !_rsOrgs.Checked;
        _rCmd.Text = "将执行：Bstoad.Worker.exe " + BuildSyncArgs();
    }

    private void WireRunner()
    {
        _runner.Output += (line, isErr) => BeginInvoke(() => AppendRun(line, isErr));
        _runner.Exited += code => BeginInvoke(() =>
        {
            AppendRun($"■ {DateTime.Now:HH:mm:ss} 结束：{ProcessRunner.ExitCodeText(code)}", false, code == 0 ? Color.LightGreen : Color.Orange);
            SetRunning(false);
            var cb = _onRunExit;
            _onRunExit = null;
            cb?.Invoke(code);
        });
    }

    /// <summary>以子进程运行一条命令。返回是否已启动。</summary>
    private bool StartRun(string args, Action<int>? onExit = null, bool showRunTab = true)
    {
        if (_runner.IsRunning)
        {
            ShowError("已有任务在运行，请等它结束或点「停止」。");
            return false;
        }
        if (_dirty)
        {
            var r = MessageBox.Show(this, "配置有未保存的修改。运行使用的是已保存的配置文件，是否先保存？",
                "BSTOAD", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes && !SaveConfig()) return false;
        }
        if (args.Contains("--apply") &&
            !Confirm($"即将写入 AD：\n\nBstoad.Worker.exe {args}\n\n建议先不勾「写入 AD」干跑一次看结果。确定执行吗？"))
            return false;

        try
        {
            _onRunExit = onExit;
            AppendRun($"▶ {DateTime.Now:HH:mm:ss}  Bstoad.Worker.exe {args}", false, Color.DeepSkyBlue);
            _runner.Start(args);
            SetRunning(true);
            if (showRunTab) _tabs.SelectedTab = _runPage;
            return true;
        }
        catch (Exception ex)
        {
            _onRunExit = null;
            ShowError("启动失败：" + ex.Message);
            return false;
        }
    }

    private void SetRunning(bool running)
    {
        _rStart.Enabled = _rMatch.Enabled = _rMigrate.Enabled = !running;
        _rStop.Enabled = running;
        _runPage.Text = running ? "运行 ●" : "运行";
    }

    private void AppendRun(string line, bool isErr, Color? color = null)
    {
        if (_rOut.TextLength > 3_000_000)
        {
            _rOut.Select(0, _rOut.TextLength / 2);
            _rOut.SelectedText = "";
        }
        var c = color ?? (isErr || line.Contains("[ERR]") ? Color.FromArgb(255, 110, 110)
            : line.Contains("[WRN]") ? Color.FromArgb(255, 190, 90)
            : _rOut.ForeColor);
        _rOut.SelectionStart = _rOut.TextLength;
        _rOut.SelectionLength = 0;
        _rOut.SelectionColor = c;
        _rOut.AppendText(line + Environment.NewLine);
        _rOut.ScrollToCaret();
    }

    // =====================================================================
    // 待处理
    // =====================================================================

    private void BuildPendingTab()
    {
        _pendingPage = NewPage("待处理", isConfig: false);
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(10, 8, 10, 6),
        };

        var r1 = new FlowLayoutPanel { AutoSize = true };
        var load = new Button { Text = "读取最近结果", AutoSize = true };
        var scan = new Button { Text = "立即扫描（干跑全量，约 1~2 分钟）", AutoSize = true };
        _pSource = new Label { AutoSize = true, ForeColor = FormBuilder.HintColor, Margin = new Padding(8, 8, 3, 3) };
        r1.Controls.AddRange(new Control[] { load, scan, _pSource });

        var r2 = new FlowLayoutPanel { AutoSize = true };
        _pFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        var export = new Button { Text = "导出 Excel（CSV）", AutoSize = true };
        var copy = new Button { Text = "复制选中行的 DN", AutoSize = true };
        r2.Controls.AddRange(new Control[] { new Label { Text = "类别：", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _pFilter, export, copy });

        var r3 = new FlowLayoutPanel { AutoSize = true };
        var query = new Button { Text = "查询部门 OU 中被禁用的账号", AutoSize = true };
        var enable = new Button { Text = "启用选中的账号", AutoSize = true };
        var del = new Button { Text = "删除选中的空 OU", AutoSize = true };
        r3.Controls.AddRange(new Control[] { query, enable, del });

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            ForeColor = FormBuilder.HintColor,
            Margin = new Padding(3, 4, 3, 2),
            Text = "清单来自最近一次全量同步（每天自动更新）。「启用账号」「删除空 OU」只在你选中并确认后执行；删除仅限同步根 OU 下已被北森撤销且完全为空的 OU。",
        };
        top.Controls.AddRange(new Control[] { r1, r2, r3, hint });

        _pGrid = NewGrid();
        _pGrid.Columns.Add("Category", "类别");
        _pGrid.Columns.Add("Level", "级别");
        _pGrid.Columns.Add("Name", "名称");
        _pGrid.Columns.Add("Detail", "说明");
        _pGrid.Columns.Add("Dn", "DN");
        _pGrid.Columns["Category"]!.Width = 130;
        _pGrid.Columns["Level"]!.Width = 70;
        _pGrid.Columns["Name"]!.Width = 140;
        _pGrid.Columns["Detail"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _pGrid.Columns["Dn"]!.Width = 320;

        _pendingPage.Controls.Add(_pGrid);
        _pendingPage.Controls.Add(top);

        load.Click += (_, _) => LoadPending();
        scan.Click += (_, _) =>
        {
            if (StartRun("sync --full", _ => LoadPending(), showRunTab: false))
                _pSource.Text = "扫描中……完成后自动刷新（实时输出见「运行」页）";
        };
        _pFilter.SelectedIndexChanged += (_, _) => RenderPending();
        export.Click += (_, _) => ExportPending();
        copy.Click += (_, _) =>
        {
            var dns = SelectedPending().Select(r => r.Dn).Where(d => !string.IsNullOrEmpty(d)).ToList();
            if (dns.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, dns));
        };
        query.Click += (_, _) => QueryDisabledAccounts(query);
        enable.Click += (_, _) => EnableSelected(enable);
        del.Click += (_, _) => DeleteSelectedOus(del);
    }

    private void LoadPending()
    {
        _pendingRows.Clear();
        var f = Path.Combine(StateDir, "pending.json");
        if (!File.Exists(f))
        {
            _pSource.Text = "还没有扫描结果：每日全量同步后会自动生成，也可以点「立即扫描」。";
        }
        else
        {
            try
            {
                var report = JsonSerializer.Deserialize<PendingReport>(File.ReadAllText(f), new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                }) ?? new PendingReport();
                foreach (var e in report.Events)
                    _pendingRows.Add(new PendingRow(e.Category, LevelText(e.Level), e.Name, e.Detail, e.Dn, e.Key));
                _pSource.Text = $"数据时间：{report.GeneratedAt:yyyy-MM-dd HH:mm}（{(report.DryRun ? "干跑扫描" : "正式同步")}），共 {report.Events.Count} 条";
            }
            catch (Exception ex)
            {
                _pSource.Text = "读取扫描结果失败：" + ex.Message;
            }
        }
        RenderPending();
    }

    private void RenderPending()
    {
        var all = _pendingRows.Concat(_liveRows).ToList();
        var cats = new[] { "全部" }.Concat(all.Select(r => r.Category).Distinct()).ToList();
        var cur = _pFilter.SelectedItem as string ?? "全部";
        if (!_pFilter.Items.Cast<string>().SequenceEqual(cats))
        {
            _pFilter.Items.Clear();
            _pFilter.Items.AddRange(cats.Cast<object>().ToArray());
            _pFilter.SelectedItem = cats.Contains(cur) ? cur : "全部";
            return; // SelectedIndexChanged 会再次调用本方法
        }

        _pGrid.Rows.Clear();
        foreach (var r in all.Where(r => cur == "全部" || r.Category == cur))
        {
            var i = _pGrid.Rows.Add(r.Category, r.Level, r.Name, r.Detail, r.Dn);
            _pGrid.Rows[i].Tag = r;
            if (r.Level == "严重") _pGrid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(200, 30, 30);
        }
    }

    private List<PendingRow> SelectedPending() =>
        _pGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PendingRow>().ToList();

    private void QueryDisabledAccounts(Button btn)
    {
        if (!CollectAll()) return;
        var doc = _doc!;
        RunBackgroundThen(btn, () =>
        {
            using var sp = GuiServices.Build(doc);
            var ad = sp.GetRequiredService<IAdProvisioner>();
            var o = sp.GetRequiredService<IOptions<ActiveDirectoryOptions>>().Value;
            return ad.ListManagedUsersUnder(o.BaseUserOu)
                .Where(u => u.IsDisabled && !InOu(u.ParentOu, o.LeaverOu))
                .Select(u => new PendingRow(LiveDisabledCategory, "需要关注", u.DisplayName ?? u.SamAccountName,
                    $"工号 {u.EmployeeId}：在部门 OU 中但处于禁用状态（可能是复职待启用，也可能是有意禁用）", u.Dn, u.EmployeeId ?? u.Dn))
                .ToList();
        }, rows =>
        {
            _liveRows.Clear();
            _liveRows.AddRange(rows);
            RenderPending();
            _pFilter.SelectedItem = rows.Count > 0 ? LiveDisabledCategory : _pFilter.SelectedItem;
            ShowInfo(rows.Count == 0 ? "部门 OU 中没有被禁用的纳管账号。" : $"找到 {rows.Count} 个，已列在「{LiveDisabledCategory}」类别下。核实后可选中并点「启用选中的账号」。");
        });
    }

    private void EnableSelected(Button btn)
    {
        var rows = SelectedPending().Where(r => r.Category == LiveDisabledCategory && r.Dn != null).ToList();
        if (rows.Count == 0)
        {
            ShowError($"请先点「查询部门 OU 中被禁用的账号」，再选中「{LiveDisabledCategory}」类别下的行。");
            return;
        }
        if (!Confirm($"将启用以下 {rows.Count} 个账号：\n\n" + string.Join("\n", rows.Take(15).Select(r => "· " + r.Name)) +
                     (rows.Count > 15 ? $"\n……等 {rows.Count} 个" : "") + "\n\n确定吗？"))
            return;
        if (!CollectAll()) return;
        var doc = _doc!;
        RunBackgroundThen(btn, () =>
        {
            using var sp = GuiServices.Build(doc);
            var ad = sp.GetRequiredService<IAdProvisioner>();
            var ok = new List<PendingRow>();
            var fail = new List<string>();
            foreach (var r in rows)
            {
                try { ad.EnableUser(r.Dn!); ok.Add(r); }
                catch (Exception ex) { fail.Add($"{r.Name}：{ex.Message}"); }
            }
            return (ok, fail);
        }, res =>
        {
            foreach (var r in res.ok) _liveRows.Remove(r);
            RenderPending();
            ShowInfo($"已启用 {res.ok.Count} 个账号。" + (res.fail.Count > 0 ? "\n\n失败：\n" + string.Join("\n", res.fail) : ""));
        });
    }

    private void DeleteSelectedOus(Button btn)
    {
        var rows = SelectedPending().Where(r => r.Category == SyncEventCategory.OrgAbandoned && r.Dn != null).ToList();
        if (rows.Count == 0)
        {
            ShowError($"请选中「{SyncEventCategory.OrgAbandoned}」类别下的行。");
            return;
        }
        if (!CollectAll()) return;
        var baseOu = _aBaseOu.Text.Trim();
        var outside = rows.Where(r => !r.Dn!.EndsWith("," + baseOu, StringComparison.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0)
        {
            ShowError("以下 OU 不在同步根 OU 之下，不允许在这里删除：\n" + string.Join("\n", outside.Select(r => r.Dn)));
            return;
        }
        if (!Confirm($"将删除以下 {rows.Count} 个 OU（仅当 OU 完全为空时才会删除，有内容的会跳过）：\n\n" +
                     string.Join("\n", rows.Select(r => "· " + r.Dn)) + "\n\n确定吗？"))
            return;
        var doc = _doc!;
        RunBackgroundThen(btn, () =>
        {
            using var sp = GuiServices.Build(doc);
            var factory = sp.GetRequiredService<ILdapConnectionFactory>();
            var ok = new List<PendingRow>();
            var fail = new List<string>();
            foreach (var r in rows)
            {
                try { AdBrowser.DeleteEmptyOu(factory, r.Dn!); ok.Add(r); }
                catch (Exception ex) { fail.Add($"{r.Name}：{ex.Message}"); }
            }
            return (ok, fail);
        }, res =>
        {
            foreach (var r in res.ok) _pendingRows.Remove(r);
            RenderPending();
            ShowInfo($"已删除 {res.ok.Count} 个空 OU。" + (res.fail.Count > 0 ? "\n\n未删除：\n" + string.Join("\n", res.fail) : ""));
        });
    }

    private void ExportPending()
    {
        var rows = _pGrid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<PendingRow>().ToList();
        if (rows.Count == 0)
        {
            ShowError("当前没有可导出的内容。");
            return;
        }
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV 文件（Excel 可直接打开）|*.csv",
            FileName = $"BSTOAD待处理_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder("类别,级别,名称,说明,DN\r\n");
        foreach (var r in rows)
            sb.Append(string.Join(",", new[] { r.Category, r.Level, r.Name, r.Detail, r.Dn ?? "" }.Select(Csv))).Append("\r\n");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // 带 BOM，Excel 才能正确识别中文
        ShowInfo($"已导出 {rows.Count} 条到：\n{dlg.FileName}");

        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static bool InOu(string parentOu, string ou) =>
        parentOu.Equals(ou, StringComparison.OrdinalIgnoreCase) ||
        parentOu.EndsWith("," + ou, StringComparison.OrdinalIgnoreCase);

    private static string LevelText(SyncEventLevel l) => l switch
    {
        SyncEventLevel.Critical => "严重",
        SyncEventLevel.Warning => "需要关注",
        _ => "提示",
    };

    // =====================================================================
    // 统计
    // =====================================================================

    private void BuildStatsTab()
    {
        _statsPage = NewPage("统计", isConfig: false);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4) };
        _stDays = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _stDays.Items.AddRange(new object[] { "近 7 天", "近 14 天", "近 30 天" });
        _stDays.SelectedIndex = 1;
        _stDry = new CheckBox { Text = "包含干跑", AutoSize = true, Margin = new Padding(12, 6, 3, 3) };
        var refresh = new Button { Text = "刷新", AutoSize = true };
        top.Controls.AddRange(new Control[] { new Label { Text = "范围：", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _stDays, _stDry, refresh });

        _chart = new StatsChart { Dock = DockStyle.Top, Height = 260, Font = Font };
        _stGrid = NewGrid();
        foreach (var (name, text, w) in new[]
                 {
                     ("Time", "时间", 140), ("Mode", "类型", 60), ("Dry", "干跑", 50), ("Emp", "拉取员工", 80),
                     ("Created", "新建", 60), ("Updated", "更新", 60), ("Moved", "移动", 60), ("Disabled", "禁用", 60),
                     ("Fail", "失败", 60), ("Sec", "耗时(秒)", 80),
                 })
        {
            _stGrid.Columns.Add(name, text);
            _stGrid.Columns[name]!.Width = w;
        }

        _statsPage.Controls.Add(_stGrid);
        _statsPage.Controls.Add(_chart);
        _statsPage.Controls.Add(top);

        refresh.Click += (_, _) => LoadStats();
        _stDays.SelectedIndexChanged += (_, _) => LoadStats();
        _stDry.CheckedChanged += (_, _) => LoadStats();
    }

    private void LoadStats()
    {
        var f = Path.Combine(StateDir, "stats.csv");
        _stGrid.Rows.Clear();
        if (!File.Exists(f))
        {
            _chart.SetData(new List<DayStat>());
            return;
        }

        List<string[]> rows;
        string[] header;
        try
        {
            var lines = ReadShared(f).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            header = lines[0].Split(',');
            rows = lines.Skip(1).Select(l => l.Split(',')).Where(p => p.Length >= header.Length - 2).ToList();
        }
        catch (Exception ex)
        {
            ShowError("读取统计文件失败：" + ex.Message);
            return;
        }

        int Col(string n) => Array.IndexOf(header, n);
        string V(string[] r, string n) => Col(n) is var i && i >= 0 && i < r.Length ? r[i] : "";
        int N(string[] r, string n) => int.TryParse(V(r, n), out var x) ? x : 0;

        var parsed = rows
            .Select(r => (Row: r, Time: DateTime.TryParse(V(r, "time"), out var t) ? t : DateTime.MinValue,
                          Dry: string.Equals(V(r, "dryRun"), "True", StringComparison.OrdinalIgnoreCase)))
            .Where(x => x.Time != DateTime.MinValue && (_stDry.Checked || !x.Dry))
            .ToList();

        var days = _stDays.SelectedIndex switch { 0 => 7, 2 => 30, _ => 14 };
        var from = DateTime.Today.AddDays(-days + 1);
        var stats = Enumerable.Range(0, days).Select(i => from.AddDays(i)).Select(day =>
        {
            var g = parsed.Where(x => x.Time.Date == day).ToList();
            return new DayStat(day,
                g.Sum(x => N(x.Row, "usersCreated")),
                g.Sum(x => N(x.Row, "usersMoved")),
                g.Sum(x => N(x.Row, "usersDisabled") + N(x.Row, "leaversReconciled")),
                g.Sum(x => N(x.Row, "failures")));
        }).ToList();
        _chart.SetData(stats);

        foreach (var x in parsed.OrderByDescending(x => x.Time).Take(500))
        {
            var r = x.Row;
            var i = _stGrid.Rows.Add(x.Time.ToString("yyyy-MM-dd HH:mm"),
                string.Equals(V(r, "fullPull"), "True", StringComparison.OrdinalIgnoreCase) ? "全量" : "增量",
                x.Dry ? "是" : "", N(r, "employeesFetched"), N(r, "usersCreated"), N(r, "usersUpdated"),
                N(r, "usersMoved"), N(r, "usersDisabled") + N(r, "leaversReconciled"), N(r, "failures"),
                (N(r, "durationMs") / 1000.0).ToString("0.0", CultureInfo.InvariantCulture));
            if (N(r, "failures") > 0) _stGrid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(200, 30, 30);
        }
    }

    // =====================================================================
    // 计划任务
    // =====================================================================

    private void BuildTasksTab()
    {
        _tasksPage = NewPage("计划任务", isConfig: false);
        var panel = new Panel { Dock = DockStyle.Top, Height = 300, AutoScroll = true };
        var fb = new FormBuilder(panel);
        fb.Section("自动同步的计划任务");
        var exe = ProcessRunner.ExePath;
        fb.Hint("程序位置：" + exe);
        if (exe.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) || exe.Contains(@"\Desktop\", StringComparison.OrdinalIgnoreCase))
            fb.Hint("程序目前放在个人目录 / 桌面下。计划任务会指向这个位置，建议先把 exe 和配置一起移到 C:\\bstoad 再安装任务。", warn: true);
        _tMinute = fb.Number("增量同步", 0, 59, 0, "每小时的第几分钟运行（建议 7，避开整点）");
        _tFullTime = fb.Custom("全量同步",
            new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 100 },
            "每天这个时间运行，含离职对账，建议放在夜间");
        var (btns, res) = fb.Actions("刷新状态", "安装 / 更新任务", "启用", "停用", "立即运行增量", "立即运行全量", "卸载任务");
        _tResult = res;
        _tMinute.Value = 7;
        _tFullTime.Value = DateTime.Today.AddHours(2);

        _tGrid = NewGrid();
        foreach (var (name, text, w) in new[]
                 {
                     ("Name", "任务", 150), ("State", "状态", 80), ("Last", "上次运行", 150),
                     ("Result", "上次结果", 170), ("Next", "下次运行", 150), ("Action", "执行命令", 400),
                 })
        {
            _tGrid.Columns.Add(name, text);
            _tGrid.Columns[name]!.Width = w;
        }

        _tasksPage.Controls.Add(_tGrid);
        _tasksPage.Controls.Add(panel);

        btns[0].Click += (_, _) => RefreshTasks();
        btns[1].Click += (_, _) =>
        {
            if (!Confirm($"将以 SYSTEM 身份注册（或覆盖）两个计划任务：\n\n" +
                         $"BSTOAD-Sync：每小时第 {_tMinute.Value} 分钟，sync --apply\n" +
                         $"BSTOAD-FullSync：每天 {_tFullTime.Value:HH:mm}，sync --apply --full\n\n程序：{exe}\n\n确定吗？"))
                return;
            var minute = (int)_tMinute.Value;
            var at = _tFullTime.Value.TimeOfDay;
            TaskAction(btns[1], () => TaskSchedulerHelper.Install(exe, minute, at), "已安装 / 更新");
        };
        btns[2].Click += (_, _) => TaskAction(btns[2], () => TaskSchedulerHelper.Enable(true), "已启用");
        btns[3].Click += (_, _) =>
        {
            if (Confirm("停用后自动同步将暂停，直到再次启用。确定吗？"))
                TaskAction(btns[3], () => TaskSchedulerHelper.Enable(false), "已停用");
        };
        btns[4].Click += (_, _) => TaskAction(btns[4], () => TaskSchedulerHelper.StartNow(TaskSchedulerHelper.SyncTask), "已触发增量同步，稍后刷新查看结果");
        btns[5].Click += (_, _) =>
        {
            if (Confirm("立即运行全量同步（含离职对账，写入 AD）？"))
                TaskAction(btns[5], () => TaskSchedulerHelper.StartNow(TaskSchedulerHelper.FullTask), "已触发全量同步，稍后刷新查看结果");
        };
        btns[6].Click += (_, _) =>
        {
            if (Confirm("卸载后不再自动同步（程序和配置文件保留）。确定吗？"))
                TaskAction(btns[6], () => TaskSchedulerHelper.Uninstall(), "已卸载");
        };
    }

    private void TaskAction(Button btn, Func<string> action, string okText)
    {
        RunBackgroundThen(btn, () => { action(); return 0; }, _ =>
        {
            FormBuilder.SetResult(_tResult, true, okText);
            RefreshTasks();
        }, _tResult);
    }

    private void RefreshTasks()
    {
        RunBackgroundThen(null, TaskSchedulerHelper.Query, list =>
        {
            _tGrid.Rows.Clear();
            foreach (var t in list)
            {
                var i = _tGrid.Rows.Add(t.Name, t.State, t.LastRun, t.LastResult, t.NextRun, t.Action);
                if (t.State == "未安装" || t.State == "已停用") _tGrid.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(190, 80, 0);
                else if (!string.IsNullOrEmpty(t.LastResult) && !t.LastResult.StartsWith("0") && !t.LastResult.Contains("运行"))
                    _tGrid.Rows[i].Cells["Result"].Style.ForeColor = Color.FromArgb(200, 30, 30);
            }
        }, _tResult);
    }

    // =====================================================================
    // 日志
    // =====================================================================

    private static readonly Regex LogEntryStart = new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

    private void BuildLogsTab()
    {
        _logsPage = NewPage("日志", isConfig: false);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 4) };
        _lFiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        _lLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _lLevel.Items.AddRange(new object[] { "全部", "仅警告和错误", "仅错误" });
        _lLevel.SelectedIndex = 0;
        _lSearch = new TextBox { Width = 200, PlaceholderText = "关键字，如工号或姓名" };
        var refresh = new Button { Text = "刷新", AutoSize = true };
        var open = new Button { Text = "打开日志文件夹", AutoSize = true };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "日期：", AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, _lFiles,
            new Label { Text = "级别：", AutoSize = true, Margin = new Padding(10, 7, 3, 3) }, _lLevel,
            new Label { Text = "查找：", AutoSize = true, Margin = new Padding(10, 7, 3, 3) }, _lSearch, refresh, open,
        });

        _lView = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9f),
            DetectUrls = false,
        };
        _logsPage.Controls.Add(_lView);
        _logsPage.Controls.Add(top);

        refresh.Click += (_, _) => LoadLogFiles();
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDir}\"") { UseShellExecute = true });
        };
        _lFiles.SelectedIndexChanged += (_, _) => ShowLog();
        _lLevel.SelectedIndexChanged += (_, _) => ShowLog();
        _lSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ShowLog();
        };
    }

    private void LoadLogFiles()
    {
        var cur = _lFiles.SelectedItem as string;
        _lFiles.Items.Clear();
        if (Directory.Exists(LogDir))
            foreach (var f in Directory.GetFiles(LogDir, "bstoad-*.log").Select(Path.GetFileName).OrderByDescending(n => n))
                _lFiles.Items.Add(f!);
        if (_lFiles.Items.Count == 0)
        {
            _lView.Text = "暂无日志。";
            return;
        }
        _lFiles.SelectedItem = cur != null && _lFiles.Items.Contains(cur) ? cur : _lFiles.Items[0];
        ShowLog();
    }

    private void ShowLog()
    {
        if (_lFiles.SelectedItem is not string name) return;
        string text;
        try { text = ReadShared(Path.Combine(LogDir, name)); }
        catch (Exception ex)
        {
            _lView.Text = "读取失败：" + ex.Message;
            return;
        }

        // 按条目分组（一条日志可能带多行异常堆栈）
        var entries = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (LogEntryStart.IsMatch(l) && sb.Length > 0)
            {
                entries.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
        }
        if (sb.Length > 0) entries.Add(sb.ToString());

        var level = _lLevel.SelectedIndex;
        var q = _lSearch.Text.Trim();
        var shown = entries
            .Where(e => level == 0 || e.Contains("[ERR]") || e.Contains("[FTL]") || (level == 1 && e.Contains("[WRN]")))
            .Where(e => q.Length == 0 || e.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        const int max = 4000;
        var truncated = shown.Count > max;
        if (truncated) shown = shown.Skip(shown.Count - max).ToList();

        _lView.SuspendLayout();
        _lView.Clear();
        if (truncated) AppendLog($"（只显示最后 {max} 条，共 {entries.Count} 条）", Color.Gray);
        foreach (var e in shown)
            AppendLog(e, e.Contains("[ERR]") || e.Contains("[FTL]") ? Color.FromArgb(200, 30, 30)
                       : e.Contains("[WRN]") ? Color.FromArgb(190, 110, 0) : Color.Black);
        _lView.ResumeLayout();
        _lView.SelectionStart = _lView.TextLength;
        _lView.ScrollToCaret();

        void AppendLog(string s, Color c)
        {
            _lView.SelectionStart = _lView.TextLength;
            _lView.SelectionColor = c;
            _lView.AppendText(s + "\n");
        }
    }

    // =====================================================================
    // 公共
    // =====================================================================

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true,
        RowHeadersVisible = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
    };

    /// <summary>读取可能正被同步程序写入的文件（日志、统计）。</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>后台执行并在 UI 线程处理结果；异常显示在 result 标签或弹窗。</summary>
    private async void RunBackgroundThen<T>(Button? btn, Func<T> work, Action<T> then, Label? result = null)
    {
        if (btn != null) btn.Enabled = false;
        UseWaitCursor = true;
        if (result != null)
        {
            result.ForeColor = FormBuilder.HintColor;
            result.Text = "处理中…";
        }
        try
        {
            var value = await Task.Run(work);
            if (result != null && result.Text == "处理中…") result.Text = "";
            then(value);
        }
        catch (Exception ex)
        {
            if (result != null) FormBuilder.SetResult(result, false, Describe(ex));
            else ShowError(Describe(ex));
        }
        finally
        {
            if (btn != null) btn.Enabled = true;
            UseWaitCursor = false;
        }
    }
}
