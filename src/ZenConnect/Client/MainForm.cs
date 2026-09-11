using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenConnect.Client;

public sealed class MainForm : Form
{
    private const string ProductionServerUrl = "https://zenconnect-noli.onrender.com";
    private const int ExitHotKeyId = 0x5A58;
    private const int WmHotKey = 0x0312;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly Color Background = Color.FromArgb(13, 18, 22);
    private static readonly Color Surface = Color.FromArgb(21, 28, 33);
    private static readonly Color SurfaceRaised = Color.FromArgb(27, 36, 42);
    private static readonly Color Border = Color.FromArgb(46, 59, 67);
    private static readonly Color TextColor = Color.FromArgb(238, 243, 245);
    private static readonly Color MutedColor = Color.FromArgb(145, 160, 168);
    private static readonly Color AccentColor = Color.FromArgb(43, 169, 199);
    private static readonly Color AccentHover = Color.FromArgb(36, 148, 176);
    private static readonly Color ErrorColor = Color.FromArgb(236, 112, 99);

    private readonly ZenConnectService _service = new();
    private readonly string _serverUrl;
    private readonly TextBox _roomKey;
    private readonly Label _status;
    private readonly Label _connectionBadge;
    private readonly Button _primaryButton;
    private readonly NotifyIcon _trayIcon;
    private bool _connected;
    private bool _allowExit;
    private bool _exitHotKeyRegistered;
    private bool _backgroundNoticeShown;

    public MainForm()
    {
        Text = "ZenConnect";
        ClientSize = new Size(590, 405);
        MinimumSize = new Size(606, 444);
        MaximumSize = new Size(606, 444);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "ZenConnect đang chạy nền",
            Visible = true,
            BalloonTipTitle = "ZenConnect vẫn đang chạy",
            BalloonTipText = "Nhấp đúp biểu tượng để mở lại. Nhấn Shift + Tab để thoát hẳn.",
            BalloonTipIcon = ToolTipIcon.Info
        };
        _trayIcon.DoubleClick += delegate { RestoreWindow(); };

        Controls.Add(new Panel
        {
            BackColor = AccentColor,
            Location = new Point(30, 30),
            Size = new Size(5, 38)
        });
        Controls.Add(new Label
        {
            Text = "ZenConnect",
            Font = new Font("Segoe UI Semibold", 20f, FontStyle.Bold),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(49, 24)
        });
        Controls.Add(new Label
        {
            Text = "Chụp màn hình và nhận phản hồi tức thời",
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(51, 58)
        });

        _connectionBadge = new Label
        {
            Text = "CHƯA KẾT NỐI",
            ForeColor = MutedColor,
            BackColor = SurfaceRaised,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(425, 32),
            Size = new Size(135, 30)
        };
        Controls.Add(_connectionBadge);

        _serverUrl = LoadServerUrl();

