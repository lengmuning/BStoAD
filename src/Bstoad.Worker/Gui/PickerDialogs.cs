using Bstoad.ActiveDirectory;
using Bstoad.Core.Models;

namespace Bstoad.Worker.Gui;

/// <summary>浏览 AD 的 OU 树并选择一个 OU（替代手抄 DN）。</summary>
internal sealed class OuPickerDialog : Form
{
    private readonly ILdapConnectionFactory _factory;
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly Label _dn = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private const string Dummy = "\u0001loading";

    public string? SelectedDn { get; private set; }

    public OuPickerDialog(ILdapConnectionFactory factory, string rootDn, string title)
    {
        _factory = factory;
        Text = title;
        Size = new Size(620, 560);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimizeBox = false;
        MaximizeBox = false;

        var ok = new Button { Text = "选择", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 44, ColumnCount = 3, Padding = new Padding(8, 6, 8, 6) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_dn, 0, 0);
        bottom.Controls.Add(ok, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_tree);
        Controls.Add(bottom);

        var root = new TreeNode(rootDn) { Tag = rootDn };
        root.Nodes.Add(Dummy);
        _tree.Nodes.Add(root);
        _tree.BeforeExpand += (_, e) => LoadChildren(e.Node!);
        _tree.AfterSelect += (_, e) =>
        {
            SelectedDn = e.Node?.Tag as string;
            _dn.Text = SelectedDn ?? "";
        };
        Shown += (_, _) => root.Expand();
    }

    private void LoadChildren(TreeNode node)
    {
        if (node.Nodes.Count != 1 || node.Nodes[0].Text != Dummy) return;
        node.Nodes.Clear();
        try
        {
            Cursor = Cursors.WaitCursor;
            foreach (var ou in AdBrowser.ListChildOus(_factory, (string)node.Tag!))
            {
                var text = string.IsNullOrEmpty(ou.Description) ? ou.Name : $"{ou.Name}    〔{ou.Description}〕";
                var child = new TreeNode(text) { Tag = ou.Dn };
                child.Nodes.Add(Dummy);
                node.Nodes.Add(child);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取 OU 失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }
}

/// <summary>从北森组织树中选择一个组织（替代手查 OrgId）。</summary>
internal sealed class OrgPickerDialog : Form
{
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, PlaceholderText = "输入部门名称查找，回车跳到下一个" };
    private readonly List<TreeNode> _all = new();
    private int _searchPos;

    public BeisenOrg? Selected { get; private set; }

    public OrgPickerDialog(IReadOnlyList<BeisenOrg> orgs, string title, string? currentId)
    {
        Text = title;
        Size = new Size(620, 620);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9f);
        MinimizeBox = false;
        MaximizeBox = false;

        var top = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 6, 8, 4) };
        top.Controls.Add(_search);

        var ok = new Button { Text = "选择", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 6, 8, 6) };
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(ok);
        CancelButton = cancel;

        Controls.Add(_tree);
        Controls.Add(top);
        Controls.Add(bottom);

        // 建树：父不在集合中的视为顶层
        var byId = orgs.Where(o => !string.IsNullOrEmpty(o.OrgId)).GroupBy(o => o.OrgId).ToDictionary(g => g.Key, g => g.First());
        var nodes = byId.Values.ToDictionary(o => o.OrgId, o =>
        {
            var n = new TreeNode($"{o.Name}  ({o.OrgId})") { Tag = o };
            _all.Add(n);
            return n;
        });
        foreach (var o in byId.Values.OrderBy(o => o.Name, StringComparer.CurrentCulture))
        {
            var n = nodes[o.OrgId];
            if (!string.IsNullOrEmpty(o.ParentOrgId) && o.ParentOrgId != o.OrgId && nodes.TryGetValue(o.ParentOrgId, out var parent))
                parent.Nodes.Add(n);
            else
                _tree.Nodes.Add(n);
        }
        foreach (TreeNode r in _tree.Nodes) r.Expand();

        if (currentId != null && nodes.TryGetValue(currentId, out var cur))
        {
            _tree.SelectedNode = cur;
            cur.EnsureVisible();
        }

        _tree.AfterSelect += (_, e) => Selected = e.Node?.Tag as BeisenOrg;
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            Selected = e.Node.Tag as BeisenOrg;
            DialogResult = DialogResult.OK;
        };
        ok.Click += (_, _) => Selected ??= _tree.SelectedNode?.Tag as BeisenOrg;
        _search.TextChanged += (_, _) => { _searchPos = 0; FindNext(); };
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            FindNext();
        };
    }

    private void FindNext()
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return;
        for (var i = 0; i < _all.Count; i++)
        {
            var n = _all[(_searchPos + i) % _all.Count];
            if (!n.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            _tree.SelectedNode = n;
            n.EnsureVisible();
            _searchPos = (_all.IndexOf(n) + 1) % _all.Count;
            return;
        }
    }
}
