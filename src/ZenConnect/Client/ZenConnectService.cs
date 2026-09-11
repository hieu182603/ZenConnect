using System;
using System.Collections.Generic;
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
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Automation;

namespace ZenConnect.Client;

public sealed class ZenConnectService : Form
{
    private const int CaptureHotKeyId = 0x5343;
    private const int PreviousMessageHotKeyId = 0x5350;
    private const int NextMessageHotKeyId = 0x534E;
    private const int WmHotKey = 0x0312;
    private const int WhMouseLowLevel = 14;
    private const int WmRightButtonDown = 0x0204;
    private const int WmMouseWheel = 0x020A;
    private const int WheelDelta = 120;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private static readonly Color BgColor = Color.FromArgb(244, 246, 248);
    private static readonly Color TextColor = Color.FromArgb(28, 36, 42);
    private static readonly Color MutedColor = Color.FromArgb(105, 115, 124);
    private static readonly Color AccentColor = Color.FromArgb(25, 118, 150);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly string _deviceKey;
    private readonly MouseHookCallback _mouseHookCallback;
    private readonly MessageOverlayForm _messageOverlay;
    private IntPtr _mouseHook = IntPtr.Zero;

    private TextBox _serverUrl = null!;
    private TextBox _deviceKeyText = null!;
    private Label _connectionStatus = null!;
    private Label _captureStatus = null!;
    private PictureBox _preview = null!;
    private ListBox _messages = null!;
    private TextBox _messageText = null!;
    private Button _captureFullButton = null!;
    private Button _captureRegionButton = null!;
    private Button _refreshButton = null!;
    private System.Windows.Forms.Timer _pollTimer;
    private System.Windows.Forms.Timer _textEntryFocusTimer;
    private long _lastMessageId;
    private bool _busy;
    private bool _polling;
    private bool _backgroundMode;
    private bool _connected;
    private bool _hotKeysRegistered;
    private bool _captureHotKeyRegistered;
    private bool _resourcesDisposed;
    private int _overlayMessageIndex = -1;
    private int _wheelDeltaRemainder;
    private string? _registeredServerUrl;

    public ZenConnectService()
    {
        _deviceKey = LoadOrCreateDeviceKey();
        _mouseHookCallback = HandleMouseHook;
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
        _textEntryFocusTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _textEntryFocusTimer.Tick += delegate { RefreshCaptureHotKeyForFocusedControl(); };
    }

    public string DeviceKey => _deviceKey;
    public string CaptureStatus => _captureStatus.Text;
    public bool IsConnected => _connected;
    public string ServerUrl
    {
        get => _serverUrl.Text;
        set
        {
            _serverUrl.Text = (value ?? string.Empty).Trim();
            _registeredServerUrl = null;
        }
    }

    public async Task ConnectAsync()
    {
        if (_connected)
        {
            await PollAsync(true);
            return;
        }

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
        await PollAsync(true);
    }

    public void Disconnect()
    {
        _pollTimer.Stop();
        StopInputHooks();
        _messageOverlay.Hide();
        _registeredServerUrl = null;
        _connectionStatus.Text = "Chua ket noi";
        _connectionStatus.ForeColor = MutedColor;
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        StopInputHooks();
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message message)
    {
        if (_connected && message.Msg == WmHotKey)
        {
            int hotKeyId = message.WParam.ToInt32();
            if (hotKeyId == CaptureHotKeyId)
                BeginInvoke(new Action(async () => await CaptureFullScreenAsync()));
            else if (hotKeyId == PreviousMessageHotKeyId)
                BeginInvoke(new Action(() => CycleOverlayMessage(-1)));
            else if (hotKeyId == NextMessageHotKeyId)
                BeginInvoke(new Action(() => CycleOverlayMessage(1)));
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _pollTimer?.Stop();
            StopInputHooks();
            _textEntryFocusTimer?.Dispose();
            _messageOverlay?.Dispose();
            _http.Dispose();
            _registrationGate.Dispose();
        }

        base.Dispose(disposing);
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
        _serverUrl = MakeTextBox("http://localhost:5080", new Point(16, 32), new Size(280, 27));
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
        _refreshButton.Click += async delegate { await ConnectAsync(); };
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
        Bitmap? bitmap = null;
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

    public async Task CaptureRegionAsync()
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
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildApiUrl("screenshots"));
            using var form = new MultipartFormDataContent();
            using var image = new ByteArrayContent(stream.ToArray());
            image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(image, "image", "capture.png");
            request.Content = form;
            AddAccessKey(request);

