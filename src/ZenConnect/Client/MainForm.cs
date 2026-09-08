using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenConnect.Client;

public sealed class MainForm : Form
{
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
    private readonly TextBox _serverUrl;
    private readonly TextBox _roomKey;
    private readonly Label _status;
    private readonly Label _connectionBadge;
    private readonly Button _primaryButton;
    private readonly Button _regionButton;
    private readonly Button _openWebButton;
    private readonly Button _serverActionButton;
    private bool _connected;

    public MainForm()
    {
        Text = "ZenConnect";
        ClientSize = new Size(590, 550);
        MinimumSize = new Size(606, 589);
        MaximumSize = new Size(606, 589);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

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

        Controls.Add(MakeCaption("SERVER URL", 30, 96));
        _serverUrl = new TextBox
        {
            Text = LoadServerUrl(),
            BackColor = SurfaceRaised,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 120),
            Size = new Size(394, 31),
            Font = new Font("Segoe UI", 10.5f)
        };
        Controls.Add(_serverUrl);

        _serverActionButton = MakeButton("DÙNG LOCAL", 438, 119, 122, 33, SurfaceRaised, TextColor);
        _serverActionButton.Click += delegate
        {
            if (_connected)
            {
                DisconnectForServerChange();
            }
            else
            {
                _serverUrl.Text = "http://localhost:5080";
                SetStatus("Đã chọn server local. Nhấn Kết nối để tiếp tục.", StatusKind.Info);
            }
        };
        Controls.Add(_serverActionButton);

        var roomPanel = new Panel
        {
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 178),
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

        _primaryButton = MakeButton("KẾT NỐI", 30, 316, 530, 52, AccentColor, Background);
        _primaryButton.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        _primaryButton.Click += async delegate
        {
            if (_connected)
                await CaptureFullScreenAsync();
            else
                await ConnectAsync();
        };
        Controls.Add(_primaryButton);

        _regionButton = MakeButton("CHỤP VÙNG", 30, 380, 255, 42, SurfaceRaised, TextColor);
        _regionButton.Enabled = false;
        _regionButton.Click += async delegate { await CaptureRegionAsync(); };
        Controls.Add(_regionButton);

        _openWebButton = MakeButton("MỞ PHÒNG WEB", 305, 380, 255, 42, SurfaceRaised, TextColor);
        _openWebButton.Enabled = false;
        _openWebButton.Click += delegate { OpenDashboard(); };
        Controls.Add(_openWebButton);

        var statusPanel = new Panel
        {
            BackColor = Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(30, 448),
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
            Text = "X: chụp nhanh    Chuột phải: hiện/ẩn    Lăn chuột: chuyển phản hồi",
            ForeColor = MutedColor,
            AutoSize = true,
            Location = new Point(31, 516),
            Font = new Font("Segoe UI", 8.5f)
        });

        FormClosing += delegate
        {
            _service.Disconnect();
            _service.Dispose();
        };
        Shown += async delegate { await ConnectAsync(); };
    }

    private async Task ConnectAsync()
    {
        string server = _serverUrl.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("Server URL phải bắt đầu bằng http:// hoặc https://.", StatusKind.Error);
            _serverUrl.Focus();
            return;
        }

        SetWorking(true, "ĐANG KẾT NỐI...");
        SetStatus("Đang đăng ký phòng và thiết lập phím tắt...", StatusKind.Info);
        try
        {
            _service.ServerUrl = server;
            await _service.ConnectAsync();
            SaveServerUrl(server);
            _connected = true;
            _serverUrl.Enabled = false;
            _serverActionButton.Text = "ĐỔI SERVER";
            _regionButton.Enabled = true;
            _openWebButton.Enabled = true;
            _connectionBadge.Text = "ĐÃ KẾT NỐI";
            _connectionBadge.ForeColor = AccentColor;
            SetStatus("Đã kết nối. Nhấn X để chụp nhanh hoặc chọn vùng trên tool.", StatusKind.Success);
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

    private async Task CaptureRegionAsync()
    {
        SetWorking(true, "ĐANG CHỌN VÙNG...");
        SetStatus("Kéo chuột để chọn vùng. Nhấn Esc để hủy.", StatusKind.Info);
        try
        {
            await _service.CaptureRegionAsync();
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

    private void DisconnectForServerChange()
    {
        _service.Disconnect();
        _connected = false;
        _serverUrl.Enabled = true;
        _serverActionButton.Text = "DÙNG LOCAL";
        _regionButton.Enabled = false;
        _openWebButton.Enabled = false;
        _connectionBadge.Text = "CHƯA KẾT NỐI";
        _connectionBadge.ForeColor = MutedColor;
        _primaryButton.Text = "KẾT NỐI";
        SetStatus("Đã ngắt kết nối. Bạn có thể đổi server URL.", StatusKind.Info);
        _serverUrl.Focus();
    }

    private void SetWorking(bool working, string? buttonText)
    {
        _primaryButton.Enabled = !working;
        _regionButton.Enabled = !working && _connected;
        _serverActionButton.Enabled = !working;
        _openWebButton.Enabled = !working && _connected;
        _primaryButton.Text = working
            ? buttonText ?? "ĐANG XỬ LÝ..."
            : _connected ? "CHỤP TOÀN MÀN HÌNH" : "KẾT NỐI";
    }

    private void CopyRoomKey()
    {
        try
        {
            Clipboard.SetText(_service.DeviceKey);
            SetStatus("Đã sao chép room key vào clipboard.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus("Không thể sao chép key: " + ex.Message, StatusKind.Error);
        }
    }

    private void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _serverUrl.Text.Trim().TrimEnd('/')
                    + "/?v=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus("Không thể mở trình duyệt: " + ex.Message, StatusKind.Error);
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

        string path = Path.Combine(GetSettingsDirectory(), "server-url.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "http://localhost:5080";
    }

    private static string GetSettingsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenConnect");
    }

    private static void SaveServerUrl(string serverUrl)
    {
        string directory = GetSettingsDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "server-url.txt");
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, serverUrl);
        File.Move(temporaryPath, path, true);
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
}
