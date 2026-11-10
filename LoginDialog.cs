namespace FO3SessionTool;

/// <summary>Modal login popup. On success exposes the signed GameApi, user name and launcher token.</summary>
public sealed class LoginDialog : Form
{
    private readonly Config _cfg;
    private readonly DarkTextBox _user = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    private readonly DarkTextBox _pass = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, UseSystemPasswordChar = true };
    private readonly DarkButton _ok = new() { Text = "Đăng nhập", Width = 110, Tag = "primary" };
    private readonly DarkButton _cancel = new() { Text = "Hủy", Width = 80, DialogResult = DialogResult.Cancel };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Dim, TextAlign = ContentAlignment.MiddleLeft };
    private CancellationTokenSource? _cts;

    public GameApi? Api { get; private set; }
    public string User { get; private set; } = "";
    public string Token { get; private set; } = "";

    public LoginDialog(Config cfg, string lastUser)
    {
        _cfg = cfg;
        Text = "Đăng nhập 3Sea";
        ClientSize = new Size(380, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = Theme.Body;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        AcceptButton = _ok;
        CancelButton = _cancel;

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(16, 14, 16, 10) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.RowH));
        t.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.Controls.Add(new Label { Text = "Tài khoản", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_user, 1, 0);
        t.Controls.Add(new Label { Text = "Mật khẩu", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(_pass, 1, 1);
        t.Controls.Add(_status, 0, 2);
        t.SetColumnSpan(_status, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);
        t.Controls.Add(buttons, 0, 3);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);
        Theme.Apply(this);

        _user.Text = lastUser;
        _ok.Click += async (_, _) => await LoginAsync();
        FormClosing += (_, _) => _cts?.Cancel();
        Shown += (_, _) => (_user.Text.Length == 0 ? _user : _pass).Focus();
    }

    private async Task LoginAsync()
    {
        if (_cts != null) return;
        var user = _user.Text.Trim();
        if (user.Length == 0 || _pass.Text.Length == 0) { SetStatus("Nhập tài khoản và mật khẩu.", Theme.Danger); return; }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var api = new GameApi(_cfg);
            SetStatus("Đang đăng nhập...", Theme.Dim);
            var token = await new LauncherClient(_cfg).LoginAsync(user, _pass.Text,
                m => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => SetStatus(m, Theme.Warn)); }, _cts.Token);
            await api.SignAsync(token, _cts.Token);

            Api = api; User = user; Token = token;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) { if (!IsDisposed) SetStatus("Đã hủy.", Theme.Dim); }
        catch (Exception ex) { if (!IsDisposed) SetStatus("Đăng nhập thất bại: " + ex.Message, Theme.Danger); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _ok.Enabled = !busy;
        _user.Enabled = !busy;
        _pass.Enabled = !busy;
    }

    private void SetStatus(string text, Color c) { _status.Text = text; _status.ForeColor = c; }
}
