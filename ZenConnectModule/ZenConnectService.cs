using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenConnect;

public sealed class ZenConnectService : Form
{
    private const int CaptureHotKeyId = 0x5343;
    private const int WmHotKey = 0x0312;
    private const int WhMouseLowLevel = 14;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmMiddleButtonDown = 0x0207;
    private const int WmMouseWheel = 0x020A;
    private const uint ModNoRepeat = 0x4000;

    private static readonly Color BgColor = Color.FromArgb(244, 246, 248);
    private static readonly Color TextColor = Color.FromArgb(28, 36, 42);
    private static readonly Color MutedColor = Color.FromArgb(105, 115, 124);
    private static readonly Color AccentColor = Color.FromArgb(25, 118, 150);

    private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly string _deviceKey;
    private IntPtr _mouseHook = IntPtr.Zero;
    private readonly MouseHookCallback _mouseHookCallback;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private readonly KeyboardHookCallback _keyboardHookCallback;
    private readonly MessageOverlayForm _messageOverlay;

    private TextBox _serverUrl;
    private TextBox _deviceKeyText;
    private Label _connectionStatus;
    private Label _captureStatus;
    private PictureBox _preview;
    private ListBox _messages;
    private TextBox _messageText;
    private Button _captureFullButton;
    private Button _captureRegionButton;
    private Button _refreshButton;
    private System.Windows.Forms.Timer _pollTimer;
    private long _lastMessageId;
    private bool _busy;
    private bool _backgroundMode;
    private bool _connected;
    private int _overlayMessageIndex = -1;
    private string _registeredServerUrl;

    public ZenConnectService()
    {
        _deviceKey = LoadOrCreateDeviceKey();
        _mouseHookCallback = HandleMouseHook;
        _keyboardHookCallback = HandleKeyboardHook;
        _messageOverlay = new MessageOverlayForm();
        Text = "ZenConnect";
        ClientSize = new Size(860, 580);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = BgColor;
        Font = new Font("Segoe UI", 9f);
        BuildUi();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _pollTimer.Tick += async delegate { await PollAsync(); };
        _pollTimer.Start();

        FormClosing += delegate
        {
            _pollTimer.Stop();
            StopInputHooks();
            _messageOverlay.Close();
            _http.Dispose();
        };
    }

    public string DeviceKey { get { return _deviceKey; } }
    public string CaptureStatus { get { return _captureStatus.Text; } }
    public string ServerUrl
    {
        get { return _serverUrl.Text; }
        set
        {
            _serverUrl.Text = (value ?? string.Empty).Trim();
            _registeredServerUrl = null;
        }
    }

    public async Task ConnectAsync()
    {
        _backgroundMode = true;
        ShowInTaskbar = false;
        Opacity = 0;

        if (!IsHandleCreated)
        {
            CreateControl();
            IntPtr unused = Handle;
        }

        await EnsureRoomRegisteredAsync();
        StartInputHooks();
        _connected = true;
        _connectionStatus.Text = "Da ket noi.";
        _connectionStatus.ForeColor = Color.FromArgb(35, 130, 75);
        _pollTimer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopInputHooks();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (_connected && message.Msg == WmHotKey && message.WParam.ToInt32() == CaptureHotKeyId)
        {
            BeginInvoke(new Action(async () => await CaptureFullScreenAsync()));
        }

        base.WndProc(ref message);
    }

    private void BuildUi()
    {
        var connectionPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 104,
            BackColor = Color.White,
            Padding = new Padding(16, 12, 16, 10)
        };
        Controls.Add(connectionPanel);

        connectionPanel.Controls.Add(MakeLabel("SERVER URL", new Point(16, 10), true));
        _serverUrl = MakeTextBox("https://zenconnect-noli.onrender.com", new Point(16, 32), new Size(280, 27));
        connectionPanel.Controls.Add(_serverUrl);

        connectionPanel.Controls.Add(MakeLabel("KEY PHONG CUA MAY", new Point(310, 10), true));
        _deviceKeyText = MakeTextBox(_deviceKey, new Point(310, 32), new Size(272, 27));
        _deviceKeyText.ReadOnly = true;
        _deviceKeyText.BackColor = Color.White;
        connectionPanel.Controls.Add(_deviceKeyText);

