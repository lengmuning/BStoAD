using Bstoad.ActiveDirectory;
using Bstoad.Beisen;
using Bstoad.Core.Options;
using Bstoad.Worker.Alerts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bstoad.Worker.Gui;

internal sealed partial class MainForm
{
    // 北森人员状态 / 雇佣类型枚举（官方「CoreHr_人员状态」可用选项）
    private static readonly int[] EmpStatusValues = { 1, 2, 3, 4, 5, 6, 8, 12 };
    private static readonly int[] EmpTypeValues = { 0, 2 };

    // ---- 北森 ----
    private TextBox _bAppKey = null!, _bAppSecret = null!, _bBaseUrl = null!, _bFullStart = null!, _bTitleFields = null!, _bEmpTypeOther = null!;
    private List<CheckBox> _bEmpStatus = null!, _bEmpType = null!;
    private CheckBox _bWithDisabled = null!, _bLogRaw = null!;

    // ---- AD ----
    private TextBox _aHost = null!, _aAccount = null!, _aPassword = null!, _aBaseOu = null!, _aLeaverOu = null!,
        _aUpn = null!, _aInitPwd = null!, _aNeverTouch = null!;
    private NumericUpDown _aPort = null!;
    private CheckBox _aSsl = null!, _aNeverExpire = null!, _aForceChange = null!;

    // ---- 同步规则 ----
    private TextBox _sRootOrg = null!, _sLeaverOrg = null!;
    private NumericUpDown _sBackfill = null!, _sMaxLeavers = null!, _sMaxRatio = null!, _sMinPull = null!;
    private CheckBox _rTransferOut = null!, _rRetired = null!, _rPendingHire = null!, _rDisableOnMoveFail = null!, _rDecodeHtml = null!;

    // ---- 告警 ----
    private CheckBox _alEnabled = null!, _alSummary = null!, _alDry = null!;
    private ComboBox _alLevel = null!;
    private NumericUpDown _alSuppress = null!;
    private TextBox _alTitle = null!;
    private CheckBox _wcOn = null!, _dtOn = null!, _fsOn = null!;
    private TextBox _wcHook = null!, _dtHook = null!, _dtSecret = null!, _fsHook = null!, _fsSecret = null!;

    private static readonly string[] LevelValues = { "Critical", "Warning", "Info" };

    // =====================================================================
    // 北森
    // =====================================================================

    private void BuildBeisenTab()
    {
        var f = new FormBuilder(NewPage("北森", isConfig: true));
        f.Section("连接器凭据");
        _bAppKey = f.Text("AppKey");
        _bAppSecret = SecretBox(f, "AppSecret", "Beisen", "AppSecret");
        f.Hint("在北森开放平台「我的连接器」中获取。密钥保存时自动加密，只有本机能解密。");

        f.Section("拉取哪些员工");
        _bEmpStatus = f.CheckRow("人员状态", "1 待入职", "2 试用", "3 正式", "4 调出", "5 待调入", "6 退休", "8 离职", "12 非正式");
        f.Hint("全部不勾 = 不传此参数，由北森决定默认范围。建议全部勾选：调出、退休等是否禁用账号由「同步规则」页决定，数据要先拉回来才能处理。");
        _bEmpType = f.CheckRow("雇佣类型", "0 内部员工", "2 实习生");
        _bEmpTypeOther = f.Text("其他雇佣类型编号", 200, "如 1,3");
        f.Hint("北森默认只返回内部员工。实习生、劳务等需要在这里勾选或填写编号才会被同步。");
        _bWithDisabled = f.Check("包含已离职的任职记录（离职归档需要，建议勾选）");

        f.Section("高级");
        _bBaseUrl = f.Text("接口地址");
        _bFullStart = f.Text("全量起始日期", 160, "yyyy-MM-dd");
        _bTitleFields = f.Text("职位字段候选");
        f.Hint("北森数据中表示「职位」的字段名，多个用逗号分隔，按顺序取第一个有值的。AD 里职务为空时可调整。");
        _bLogRaw = f.Check("在日志中输出北森原始数据样本");
        f.Hint("样本含身份证号、住址等个人信息。只在排查字段问题时临时开启，查完立即关闭并清理日志。", warn: true);

        var (btns, res) = f.Actions("测试北森连接");
        btns[0].Click += (_, _) =>
        {
            if (!CollectAll()) return;
            var doc = _doc!;
            RunBackground(btns[0], res, () =>
            {
                using var sp = GuiServices.Build(doc);
                sp.GetRequiredService<IBeisenTokenProvider>().GetAccessTokenAsync().GetAwaiter().GetResult();
                var orgs = sp.GetRequiredService<IBeisenOrgClient>().GetAllOrgsAsync().GetAwaiter().GetResult();
                return (true, $"连接成功：已取得访问令牌，读取到 {orgs.Count} 个组织");
            });
        };
    }

