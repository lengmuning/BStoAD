using Bstoad.Core.Options;
using Bstoad.Worker.Events;

namespace Bstoad.Worker.Gui;

internal sealed partial class MainForm
{
    // ---- 事件订阅 ----
    private CheckBox _evEnabled = null!, _evHourly = null!;
    private TextBox _evUrl = null!, _evToken = null!;
    private NumericUpDown _evPoll = null!, _evSettle = null!, _evAlertAfter = null!;

    // =====================================================================
    // 事件订阅
    // =====================================================================

    private void BuildEventTab()
    {
        var f = new FormBuilder(NewPage("事件订阅", isConfig: true));
        f.Section("实时同步");
        _evEnabled = f.Check("启用事件订阅：北森有变动时自动同步到 AD（约 2 分钟内生效）");
        f.Hint("关闭时与 v1.1 完全一样，只按计划任务定时同步。开启后每日全量同步仍然保留，用于兜底和离职对账。");
        _evUrl = f.Text("事件服务地址", 360, "如 http://事件服务器:8601");
        f.Hint("北森事件接收端的内网拉取接口（8601 端口），不是填在北森后台的那个公网回调地址。更换地址后，监听从新地址的当前最新事件开始。");
        _evToken = SecretBox(f, "访问令牌", "EventFeed", "Token");
        f.Hint("在事件服务器上执行 sudo grep BEISEN_FEED_TOKEN /etc/beisen-webhook.env 获取。令牌视同密码，保存时加密。");

        var (btns, res) = f.Actions("测试连接");
        btns[0].Click += (_, _) =>
        {
            if (!CollectAll()) return;
            var url = _doc!.GetString("", "EventFeed", "Url");
            var token = _doc.GetString("", "EventFeed", "Token");
            RunBackground(btns[0], res, () => TestEventFeed(url, token));
        };

        f.Section("高级");
        _evPoll = f.Number("轮询间隔", 2, 600, 0, "秒");
        _evSettle = f.Number("合并等待", 30, 3600, 0, "秒");
        f.Hint("收到事件后等这么久没有新事件再同步，把 HR 的一组连续操作合并成一次。需大于 75 秒（北森数据写入后要等一会儿才查得到）。");
        _evAlertAfter = f.Number("连不上事件服务", 1, 1440, 0, "分钟后告警（走「告警」页的渠道）");

        f.Section("定时同步");
        _evHourly = f.Check("保留每小时增量同步（计划任务 BSTOAD-Sync）");
        f.Hint("事件订阅稳定前建议保留。取消勾选并保存后，每小时增量任务被暂停（不删除，重新勾选即恢复），" +
               "北森变更只靠事件订阅同步，每日全量同步照常运行。");
        f.Hint("暂停前请确认北森已推送你关心的全部事件类型（入职、调动、离职、组织变更等）——没有推送的类型要等每日全量才会同步。" +
               "观察方法：每日全量同步若仍有新建 / 移动 / 禁用，就是事件没有覆盖到的变更（可在「告警」页开启每日汇总）。", warn: true);

        f.Section("让它运行起来");
        f.Hint("保存配置后，到「计划任务」页点「安装 / 更新任务」：会注册开机自启的 BSTOAD-Events 任务并立即启动。" +
               "之后修改本页配置，保存时会提示重启事件监听。");
    }

    private static (bool Ok, string Message) TestEventFeed(string url, string protectedToken)
    {
        string token;
        try { token = ConfigProtector.Unprotect(protectedToken); }
        catch (Exception ex) { return (false, ex.Message); }

        using var client = new EventFeedClient(url, token, TimeSpan.FromSeconds(10));
        var st = client.GetStatusAsync().GetAwaiter().GetResult();
        var latest = st.LatestReceivedAtMs is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
            : "无";
        var msg = $"连接成功：服务端保留 {st.Count} 条事件（{st.RetentionDays} 天），最新序号 {st.MaxSeq}，最近一条 {latest}";

        var cursor = EventCursorStore.Load();
        if (cursor != null && string.Equals(EventFeedClient.NormalizeUrl(cursor.Url), EventFeedClient.NormalizeUrl(url), StringComparison.OrdinalIgnoreCase))
        {
            var behind = Math.Max(0, st.MaxSeq - cursor.Seq);
            msg += behind == 0 ? "；本机已全部处理" : $"；本机处理到序号 {cursor.Seq}，还有 {behind} 条待处理";
        }
        else
        {
            msg += "；本机尚未开始监听这个地址";
        }
        return (true, msg);
    }