        var copyKeyButton = MakeButton("COPY", new Point(592, 30), new Size(62, 31), Color.FromArgb(75, 88, 98));
        copyKeyButton.Click += delegate
        {
            Clipboard.SetText(_deviceKey);
            _connectionStatus.Text = "Da sao chep key phong.";
            _connectionStatus.ForeColor = Color.FromArgb(35, 130, 75);
        };
        connectionPanel.Controls.Add(copyKeyButton);

        _refreshButton = MakeButton("KET NOI", new Point(668, 30), new Size(160, 31), AccentColor);
        _refreshButton.Click += async delegate { await PollAsync(true); };
        connectionPanel.Controls.Add(_refreshButton);

        _connectionStatus = MakeLabel("Chua ket noi", new Point(16, 72), false);
        _connectionStatus.ForeColor = MutedColor;
        connectionPanel.Controls.Add(_connectionStatus);

        var workArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(16),
            BackColor = BgColor
        };
        workArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        workArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42f));
        Controls.Add(workArea);

        var capturePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
        var messagePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(12, 0, 0, 0) };
        workArea.Controls.Add(capturePanel, 0, 0);
        workArea.Controls.Add(messagePanel, 1, 0);

        var captureTitle = MakeLabel("Chup man hinh", new Point(14, 14), false);
        captureTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        capturePanel.Controls.Add(captureTitle);

        _captureFullButton = MakeButton("TOAN MAN HINH", new Point(14, 48), new Size(150, 34), AccentColor);
        _captureFullButton.Click += async delegate { await CaptureFullScreenAsync(); };
        capturePanel.Controls.Add(_captureFullButton);

        _captureRegionButton = MakeButton("CHON VUNG", new Point(174, 48), new Size(130, 34), Color.FromArgb(75, 88, 98));
        _captureRegionButton.Click += async delegate { await CaptureRegionAsync(); };
        capturePanel.Controls.Add(_captureRegionButton);

        _captureStatus = MakeLabel("San sang", new Point(14, 94), false);
        _captureStatus.ForeColor = MutedColor;
        capturePanel.Controls.Add(_captureStatus);

        _preview = new PictureBox
        {
            Location = new Point(14, 122),
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(235, 238, 240),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        capturePanel.Controls.Add(_preview);
        capturePanel.Resize += delegate
        {
            _preview.Size = new Size(Math.Max(100, capturePanel.ClientSize.Width - 28), Math.Max(100, capturePanel.ClientSize.Height - 136));
        };

        var messageTitle = MakeLabel("Tin nhan phan hoi", new Point(14, 14), false);
        messageTitle.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        messagePanel.Controls.Add(messageTitle);

        _messages = new ListBox
        {
            Location = new Point(14, 48),
            Height = 172,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            IntegralHeight = false
        };
        _messages.SelectedIndexChanged += delegate { ShowSelectedMessage(); };
        messagePanel.Controls.Add(_messages);

        _messageText = new TextBox
        {
            Location = new Point(14, 232),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            ForeColor = TextColor,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        messagePanel.Controls.Add(_messageText);
        messagePanel.Resize += delegate
        {
            _messages.Width = Math.Max(100, messagePanel.ClientSize.Width - 28);
            _messageText.Size = new Size(Math.Max(100, messagePanel.ClientSize.Width - 28), Math.Max(80, messagePanel.ClientSize.Height - 246));
        };
    }

    public async Task CaptureFullScreenAsync()
    {
        if (!CanOperate())
            return;

        SetBusy(true, "Dang chup toan man hinh...");
        Bitmap bitmap = null;
        try
        {
            Hide();
            await Task.Delay(180);
            Rectangle bounds = SystemInformation.VirtualScreen;
            bitmap = CaptureScreen(bounds);
        }
        catch (Exception ex)
        {
            _captureStatus.Text = "Chup anh that bai: " + FriendlyError(ex);
            SetBusy(false, null);
        }
        finally
        {
            RestoreAfterCapture();
        }

        if (bitmap != null)
            await UploadAsync(bitmap);
    }

    private async Task CaptureRegionAsync()
    {
        if (!CanOperate())
            return;

        Rectangle? selected = null;
        bool captureFailed = false;
        try
        {
            Hide();
            await Task.Delay(120);
            using (var selector = new RegionSelectorForm())
                selected = selector.SelectRegion();

            if (selected.HasValue)
            {
                SetBusy(true, "Dang chup vung da chon...");
                Bitmap bitmap = CaptureScreen(selected.Value);
                RestoreAfterCapture();
                await UploadAsync(bitmap);
                return;
            }
        }
        catch (Exception ex)
        {
            captureFailed = true;
            _captureStatus.Text = "Chup anh that bai: " + FriendlyError(ex);
            SetBusy(false, null);
        }
        finally
        {
            if (!_backgroundMode && !Visible)
            {
                Show();
                Activate();
            }
        }

        if (!captureFailed)
            _captureStatus.Text = "Da huy chon vung.";
    }

    private static Bitmap CaptureScreen(Rectangle bounds)
    {
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private void RestoreAfterCapture()
    {
        if (_backgroundMode)
        {
            Hide();
            return;
        }

        Show();
        Activate();
    }

    private async Task UploadAsync(Bitmap bitmap)
    {
        try
        {
            await EnsureRoomRegisteredAsync();
            SetPreview(bitmap);
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;

                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("screenshots")))
                using (var form = new MultipartFormDataContent())
                using (var image = new ByteArrayContent(stream.ToArray()))
                {
                    image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    form.Add(image, "image", "capture.png");
                    request.Content = form;
                    AddAccessKey(request);

                    using (HttpResponseMessage response = await _http.SendAsync(request))
                    {
                        string payload = await response.Content.ReadAsStringAsync();
                        response.EnsureSuccessStatusCode();
                        var uploaded = JsonSerializer.Deserialize<ScreenshotResponse>(payload, _jsonOptions);
                        _captureStatus.Text = uploaded == null
                            ? "Da tai anh len."
                            : "Da tai anh so " + uploaded.Sequence + ".";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _registeredServerUrl = null;
            _captureStatus.Text = "Tai anh that bai: " + FriendlyError(ex);
        }
        finally
        {
            bitmap.Dispose();
            SetBusy(false, null);
        }
    }

    private async Task PollAsync(bool reportSuccess = false)
    {
        if (_busy || IsDisposed)
            return;

        if (!HasConnectionSettings())
            return;

        try
        {
            await EnsureRoomRegisteredAsync();
            using (var request = new HttpRequestMessage(HttpMethod.Get, BuildApiUrl("messages?after=" + _lastMessageId)))
            {
                AddAccessKey(request);
                using (HttpResponseMessage response = await _http.SendAsync(request))
                {
                    string payload = await response.Content.ReadAsStringAsync();
                    response.EnsureSuccessStatusCode();
                    var items = JsonSerializer.Deserialize<List<MessageResponse>>(payload, _jsonOptions) ?? new List<MessageResponse>();
                    foreach (MessageResponse item in items.OrderBy(x => x.Id))
                    {
                        _messages.Items.Add(item);
                        _lastMessageId = Math.Max(_lastMessageId, item.Id);
                    }

                    if (items.Count > 0)
                    {
                        _messages.SelectedIndex = _messages.Items.Count - 1;
                        if (_messageOverlay.Visible)
                        {
                            _overlayMessageIndex = _messages.Items.Count - 1;
                            ShowCurrentOverlayMessage();
                        }
                    }

                    _connectionStatus.Text = reportSuccess ? "Da ket noi." : "Dang ket noi";
                    _connectionStatus.ForeColor = Color.FromArgb(35, 130, 75);
                }
            }
        }
        catch (Exception ex)
        {
            _registeredServerUrl = null;
            _connectionStatus.Text = "Mat ket noi: " + FriendlyError(ex);
            _connectionStatus.ForeColor = Color.FromArgb(180, 55, 45);
        }
    }

    private bool CanOperate()
    {
        if (_busy)
            return false;

        if (!HasConnectionSettings())
        {
            MessageBox.Show("Hay nhap server URL.", "ZenConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private bool HasConnectionSettings()
    {
        return !string.IsNullOrWhiteSpace(_serverUrl.Text);
    }

    private string BuildApiUrl(string resource)
    {
        string server = _serverUrl.Text.Trim().TrimEnd('/');
        string session = Uri.EscapeDataString(_deviceKey);
        return server + "/api/sessions/" + session + "/" + resource;
    }

    private void AddAccessKey(HttpRequestMessage request)
    {
        request.Headers.Add("X-Screen-Review-Key", _deviceKey);
    }

    private async Task EnsureRoomRegisteredAsync()
    {
        string server = _serverUrl.Text.Trim().TrimEnd('/');
        if (string.Equals(server, _registeredServerUrl, StringComparison.OrdinalIgnoreCase))
            return;

        using (var request = new HttpRequestMessage(HttpMethod.Post, server + "/api/rooms/register"))
        {
            AddAccessKey(request);
            using (HttpResponseMessage response = await _http.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                _registeredServerUrl = server;
            }
        }
    }

    private static string LoadOrCreateDeviceKey()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZenConnect");
        string path = Path.Combine(directory, "device-key.txt");

        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim().ToUpperInvariant();
            if (IsValidDeviceKey(existing))
                return existing;
        }

        Directory.CreateDirectory(directory);
        string created = GenerateDeviceKey();
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, created, Encoding.ASCII);
        File.Move(temporaryPath, path, true);
        return created;
    }

    private static string GenerateDeviceKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        byte[] random = RandomNumberGenerator.GetBytes(20);
        var value = new StringBuilder(24);
        for (int index = 0; index < random.Length; index++)
        {
            if (index > 0 && index % 4 == 0)
                value.Append('-');
            value.Append(alphabet[random[index] & 31]);
        }
        return value.ToString();
    }

    private static bool IsValidDeviceKey(string value)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string[] groups = (value ?? string.Empty).Split('-');
        return groups.Length == 5
            && groups.All(group => group.Length == 4 && group.All(character => alphabet.Contains(character)));
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        _captureFullButton.Enabled = !busy;
        _captureRegionButton.Enabled = !busy;
        if (!string.IsNullOrEmpty(status))
            _captureStatus.Text = status;
    }

    private void SetPreview(Bitmap bitmap)
    {
        Image old = _preview.Image;
        _preview.Image = new Bitmap(bitmap);
        if (old != null)
            old.Dispose();
    }

    private void ShowSelectedMessage()
    {
        var selected = _messages.SelectedItem as MessageResponse;
        _messageText.Text = selected == null ? string.Empty : selected.Text;
    }

    private void StartInputHooks()
    {
        IntPtr module = GetModuleHandle(null);
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardHookCallback, module, 0);
            if (_keyboardHook == IntPtr.Zero)
                throw new InvalidOperationException("Khong the theo doi phim X tren may nay.");
        }

        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseHookCallback, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                throw new InvalidOperationException("Khong the theo doi chuot tren may nay.");
            }
        }
    }

    private void StopInputHooks()
    {
        _connected = false;
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr HandleMouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && _connected && !IsDisposed)
        {
            int mouseMessage = message.ToInt32();
            if (mouseMessage == WmMiddleButtonDown)
            {
                QueueOnUiThread(ToggleMessageOverlay);
            }
            else if (mouseMessage == WmMouseWheel && _messageOverlay.Visible)
            {
                var input = Marshal.PtrToStructure<LowLevelMouseInput>(data);
                int delta = unchecked((short)((input.MouseData >> 16) & 0xffff));
                QueueOnUiThread(() => CycleOverlayMessage(delta));
            }
        }

        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private IntPtr HandleKeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0
            && _connected
            && !IsDisposed
            && message.ToInt32() == WmKeyDown
            && Marshal.ReadInt32(data) == (int)Keys.X)
        {
            QueueOnUiThread(async () => await CaptureFullScreenAsync());
        }

        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private void QueueOnUiThread(Action action)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ToggleMessageOverlay()
    {
        if (_messageOverlay.Visible)
        {
            _messageOverlay.Hide();
            return;
        }

        _overlayMessageIndex = _messages.Items.Count - 1;
        ShowCurrentOverlayMessage();
    }

    private void CycleOverlayMessage(int wheelDelta)
    {
        int count = _messages.Items.Count;
        if (!_messageOverlay.Visible || count == 0 || wheelDelta == 0)
            return;

        int direction = wheelDelta > 0 ? -1 : 1;
        _overlayMessageIndex = (_overlayMessageIndex + direction + count) % count;
        ShowCurrentOverlayMessage();
    }

    private void ShowCurrentOverlayMessage()
    {
        int count = _messages.Items.Count;
        if (count == 0)
        {
            _messageOverlay.SetMessage("Tin nhan", "Chua co tin nhan.", "0 / 0");
        }
        else
        {
            _overlayMessageIndex = Math.Max(0, Math.Min(_overlayMessageIndex, count - 1));
            var message = (MessageResponse)_messages.Items[_overlayMessageIndex];
            _messageOverlay.SetMessage(
                "Anh " + message.Sequence + " · " + message.CreatedAt.ToLocalTime().ToString("HH:mm:ss"),
                message.Text,
                (_overlayMessageIndex + 1) + " / " + count);
        }

        _messageOverlay.ShowNear(Cursor.Position);
    }

    private static string FriendlyError(Exception exception)
    {
        if (exception is HttpRequestException)
            return "khong truy cap duoc server";
        if (exception is TaskCanceledException)
            return "server phan hoi qua cham";
        return exception.Message;
    }

    private static Label MakeLabel(string text, Point location, bool caption)
    {
        return new Label
        {
            Text = text,
            Location = location,
            AutoSize = true,
            ForeColor = caption ? MutedColor : TextColor,
            Font = new Font("Segoe UI", caption ? 7.5f : 9f, caption ? FontStyle.Bold : FontStyle.Regular)
        };
    }

    private static TextBox MakeTextBox(string text, Point location, Size size)
    {
        return new TextBox { Text = text, Location = location, Size = size, BorderStyle = BorderStyle.FixedSingle };
    }

    private static Button MakeButton(string text, Point location, Size size, Color color)
    {
        var button = new Button
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = color,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private sealed class ScreenshotResponse
    {
        public int Sequence { get; set; }
    }

    private sealed class MessageResponse
    {
        public long Id { get; set; }
        public int Sequence { get; set; }
        public string Text { get; set; }
        public DateTimeOffset CreatedAt { get; set; }

        public override string ToString()
        {
            return "Anh " + Sequence + "  |  " + CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookCallback lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookCallback lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseInput
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

public delegate IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam);
public delegate IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam);

internal sealed class MessageOverlayForm : Form
{
    private Label _titleLabel;
    private Label _textLabel;
    private Label _countLabel;

    public MessageOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(40, 44, 52); // Dark theme background
        Opacity = 0.95;
        Size = new Size(350, 75);

        _titleLabel = new Label
        {
            ForeColor = Color.LightGray,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Location = new Point(15, 10),
            AutoSize = true
        };

        _countLabel = new Label
        {
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Location = new Point(280, 10),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        _textLabel = new Label
        {
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(15, 40),
            Size = new Size(320, 25),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };

        Controls.Add(_titleLabel);
        Controls.Add(_countLabel);
        Controls.Add(_textLabel);
    }

    public void SetMessage(string title, string text, string count)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SetMessage(title, text, count)));
            return;
        }

        _titleLabel.Text = title;
        _textLabel.Text = text;
        _countLabel.Text = count;
        _countLabel.Left = Width - _countLabel.PreferredWidth - 15;
    }

    public void ShowNear(Point location)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => ShowNear(location)));
            return;
        }

        if (!Visible) Show();

        // Position it near the requested location (e.g., mouse cursor)
        int x = location.X + 20;
        int y = location.Y + 20;

        // Keep on screen
        var screen = Screen.FromPoint(location).Bounds;
        if (x + Width > screen.Right) x = location.X - Width - 20;
        if (y + Height > screen.Bottom) y = location.Y - Height - 20;

        Location = new Point(x, y);
    }
}