    private void LoadBeisen()
    {
        var d = _doc!;
        _bAppKey.Text = NotPlaceholder(d.GetString("", "Beisen", "AppKey"));

        var statuses = ParseIntList(d.GetString("", "Beisen", "EmployeeExtraParams", "empStatus"));
        for (var i = 0; i < EmpStatusValues.Length; i++) _bEmpStatus[i].Checked = statuses.Contains(EmpStatusValues[i]);
        var types = ParseIntList(d.GetString("", "Beisen", "EmployeeExtraParams", "employType"));
        for (var i = 0; i < EmpTypeValues.Length; i++) _bEmpType[i].Checked = types.Contains(EmpTypeValues[i]);
        _bEmpTypeOther.Text = string.Join(",", types.Where(t => !EmpTypeValues.Contains(t)));
        _bWithDisabled.Checked = d.GetBool(false, "Beisen", "EmployeeExtraParams", "withDisabled");

        var def = new BeisenOptions();
        _bBaseUrl.Text = d.GetString(def.BaseUrl, "Beisen", "BaseUrl");
        _bFullStart.Text = d.GetString(def.FullPullStartDate, "Beisen", "FullPullStartDate");
        var tf = d.GetStringArray("Beisen", "TitleFields");
        _bTitleFields.Text = string.Join(",", tf.Length > 0 ? tf : def.TitleFields);
        _bLogRaw.Checked = d.GetBool(false, "Beisen", "LogRawSamples");
    }

    private void CollectBeisen()
    {
        var d = _doc!;
        d.SetString(_bAppKey.Text.Trim(), "Beisen", "AppKey");

        var statuses = EmpStatusValues.Where((_, i) => _bEmpStatus[i].Checked).ToList();
        SetListParam(statuses, "empStatus");
        var types = EmpTypeValues.Where((_, i) => _bEmpType[i].Checked).ToList();
        types.AddRange(ParseIntList(_bEmpTypeOther.Text).Where(t => !types.Contains(t)));
        SetListParam(types, "employType");
        // 额外参数按 JSON 字面量字符串保存（与手工配置格式一致）
        d.SetString(_bWithDisabled.Checked ? "true" : "false", "Beisen", "EmployeeExtraParams", "withDisabled");

        d.SetString(_bBaseUrl.Text.Trim(), "Beisen", "BaseUrl");
        d.SetString(_bFullStart.Text.Trim(), "Beisen", "FullPullStartDate");
        d.SetStringArray(SplitList(_bTitleFields.Text), "Beisen", "TitleFields");
        d.SetBool(_bLogRaw.Checked, "Beisen", "LogRawSamples");

        void SetListParam(List<int> values, string key)
        {
            if (values.Count == 0) d.Remove("Beisen", "EmployeeExtraParams", key);
            else d.SetString("[" + string.Join(",", values.OrderBy(v => v)) + "]", "Beisen", "EmployeeExtraParams", key);
        }
    }