    private void LoadEvents()
    {
        var d = _doc!;
        var def = new EventFeedOptions();
        _evEnabled.Checked = d.GetBool(false, "EventFeed", "Enabled");
        _evUrl.Text = d.GetString("", "EventFeed", "Url");
        _evPoll.Value = Math.Clamp(d.GetInt(def.PollIntervalSeconds, "EventFeed", "PollIntervalSeconds"), 2, 600);
        _evSettle.Value = Math.Clamp(d.GetInt(def.SettleSeconds, "EventFeed", "SettleSeconds"), 30, 3600);
        _evAlertAfter.Value = Math.Clamp(d.GetInt(def.AlertAfterMinutes, "EventFeed", "AlertAfterMinutes"), 1, 1440);
        _evHourly.Checked = d.GetBool(new SyncOptions().HourlyIncrementalEnabled, "Sync", "HourlyIncrementalEnabled");
    }

    private void CollectEvents()
    {
        var d = _doc!;
        d.SetBool(_evEnabled.Checked, "EventFeed", "Enabled");
        d.SetString(EventFeedClient.NormalizeUrl(_evUrl.Text), "EventFeed", "Url");
        d.SetInt((int)_evPoll.Value, "EventFeed", "PollIntervalSeconds");
        d.SetInt((int)_evSettle.Value, "EventFeed", "SettleSeconds");
        d.SetInt((int)_evAlertAfter.Value, "EventFeed", "AlertAfterMinutes");
        d.SetBool(_evHourly.Checked, "Sync", "HourlyIncrementalEnabled");
        if (d.Get("EventFeed", "Token") == null) d.SetString("", "EventFeed", "Token");
    }

    private void ValidateEvents(List<string> p)
    {
        if (!_evHourly.Checked && !_evEnabled.Checked)
            p.Add("每小时增量已暂停，且未启用事件订阅：北森的变更要等每日全量同步才会写入 AD");
        if (!_evEnabled.Checked) return;
        if (!Uri.TryCreate(_evUrl.Text.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            p.Add("已启用事件订阅，但事件服务地址不是有效的 http(s) 地址");
        if (!_doc!.IsSecretSet("EventFeed", "Token"))
            p.Add("已启用事件订阅，但访问令牌未填写");
        if (_evSettle.Value < 75)
            p.Add("合并等待小于 75 秒：北森数据可能还查不到，程序会自动按 75 秒执行");
    }

    /// <summary>
    /// 保存配置后应用到计划任务：按「保留每小时增量同步」暂停 / 恢复 BSTOAD-Sync；
    /// 事件监听正在运行时提示重启，让新配置生效。
    /// </summary>
    private void ApplyTaskSettingsAfterSave()
    {
        var hourly = _evHourly.Checked;
        RunBackgroundThen(null, () => (Hourly: TaskSchedulerHelper.SetHourly(hourly), Running: TaskSchedulerHelper.IsEventsRunning()), r =>
        {
            if (r.Hourly == "CHANGED")
                ShowInfo(hourly
                    ? "已恢复每小时增量同步（BSTOAD-Sync 已启用）。"
                    : "已暂停每小时增量同步（BSTOAD-Sync 已停用，未删除）。北森变更现在只靠事件订阅 + 每日全量同步。");
            if (r.Running && Confirm("事件监听正在运行，新配置需要重启它才能生效。现在重启？"))
                RunBackgroundThen(null, TaskSchedulerHelper.RestartEvents, _ => ShowInfo("事件监听已重启。"));
        });
    }
}