            using HttpResponseMessage response = await _http.SendAsync(request);
            string payload = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                if (statusCode == 405)
                {
                    throw new InvalidOperationException(
                        "Server local đang dùng bản cũ (HTTP 405). Hãy đóng ZenConnect và chạy lại run.bat.");
                }

                string detail = ReadServerError(payload);
                throw new InvalidOperationException(
                    "Server từ chối tải ảnh (HTTP " + statusCode + ")"
                    + (string.IsNullOrEmpty(detail) ? "." : ": " + detail));
            }
            var uploaded = JsonSerializer.Deserialize<ScreenshotResponse>(payload, _jsonOptions);
            _captureStatus.Text = uploaded == null
                ? "Da tai anh len."
                : "Da tai anh so " + uploaded.Sequence + ".";
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
        if (_busy || _polling || !_connected || IsDisposed)
            return;

        if (!HasConnectionSettings())
            return;

        _polling = true;
        try
        {
            await EnsureRoomRegisteredAsync();
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildApiUrl("messages?after=" + _lastMessageId));
            AddAccessKey(request);
            using HttpResponseMessage response = await _http.SendAsync(request);
            string payload = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            var items = JsonSerializer.Deserialize<List<MessageResponse>>(payload, _jsonOptions) ?? new List<MessageResponse>();
            foreach (MessageResponse item in items.OrderBy(x => x.Id))
            {
                if (item.Id <= _lastMessageId)
                    continue;

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
        catch (Exception ex)
        {
            _registeredServerUrl = null;
            _connectionStatus.Text = "Mat ket noi: " + FriendlyError(ex);
            _connectionStatus.ForeColor = Color.FromArgb(180, 55, 45);
        }
        finally
        {
            _polling = false;
        }
    }

    private bool CanOperate()
    {
        if (_busy)
            return false;

        if (!_connected)
        {
            MessageBox.Show("Hay ket noi ZenConnect truoc khi chup anh.", "ZenConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!HasConnectionSettings())
        {
            MessageBox.Show("Hay nhap server URL.", "ZenConnect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        return true;
    }

    private bool HasConnectionSettings()
    {
        return Uri.TryCreate(_serverUrl.Text.Trim(), UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private string BuildApiUrl(string resource)
    {
        string server = _serverUrl.Text.Trim().TrimEnd('/');
        return server + "/api/" + resource;
    }

    private void AddAccessKey(HttpRequestMessage request)
    {
        request.Headers.Add("X-Screen-Review-Key", _deviceKey);
    }

    private static string ReadServerError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private async Task EnsureRoomRegisteredAsync()
    {
        string server = _serverUrl.Text.Trim().TrimEnd('/');
        if (string.Equals(server, _registeredServerUrl, StringComparison.OrdinalIgnoreCase))
            return;

        if (!HasConnectionSettings())
            throw new InvalidOperationException("Server URL khong hop le.");

        await _registrationGate.WaitAsync();
        try
        {
            if (string.Equals(server, _registeredServerUrl, StringComparison.OrdinalIgnoreCase))
                return;

            using var request = new HttpRequestMessage(HttpMethod.Post, server + "/api/rooms/register");
            AddAccessKey(request);
            using HttpResponseMessage response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            _registeredServerUrl = server;
        }
        finally
        {
            _registrationGate.Release();
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

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        _captureFullButton.Enabled = !busy;
        _captureRegionButton.Enabled = !busy;
        if (!string.IsNullOrEmpty(status))
            _captureStatus.Text = status;
    }

    private void SetPreview(Bitmap bitmap)
    {
        Image? old = _preview.Image;
        _preview.Image = new Bitmap(bitmap);
        old?.Dispose();
    }

    private void ShowSelectedMessage()
    {
        var selected = _messages.SelectedItem as MessageResponse;
        _messageText.Text = selected == null ? string.Empty : selected.Text;
    }

    private void StartInputHooks()
    {
        if (_hotKeysRegistered)
            return;

        uint captureModifiers = ModNoRepeat;
        uint navigationModifiers = ModControl | ModShift | ModNoRepeat;
        bool captureRegistered = RegisterHotKey(Handle, CaptureHotKeyId, captureModifiers, (uint)Keys.X);
        bool previousRegistered = RegisterHotKey(Handle, PreviousMessageHotKeyId, navigationModifiers, (uint)Keys.Up);
        bool nextRegistered = RegisterHotKey(Handle, NextMessageHotKeyId, navigationModifiers, (uint)Keys.Down);

        if (!captureRegistered || !previousRegistered || !nextRegistered)
        {
            UnregisterHotKey(Handle, CaptureHotKeyId);
            UnregisterHotKey(Handle, PreviousMessageHotKeyId);
            UnregisterHotKey(Handle, NextMessageHotKeyId);
            throw new InvalidOperationException("Khong the dang ky phim tat X hoac Ctrl + Shift + Up/Down.");
        }

        _mouseHook = SetWindowsHookEx(WhMouseLowLevel, _mouseHookCallback, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
        {
            UnregisterHotKey(Handle, CaptureHotKeyId);
            UnregisterHotKey(Handle, PreviousMessageHotKeyId);
            UnregisterHotKey(Handle, NextMessageHotKeyId);
            throw new InvalidOperationException("Khong the theo doi chuot phai tren may nay.");
        }

        _captureHotKeyRegistered = true;
        _hotKeysRegistered = true;
        _textEntryFocusTimer.Start();
        RefreshCaptureHotKeyForFocusedControl();
    }

    private void RefreshCaptureHotKeyForFocusedControl()
    {
        if (!_hotKeysRegistered || !IsHandleCreated)
            return;

        bool textEntryFocused = IsEditableTextControlFocused();
        if (textEntryFocused && _captureHotKeyRegistered)
        {
            UnregisterHotKey(Handle, CaptureHotKeyId);
            _captureHotKeyRegistered = false;
        }
        else if (!textEntryFocused && !_captureHotKeyRegistered)
        {
            _captureHotKeyRegistered = RegisterHotKey(
                Handle, CaptureHotKeyId, ModNoRepeat, (uint)Keys.X);
        }
    }

    private static bool IsEditableTextControlFocused()
    {
        if (IsWin32TextControlFocused())
            return true;

        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            if (focused == null)
                return false;

            ControlType type = focused.Current.ControlType;
            if (type != ControlType.Edit && type != ControlType.ComboBox)
                return false;

            if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern)
                && pattern is ValuePattern valuePattern)
                return !valuePattern.Current.IsReadOnly;

            // Some browser and custom controls expose the edit control type but
            // not ValuePattern. Treat those as editable so X remains available.
            return focused.Current.IsKeyboardFocusable;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsWin32TextControlFocused()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        uint threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (threadId == 0 || !GetGUIThreadInfo(threadId, ref info) || info.Focus == IntPtr.Zero)
            return false;

        var className = new StringBuilder(128);
        if (GetClassName(info.Focus, className, className.Capacity) == 0)
            return false;

        string value = className.ToString();
        return value.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Scintilla", StringComparison.OrdinalIgnoreCase);
    }

    private void StopInputHooks()
    {
        _connected = false;
        _textEntryFocusTimer.Stop();
        if (_hotKeysRegistered && IsHandleCreated)
        {
            if (_captureHotKeyRegistered)
                UnregisterHotKey(Handle, CaptureHotKeyId);
            UnregisterHotKey(Handle, PreviousMessageHotKeyId);
            UnregisterHotKey(Handle, NextMessageHotKeyId);
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _hotKeysRegistered = false;
        _captureHotKeyRegistered = false;
    }

    private IntPtr HandleMouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0
            && _connected
            && !IsDisposed)
        {
            int mouseMessage = message.ToInt32();
            if (mouseMessage == WmRightButtonDown)
            {
                QueueOnUiThread(ToggleMessageOverlay);
            }
            else if (mouseMessage == WmMouseWheel && _messageOverlay.Visible && data != IntPtr.Zero)
            {
                MouseLowLevelHookData hookData = Marshal.PtrToStructure<MouseLowLevelHookData>(data);
                short delta = unchecked((short)(hookData.MouseData >> 16));
                _wheelDeltaRemainder += delta;
                int steps = _wheelDeltaRemainder / WheelDelta;
                _wheelDeltaRemainder %= WheelDelta;
                if (steps != 0)
                    QueueOnUiThread(() => CycleOverlayMessage(-steps));
            }
        }

        return CallNextHookEx(_mouseHook, code, message, data);
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
            _wheelDeltaRemainder = 0;
            _messageOverlay.Hide();
            return;
        }

        _wheelDeltaRemainder = 0;
        _overlayMessageIndex = _messages.Items.Count - 1;
        ShowCurrentOverlayMessage();
    }

    private void CycleOverlayMessage(int offset)
    {
        int count = _messages.Items.Count;
        if (!_messageOverlay.Visible || count == 0 || offset == 0)
            return;

        _overlayMessageIndex = ((_overlayMessageIndex + offset) % count + count) % count;
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
                message.Sequence.HasValue ? "Ảnh " + message.Sequence.Value : "Đáp án",
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
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr windowHandle, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookCallback callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private delegate IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Active;
        public IntPtr Focus;
        public IntPtr Capture;
        public IntPtr MenuOwner;
        public IntPtr MoveSize;
        public IntPtr Caret;
        public NativeRect CaretRectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHookData
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private sealed class ScreenshotResponse
    {
        public int Sequence { get; set; }
    }

    private sealed class MessageResponse
    {
        public long Id { get; set; }
        public int? Sequence { get; set; }
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }

        public override string ToString()
        {
            string scope = Sequence.HasValue ? "Anh " + Sequence.Value : "Đáp án chung";
            return scope + "  |  " + CreatedAt.ToLocalTime().ToString("HH:mm:ss");
        }
    }
}

internal sealed class MessageOverlayForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _textLabel;
    private readonly Label _countLabel;

    public MessageOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(224, 229, 232);
        Opacity = 0.68;
        Size = new Size(300, 32);
        DoubleBuffered = true;

        Controls.Add(new Panel
        {
            BackColor = Color.FromArgb(43, 169, 199),
            Dock = DockStyle.Left,
            Width = 2
        });

        _titleLabel = new Label
        {
            ForeColor = Color.FromArgb(82, 96, 105),
            Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
            Location = new Point(10, 0),
            Size = new Size(52, 32),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _countLabel = new Label
        {
            ForeColor = Color.FromArgb(18, 132, 160),
            Font = new Font("Consolas", 7.5f, FontStyle.Bold),
            Location = new Point(258, 0),
            Size = new Size(34, 32),
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };

        _textLabel = new Label
        {
            ForeColor = Color.FromArgb(28, 36, 42),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            Location = new Point(68, 0),
            Size = new Size(182, 32),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false
        };

        Controls.Add(_titleLabel);
        Controls.Add(_countLabel);
        Controls.Add(_textLabel);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(135, 151, 159));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    public void SetMessage(string title, string text, string count)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => SetMessage(title, text, count)));
            return;
        }

        _titleLabel.Text = title;
        _textLabel.Text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        _countLabel.Text = count;
        _countLabel.Width = Math.Max(42, _countLabel.PreferredWidth);
        _countLabel.Left = Width - _countLabel.Width - 8;
        _textLabel.Width = Math.Max(70, _countLabel.Left - _textLabel.Left - 7);
    }

    public void ShowNear(Point location)
    {
        if (InvokeRequired)
        {
            Invoke(new Action(() => ShowNear(location)));
            return;
        }

        if (!Visible) Show();

        int x = location.X + 14;
        int y = location.Y + 14;

        Rectangle screen = Screen.FromPoint(location).WorkingArea;
        if (x + Width > screen.Right) x = location.X - Width - 14;
        if (y + Height > screen.Bottom) y = location.Y - Height - 14;
        x = Math.Max(screen.Left, x);
        y = Math.Max(screen.Top, y);

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

        using var brush = new SolidBrush(Color.FromArgb(70, Color.White));
        using var pen = new Pen(Color.White, 2f);
        e.Graphics.FillRectangle(brush, _selection);
        e.Graphics.DrawRectangle(pen, _selection);
    }
}