internal sealed class RegionSelectorForm : Form
{
    private Point _start;
    private Rectangle _selection;
    private bool _dragging;
    private bool _confirmed;

    public RegionSelectorForm()
    {
        Bounds = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.28;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    public Rectangle? SelectRegion()
    {
        ShowDialog();
        if (!_confirmed || _selection.Width < 4 || _selection.Height < 4)
            return null;

        return new Rectangle(
            Bounds.Left + _selection.Left,
            Bounds.Top + _selection.Top,
            _selection.Width,
            _selection.Height);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _start = e.Location;
            _selection = Rectangle.Empty;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            _selection = Rectangle.FromLTRB(
                Math.Min(_start.X, e.X),
                Math.Min(_start.Y, e.Y),
                Math.Max(_start.X, e.X),
                Math.Max(_start.Y, e.Y));
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging && e.Button == MouseButtons.Left)
        {
            _dragging = false;
            _confirmed = _selection.Width >= 4 && _selection.Height >= 4;
            Close();
        }
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            _confirmed = false;
            Close();
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_selection.IsEmpty)
            return;

        using (var brush = new SolidBrush(Color.FromArgb(70, Color.White)))
        using (var pen = new Pen(Color.White, 2f))
        {
            e.Graphics.FillRectangle(brush, _selection);
            e.Graphics.DrawRectangle(pen, _selection);
        }
    }
}