    // =====================================================================
    // AD
    // =====================================================================

    private void BuildAdTab()
    {
        var f = new FormBuilder(NewPage("AD 域控", isConfig: true));
        f.Section("域控连接");
        _aHost = f.Text("域控地址", 300);
        _aPort = f.Number("端口", 1, 65535);
        _aSsl = f.Check("使用 LDAPS（636 端口）；389 端口时程序会自动启用签名+加密");
        _aAccount = f.Text("服务账号", 300, @"域名\用户名，如 CORP\bstoad-svc");
        f.Hint("这里写一个反斜杠即可（不用像手改 JSON 那样写两个）。");
        _aPassword = SecretBox(f, "服务账号密码", "ActiveDirectory", "ServicePassword");

        f.Section("OU 位置");
        (_aBaseOu, var b1) = f.TextWithButton("同步根 OU", "浏览…", 520);
        f.Hint("北森的部门树会建在这个 OU 下面。");
        (_aLeaverOu, var b2) = f.TextWithButton("离职归档 OU", "浏览…", 520);
        f.Hint("离职员工的账号会移到这里并禁用。建议放在同步根 OU 之外，名称不要与北森部门重名。");
        _aUpn = f.Text("UPN 后缀", 220, "如 corp.com");
        b1.Click += (_, _) => BrowseOu(_aBaseOu, "选择同步根 OU");
        b2.Click += (_, _) => BrowseOu(_aLeaverOu, "选择离职归档 OU");

        f.Section("新建账号");
        _aInitPwd = f.Text("初始密码", 220);
        f.Hint("需满足域密码策略，否则新建账号会失败（错误 0x52d）。", warn: true);
        _aNeverExpire = f.Check("密码永不过期");
        _aForceChange = f.Check("首次登录必须修改密码");

        f.Section("禁区（永不触碰）");
        _aNeverTouch = f.Multiline("禁区容器", 100, 520);
        f.Hint("每行一个 DN。位于这些容器下的账号，同步永不接管、修改、移动或禁用。特权账号（adminCount=1）无论在哪都自动受保护。");

        var (btns, res) = f.Actions("测试 AD 连接");
        btns[0].Click += (_, _) =>
        {
            if (!CollectAll()) return;
            var doc = _doc!;
            RunBackground(btns[0], res, () =>
            {
                using var sp = GuiServices.Build(doc);
                var factory = sp.GetRequiredService<ILdapConnectionFactory>();
                factory.TestBind();
                var o = sp.GetRequiredService<IOptions<ActiveDirectoryOptions>>().Value;
                var baseOk = AdBrowser.Exists(factory, o.BaseUserOu);
                var leaverOk = AdBrowser.Exists(factory, o.LeaverOu);
                var msg = "绑定成功；同步根 OU " + (baseOk ? "存在" : "不存在") + "；离职归档 OU " + (leaverOk ? "存在" : "不存在（首次归档时会自动创建）");
                return (baseOk, msg);
            });
        };
    }

