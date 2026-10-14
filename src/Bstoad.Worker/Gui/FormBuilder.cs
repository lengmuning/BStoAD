namespace Bstoad.Worker.Gui;

/// <summary>纵向「标签 + 输入框 + 说明」表单的构建工具。</summary>
internal sealed class FormBuilder
{
    public static readonly Color HintColor = Color.FromArgb(110, 110, 110);
    public static readonly Color WarnColor = Color.FromArgb(190, 80, 0);

    private readonly TableLayoutPanel _t;

    public FormBuilder(Control host)
    {
        if (host is ScrollableControl sc) sc.AutoScroll = true;
        _t = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(0, 0),
            Padding = new Padding(16, 12, 16, 24),
        };
        _t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        host.Controls.Add(_t);
    }

    private int NewRow()
    {
        _t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return _t.RowCount++;
    }

    public void Section(string title)
    {
        var row = NewRow();
        var l = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(_t.Font.FontFamily, 10.5f, FontStyle.Bold),
            Margin = new Padding(0, row == 0 ? 4 : 18, 0, 6),
        };
        _t.Controls.Add(l, 0, row);
        _t.SetColumnSpan(l, 3);
    }

    public Label Hint(string text, bool warn = false)
    {
        var row = NewRow();
        var l = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            ForeColor = warn ? WarnColor : HintColor,
            Margin = new Padding(3, 0, 3, 6),
        };
        _t.Controls.Add(l, 1, row);
        _t.SetColumnSpan(l, 2);
        return l;
    }

    private int LabelRow(string label)
    {
        var row = NewRow();
        _t.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 7, 12, 3),
        }, 0, row);
        return row;
    }

    public TextBox Text(string label, int width = 440, string? placeholder = null)
    {
        var row = LabelRow(label);
        var tb = new TextBox { Width = width, PlaceholderText = placeholder ?? "" };
        _t.Controls.Add(tb, 1, row);
        return tb;
    }

    public TextBox Secret(string label, int width = 440)
    {
        var tb = Text(label, width);
        tb.UseSystemPasswordChar = true;
        return tb;
    }

    public TextBox Multiline(string label, int height = 90, int width = 440)
    {
        var row = LabelRow(label);
        var tb = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = width,
            Height = height,
            WordWrap = false,
        };
        _t.Controls.Add(tb, 1, row);
        return tb;
    }

    public (TextBox Box, Button Button) TextWithButton(string label, string buttonText, int width = 440)
    {
        var tb = Text(label, width);
        var row = _t.GetRow(tb);
        var b = new Button { Text = buttonText, AutoSize = true, Margin = new Padding(6, 2, 3, 2) };
        _t.Controls.Add(b, 2, row);
        return (tb, b);
    }

    /// <summary>标签 + 任意控件。</summary>
    public T Custom<T>(string label, T control, string? suffix = null) where T : Control
    {
        var row = LabelRow(label);
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        flow.Controls.Add(control);
        if (suffix != null)
            flow.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(6, 6, 3, 3) });
        _t.Controls.Add(flow, 1, row);
        return control;
    }

    public CheckBox Check(string text)
    {
        var row = NewRow();
        var cb = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 2) };
        _t.Controls.Add(cb, 1, row);
        _t.SetColumnSpan(cb, 2);
        return cb;
    }

    public NumericUpDown Number(string label, decimal min, decimal max, int decimals = 0, string? suffix = null)
    {
        var row = LabelRow(label);
        var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var n = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 100 };
        flow.Controls.Add(n);
        if (suffix != null)
            flow.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(6, 6, 3, 3) });
        _t.Controls.Add(flow, 1, row);
        return n;
    }

    public ComboBox Combo(string label, int width, params string[] items)
    {
        var row = LabelRow(label);
        var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        cb.Items.AddRange(items);
        _t.Controls.Add(cb, 1, row);
        return cb;
    }

    /// <summary>一行复选框（如人员状态多选）。</summary>
    public List<CheckBox> CheckRow(string label, params string[] texts)
    {
        var row = LabelRow(label);
        var flow = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(660, 0), Margin = Padding.Empty };
        var list = new List<CheckBox>();
        foreach (var t in texts)
        {
            var cb = new CheckBox { Text = t, AutoSize = true, Margin = new Padding(3, 6, 10, 2) };
            flow.Controls.Add(cb);
            list.Add(cb);
        }
        _t.Controls.Add(flow, 1, row);
        _t.SetColumnSpan(flow, 2);
        return list;
    }

    /// <summary>一行按钮 + 右侧结果文字。</summary>
    public (List<Button> Buttons, Label Result) Actions(params string[] texts)
    {
        var row = NewRow();
        var flow = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(0, 10, 0, 4) };
        var buttons = new List<Button>();
        foreach (var t in texts)
        {
            var b = new Button { Text = t, AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
            flow.Controls.Add(b);
            buttons.Add(b);
        }
        var result = new Label { AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(8, 8, 3, 3) };
        flow.Controls.Add(result);
        _t.Controls.Add(flow, 1, row);
        _t.SetColumnSpan(flow, 2);
        return (buttons, result);
    }

    public static void SetResult(Label l, bool ok, string text)
    {
        l.ForeColor = ok ? Color.FromArgb(0, 128, 0) : Color.FromArgb(200, 30, 30);
        l.Text = (ok ? "✓ " : "✗ ") + text;
    }
}