        var roomPanel = new Panel
        {
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 96),
            Size = new Size(530, 112)
        };
        roomPanel.Controls.Add(MakeCaption("ROOM KEY CỦA MÁY", 18, 15));
        roomPanel.Controls.Add(new Label
        {
            Text = "Dùng key này để mở đúng phòng review trên web.",
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(18, 36)
        });
        _roomKey = new TextBox
        {
            Text = _service.DeviceKey,
            ReadOnly = true,
            BackColor = Background,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(18, 66),
            Size = new Size(356, 30),
            Font = new Font("Consolas", 13f, FontStyle.Bold),
            TextAlign = HorizontalAlignment.Center
        };
        roomPanel.Controls.Add(_roomKey);
        var copyButton = MakeButton("SAO CHÉP KEY", 386, 65, 124, 32, SurfaceRaised, TextColor);
        copyButton.Click += delegate { CopyRoomKey(); };
        roomPanel.Controls.Add(copyButton);
        Controls.Add(roomPanel);

        _primaryButton = MakeButton("KẾT NỐI", 30, 228, 530, 52, AccentColor, Background);
        _primaryButton.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        _primaryButton.Click += async delegate
        {
            if (_connected)
                await CaptureFullScreenAsync();
            else
                await ConnectAsync();
        };
        Controls.Add(_primaryButton);

        var statusPanel = new Panel
        {
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 300),
            Size = new Size(530, 52)
        };
        statusPanel.Controls.Add(new Panel { BackColor = AccentColor, Dock = DockStyle.Left, Width = 4 });
        _status = new Label
        {
            Text = "Sẵn sàng kết nối",
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(18, 14),
            Size = new Size(494, 23),
            TextAlign = ContentAlignment.MiddleLeft
        };
        statusPanel.Controls.Add(_status);
        Controls.Add(statusPanel);

        Controls.Add(new Label
        {
            Text = "X: chụp nhanh    Chuột phải: hiện/ẩn    Lăn chuột: chuyển phản hồi    Shift+Tab: thoát",
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(31, 370),
            Font = new Font("Segoe UI", 8.5f)
        });

        FormClosing += HandleFormClosing;
        FormClosed += delegate
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _service.Disconnect();
            _service.Dispose();
        };
        Shown += async delegate { await ConnectAsync(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _exitHotKeyRegistered = RegisterHotKey(
            Handle, ExitHotKeyId, ModShift | ModNoRepeat, (uint)Keys.Tab);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_exitHotKeyRegistered)
        {
            UnregisterHotKey(Handle, ExitHotKeyId);
            _exitHotKeyRegistered = false;
        }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotKey && message.WParam.ToInt32() == ExitHotKeyId)
        {
            _allowExit = true;
            Close();
            return;
        }
        base.WndProc(ref message);
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit || e.CloseReason == CloseReason.WindowsShutDown)
            return;

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;
        if (!_backgroundNoticeShown)
        {
            _backgroundNoticeShown = true;
            _trayIcon.ShowBalloonTip(3500);
        }
    }

    private void RestoreWindow()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
    }

    private async Task ConnectAsync()
    {
        string server = _serverUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Server URL phải bắt đầu bằng http:// hoặc https://.", StatusKind.Error);
            return;
        }

        SetWorking(true, "ĐANG KẾT NỐI...");
        SetStatus("Đang đăng ký phòng và thiết lập phím tắt...", StatusKind.Info);
        try
        {
            _service.ServerUrl = server;
            await _service.ConnectAsync();
            _connected = true;
            _connectionBadge.Text = "ĐÃ KẾT NỐI";
            _connectionBadge.ForeColor = AccentColor;
            SetStatus("Đã kết nối. Nhấn X để chụp nhanh; X vẫn gõ được trong ô nhập.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            _service.Disconnect();
            SetStatus("Không thể kết nối: " + FriendlyError(ex), StatusKind.Error);
        }
        finally
        {
            SetWorking(false, null);
        }
    }

    private async Task CaptureFullScreenAsync()
    {
        SetWorking(true, "ĐANG CHỤP...");
        SetStatus("Đang chụp và tải ảnh lên phòng...", StatusKind.Info);
        try
        {
            await _service.CaptureFullScreenAsync();
            ShowCaptureResult();
        }
        finally
        {
            SetWorking(false, null);
        }
    }

    private void ShowCaptureResult()
    {
        string result = _service.CaptureStatus;
        bool failed = result.Contains("that bai", StringComparison.OrdinalIgnoreCase)
            || result.Contains("thất bại", StringComparison.OrdinalIgnoreCase);
        SetStatus(result, failed ? StatusKind.Error : StatusKind.Success);
    }

    private void SetWorking(bool working, string? buttonText)
    {
        _primaryButton.Enabled = !working;
        _primaryButton.Text = working
            ? buttonText ?? "ĐANG XỬ LÝ..."
            : _connected ? "CHỤP TOÀN MÀN HÌNH" : "KẾT NỐI";
    }

    private void CopyRoomKey()
    {
        try
        {
            Clipboard.SetDataObject(_service.DeviceKey, true, 10, 50);
            SetStatus("Đã sao chép room key vào clipboard.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Không thể sao chép key: " + ex.Message, StatusKind.Error);
        }
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status.Text = text;
        _status.ForeColor = kind == StatusKind.Error
            ? ErrorColor
            : kind == StatusKind.Success ? AccentColor : MutedColor;
    }

    private static string FriendlyError(Exception exception)
    {
        if (exception is System.Net.Http.HttpRequestException)
            return "không truy cập được server";
        if (exception is TaskCanceledException)
            return "server phản hồi quá chậm";
        return exception.Message;
    }

    private static string LoadServerUrl()
    {
        string? configured = Environment.GetEnvironmentVariable("ZENCONNECT_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return ProductionServerUrl;
    }

    private static Label MakeCaption(string text, int x, int y)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8f, FontStyle.Bold)
        };
    }

    private static Button MakeButton(string text, int x, int y, int width, int height, Color backColor, Color foreColor)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = backColor == AccentColor ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = backColor == AccentColor
            ? AccentHover
            : Color.FromArgb(35, 47, 54);
        button.FlatAppearance.MouseDownBackColor = backColor == AccentColor ? AccentHover : Background;
        return button;
    }

    private enum StatusKind
    {
        Info,
        Success,
        Error
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