    private async void BrowseOu(TextBox target, string title)
    {
        if (!CollectAll()) return;
        var sp = GuiServices.Build(_doc!);
        try
        {
            UseWaitCursor = true;
            var factory = sp.GetRequiredService<ILdapConnectionFactory>();
            var root = await Task.Run(() => AdBrowser.GetDefaultNamingContext(factory));
            UseWaitCursor = false;
            using var dlg = new OuPickerDialog(factory, root, title);
            if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedDn))
                target.Text = dlg.SelectedDn;
        }
        catch (Exception ex)
        {
            ShowError("无法浏览 AD（请先确认域控地址和服务账号正确）：\n" + Describe(ex));
        }
        finally
        {
            UseWaitCursor = false;
            sp.Dispose();
        }
    }

    private void LoadAd()
    {
        var d = _doc!;
        var def = new ActiveDirectoryOptions();
        _aHost.Text = d.GetString("", "ActiveDirectory", "LdapHost");
        _aPort.Value = Math.Clamp(d.GetInt(389, "ActiveDirectory", "Port"), 1, 65535);
        _aSsl.Checked = d.GetBool(false, "ActiveDirectory", "UseSsl");
        _aAccount.Text = NotPlaceholder(d.GetString("", "ActiveDirectory", "ServiceAccount"));
        _aBaseOu.Text = d.GetString("", "ActiveDirectory", "BaseUserOu");
        _aLeaverOu.Text = d.GetString("", "ActiveDirectory", "LeaverOu");
        _aUpn.Text = d.GetString("", "ActiveDirectory", "UpnSuffix");
        _aInitPwd.Text = d.GetString(def.DefaultInitialPassword, "ActiveDirectory", "DefaultInitialPassword");
        _aNeverExpire.Checked = d.GetBool(def.PasswordNeverExpires, "ActiveDirectory", "PasswordNeverExpires");
        _aForceChange.Checked = d.GetBool(def.ForceChangePasswordAtNextLogon, "ActiveDirectory", "ForceChangePasswordAtNextLogon");
        _aNeverTouch.Lines = d.GetStringArray("ActiveDirectory", "NeverTouchOuDns");
    }

    private void CollectAd()
    {
        var d = _doc!;
        d.SetString(_aHost.Text.Trim(), "ActiveDirectory", "LdapHost");
        d.SetInt((int)_aPort.Value, "ActiveDirectory", "Port");
        d.SetBool(_aSsl.Checked, "ActiveDirectory", "UseSsl");
        d.SetString(_aAccount.Text.Trim(), "ActiveDirectory", "ServiceAccount");
        d.SetString(_aBaseOu.Text.Trim(), "ActiveDirectory", "BaseUserOu");
        d.SetString(_aLeaverOu.Text.Trim(), "ActiveDirectory", "LeaverOu");
        d.SetString(_aUpn.Text.Trim(), "ActiveDirectory", "UpnSuffix");
        d.SetString(_aInitPwd.Text, "ActiveDirectory", "DefaultInitialPassword");
        d.SetBool(_aNeverExpire.Checked, "ActiveDirectory", "PasswordNeverExpires");
        d.SetBool(_aForceChange.Checked, "ActiveDirectory", "ForceChangePasswordAtNextLogon");
        d.SetStringArray(_aNeverTouch.Lines.Select(l => l.Trim()).Where(l => l.Length > 0), "ActiveDirectory", "NeverTouchOuDns");
    }

    // =====================================================================
    // 同步规则
    // =====================================================================

    private void BuildSyncTab()
    {
        var f = new FormBuilder(NewPage("同步规则", isConfig: true));
        f.Section("组织范围");
        (_sRootOrg, var b1) = f.TextWithButton("同步根组织 OrgId", "从北森选择…", 200);
        f.Hint("只同步该组织下面的部门，它的直接下级挂在同步根 OU 下。留空 = 同步整棵组织树。");
        (_sLeaverOrg, var b2) = f.TextWithButton("离职人员部门 OrgId", "从北森选择…", 200);
        f.Hint("员工被挂到这个部门即视为离职（移到离职归档 OU 并禁用）。留空 = 只按人员状态判断。");
        b1.Click += (_, _) => PickOrg(_sRootOrg, "选择同步根组织");
        b2.Click += (_, _) => PickOrg(_sLeaverOrg, "选择离职人员部门");

        f.Section("增量同步");
        _sBackfill = f.Number("增量回看天数", 1, 90, 0, "天");
        f.Hint("每小时任务拉取最近 N 天内有变动的员工。窗口大一些能扛住服务器停机，默认 7。");

        f.Section("安全阀");
        _sMaxLeavers = f.Number("单轮最多禁用", 0, 100000, 0, "个");
        _sMaxRatio = f.Number("或纳管总数的", 0, 100, 1, "%");
        f.Hint("每日全量对账时，若一轮拟禁用的账号超过两者中较大的值，整轮中止并告警，防止北森数据异常导致大批误禁用。");
        _sMinPull = f.Number("拉取量不低于上轮的", 0, 100, 0, "%");
        f.Hint("全量拉取的员工数骤降到这个比例以下，视为北森数据或权限异常，本轮不做离职对账。");

        f.Section("业务规则（默认与 v1.0 行为一致）");
        _rTransferOut = f.Check("「调出」（状态 4）按离职处理，禁用账号");
        f.Hint("集团内跨公司调动、调动后继续使用同一个 AD 账号的，建议取消勾选。");
        _rRetired = f.Check("「退休」（状态 6）按离职处理，禁用账号");
        _rPendingHire = f.Check("「待入职」（状态 1）提前建号");
        f.Hint("勾选：入职前账号已就绪。取消：等北森状态变为试用或正式后再建号。");
        _rDisableOnMoveFail = f.Check("离职账号移到离职 OU 失败时，仍在原位置禁用（推荐勾选）");
        _rDecodeHtml = f.Check("把名称中的 &amp; 等符号还原为 &");
        f.Hint("开启后，名称带 &amp; 的部门 OU 会被改名一次（同一个 OU 改名，里面的账号不受影响）。", warn: true);
    }

    private async void PickOrg(TextBox target, string title)
    {
        if (!CollectAll()) return;
        var doc = _doc!;
        try
        {
            UseWaitCursor = true;
            var orgs = await Task.Run(() =>
            {
                using var sp = GuiServices.Build(doc);
                return sp.GetRequiredService<IBeisenOrgClient>().GetAllOrgsAsync().GetAwaiter().GetResult();
            });
            UseWaitCursor = false;
            using var dlg = new OrgPickerDialog(orgs, title, target.Text.Trim());
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Selected != null)
                target.Text = dlg.Selected.OrgId;
        }
        catch (Exception ex)
        {
            ShowError("读取北森组织失败（请先确认北森凭据正确）：\n" + Describe(ex));
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void LoadSync()
    {
        var d = _doc!;
        var def = new SyncOptions();
        _sRootOrg.Text = d.GetString("", "Sync", "RootOrgId");
        _sLeaverOrg.Text = d.GetString("", "Sync", "LeaverOrgId");
        _sBackfill.Value = Math.Clamp(d.GetInt(def.InitialBackfillDays, "Sync", "InitialBackfillDays"), 1, 90);
        _sMaxLeavers.Value = Math.Clamp(d.GetInt(def.MaxLeaversPerRun, "Sync", "MaxLeaversPerRun"), 0, 100000);
        _sMaxRatio.Value = (decimal)Math.Clamp(d.GetDouble(def.MaxLeaverRatio, "Sync", "MaxLeaverRatio") * 100, 0, 100);
        _sMinPull.Value = (decimal)Math.Clamp(d.GetDouble(def.MinPullRatioVsLastRun, "Sync", "MinPullRatioVsLastRun") * 100, 0, 100);
        _rTransferOut.Checked = d.GetBool(def.TreatTransferOutAsLeaver, "Sync", "TreatTransferOutAsLeaver");
        _rRetired.Checked = d.GetBool(def.TreatRetiredAsLeaver, "Sync", "TreatRetiredAsLeaver");
        _rPendingHire.Checked = d.GetBool(def.CreateAccountForPendingHire, "Sync", "CreateAccountForPendingHire");
        _rDisableOnMoveFail.Checked = d.GetBool(def.DisableLeaverEvenIfMoveFails, "Sync", "DisableLeaverEvenIfMoveFails");
        _rDecodeHtml.Checked = d.GetBool(def.DecodeHtmlEntitiesInNames, "Sync", "DecodeHtmlEntitiesInNames");
    }

    private void CollectSync()
    {
        var d = _doc!;
        d.SetString(_sRootOrg.Text.Trim(), "Sync", "RootOrgId");
        d.SetString(_sLeaverOrg.Text.Trim(), "Sync", "LeaverOrgId");
        d.SetInt((int)_sBackfill.Value, "Sync", "InitialBackfillDays");
        d.SetInt((int)_sMaxLeavers.Value, "Sync", "MaxLeaversPerRun");
        d.SetDouble((double)_sMaxRatio.Value / 100, "Sync", "MaxLeaverRatio");
        d.SetDouble((double)_sMinPull.Value / 100, "Sync", "MinPullRatioVsLastRun");
        d.SetBool(_rTransferOut.Checked, "Sync", "TreatTransferOutAsLeaver");
        d.SetBool(_rRetired.Checked, "Sync", "TreatRetiredAsLeaver");
        d.SetBool(_rPendingHire.Checked, "Sync", "CreateAccountForPendingHire");
        d.SetBool(_rDisableOnMoveFail.Checked, "Sync", "DisableLeaverEvenIfMoveFails");
        d.SetBool(_rDecodeHtml.Checked, "Sync", "DecodeHtmlEntitiesInNames");
    }

    // =====================================================================
    // 告警
    // =====================================================================

    private void BuildAlertTab()
    {
        var f = new FormBuilder(NewPage("告警", isConfig: true));
        f.Section("总开关");
        _alEnabled = f.Check("启用告警推送");
        f.Hint("告警只在正式同步（写入 AD）后发送；任何渠道发送失败都不会影响同步本身。");
        _alLevel = f.Combo("推送级别", 360, "仅严重（熔断、绑定失败、程序异常）", "需要关注及以上（推荐）", "全部（含工号为空等提示）");
        _alSuppress = f.Number("同一事项不重复提醒", 0, 720, 0, "小时内");
        _alSummary = f.Check("每天全量同步后推送一份汇总（即使没有新问题）");
        _alDry = f.Check("干跑时也推送（一般不需要）");
        _alTitle = f.Text("消息标题", 300);
        f.Hint("钉钉机器人若设置了「自定义关键词」，标题里要包含该关键词。");

        f.Section("企业微信");
        _wcOn = f.Check("启用");
        _wcHook = SecretBox(f, "Webhook 地址", "Alerts", "WeCom", "Webhook");
        f.Hint("群聊 → 添加群机器人 → 复制 Webhook 地址。地址视同密码，保存时加密。");
        AddTestButton(f, "wecom");

        f.Section("钉钉");
        _dtOn = f.Check("启用");
        _dtHook = SecretBox(f, "Webhook 地址", "Alerts", "DingTalk", "Webhook");
        _dtSecret = SecretBox(f, "加签密钥", "Alerts", "DingTalk", "Secret");
        f.Hint("安全设置选「加签」时填写 SEC 开头的密钥；选「自定义关键词」则留空。");
        AddTestButton(f, "dingtalk");

        f.Section("飞书");
        _fsOn = f.Check("启用");
        _fsHook = SecretBox(f, "Webhook 地址", "Alerts", "Feishu", "Webhook");
        _fsSecret = SecretBox(f, "签名密钥", "Alerts", "Feishu", "Secret");
        f.Hint("自定义机器人开启「签名校验」时填写，否则留空。");
        AddTestButton(f, "feishu");

    }

    private void AddTestButton(FormBuilder f, string channel)
    {
        var (btns, res) = f.Actions("发送测试消息");
        btns[0].Click += (_, _) =>
        {
            if (!CollectAll()) return;
            var doc = _doc!;
            RunBackground(btns[0], res, () =>
            {
                using var sp = GuiServices.Build(doc);
                var o = sp.GetRequiredService<IOptions<AlertOptions>>().Value;
                var only = new AlertOptions { Title = o.Title };
                switch (channel)
                {
                    case "wecom": only.WeCom = o.WeCom; only.WeCom.Enabled = true; break;
                    case "dingtalk": only.DingTalk = o.DingTalk; only.DingTalk.Enabled = true; break;
                    default: only.Feishu = o.Feishu; only.Feishu.Enabled = true; break;
                }
                var results = new AlertSender().SendAsync(only, only.Title + " · 测试消息",
                    $"这是一条测试消息，说明告警渠道配置正确。\n服务器：{Environment.MachineName}\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                    .GetAwaiter().GetResult();
                if (results.Count == 0) return (false, "请先填写 Webhook 地址");
                return (results[0].Ok, results[0].Ok ? "已发送，请到群里查看" : results[0].Message);
            });
        };
    }

    private void LoadAlerts()
    {
        var d = _doc!;
        var def = new AlertOptions();
        _alEnabled.Checked = d.GetBool(false, "Alerts", "Enabled");
        _alLevel.SelectedIndex = Math.Max(0, Array.IndexOf(LevelValues,
            AlertService.ParseLevel(d.GetString(def.MinLevel, "Alerts", "MinLevel")) switch
            {
                Core.Models.SyncEventLevel.Critical => "Critical",
                Core.Models.SyncEventLevel.Info => "Info",
                _ => "Warning",
            }));
        _alSuppress.Value = Math.Clamp(d.GetInt(def.RepeatSuppressHours, "Alerts", "RepeatSuppressHours"), 0, 720);
        _alSummary.Checked = d.GetBool(false, "Alerts", "SendFullSyncSummary");
        _alDry.Checked = d.GetBool(false, "Alerts", "AlertOnDryRun");
        _alTitle.Text = d.GetString(def.Title, "Alerts", "Title");

        _wcOn.Checked = d.GetBool(false, "Alerts", "WeCom", "Enabled");
        _dtOn.Checked = d.GetBool(false, "Alerts", "DingTalk", "Enabled");
        _fsOn.Checked = d.GetBool(false, "Alerts", "Feishu", "Enabled");
    }

    private void CollectAlerts()
    {
        var d = _doc!;
        d.SetBool(_alEnabled.Checked, "Alerts", "Enabled");
        d.SetString(LevelValues[Math.Max(0, _alLevel.SelectedIndex)], "Alerts", "MinLevel");
        d.SetInt((int)_alSuppress.Value, "Alerts", "RepeatSuppressHours");
        d.SetBool(_alSummary.Checked, "Alerts", "SendFullSyncSummary");
        d.SetBool(_alDry.Checked, "Alerts", "AlertOnDryRun");
        d.SetString(_alTitle.Text.Trim(), "Alerts", "Title");

        d.SetBool(_wcOn.Checked, "Alerts", "WeCom", "Enabled");
        d.SetBool(_dtOn.Checked, "Alerts", "DingTalk", "Enabled");
        d.SetBool(_fsOn.Checked, "Alerts", "Feishu", "Enabled");

        // 确保渠道段存在（即使从未填写），便于手工查看配置
        foreach (var ch in new[] { "WeCom", "DingTalk", "Feishu" })
            if (d.Get("Alerts", ch, "Webhook") == null) d.SetString("", "Alerts", ch, "Webhook");
    }

    // =====================================================================
    // 工具
    // =====================================================================

    private static string NotPlaceholder(string s) => s.StartsWith("<PLACEHOLDER", StringComparison.Ordinal) ? "" : s;

    private static List<int> ParseIntList(string s) =>
        SplitList(s.Replace("[", "").Replace("]", ""))
            .Select(x => int.TryParse(x, out var n) ? (int?)n : null)
            .Where(n => n.HasValue).Select(n => n!.Value).Distinct().ToList();

    private static List<string> SplitList(string s) =>
        s.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
