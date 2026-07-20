using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenConnect;

namespace ZenConnectClient;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(24, 24, 27);
    private static readonly Color CardColor = Color.FromArgb(39, 39, 42);
    private static readonly Color TextColor = Color.FromArgb(250, 250, 250);
    private static readonly Color MutedColor = Color.FromArgb(161, 161, 170);
    private static readonly Color PrimaryColor = Color.FromArgb(59, 130, 246); // Blue
    private static readonly Color PrimaryHover = Color.FromArgb(37, 99, 235);
    private static readonly Color SuccessColor = Color.FromArgb(34, 197, 94); // Green
    private static readonly Color ErrorColor = Color.FromArgb(239, 68, 68); // Red
    
    private readonly ZenConnectService _service = new();
    private readonly TextBox _roomKey;
    private readonly Label _status;
    private readonly Button _actionButton; 

    public MainForm()
    {
        Text = "ZenConnect";
        ClientSize = new Size(420, 310);
        MinimumSize = new Size(420, 310);
        MaximumSize = new Size(420, 310);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        Font = new Font("Segoe UI", 10f);
        
        var titleLabel = new Label
        {
            Text = "ZenConnect",
            Font = new Font("Segoe UI Semibold", 18f, FontStyle.Bold),
            ForeColor = TextColor,
            AutoSize = true,
            Location = new Point(0, 25),
            Size = new Size(420, 40),
            TextAlign = ContentAlignment.MiddleCenter
        };
        // Center title horizontally
        titleLabel.Location = new Point((ClientSize.Width - titleLabel.PreferredWidth) / 2, 25);
        Controls.Add(titleLabel);

        var keyLabel = new Label
        {
            Text = "ROOM KEY",
            Location = new Point(30, 95),
            AutoSize = true,
            ForeColor = MutedColor,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold)
        };
        Controls.Add(keyLabel);
        
        _roomKey = new TextBox
        {
            Text = _service.DeviceKey,
            ReadOnly = true,
            BackColor = CardColor,
            ForeColor = TextColor,
            Location = new Point(30, 118),
            Size = new Size(250, 32),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 14f, FontStyle.Bold)
        };
        Controls.Add(_roomKey);

        var copyButton = MakeButton("COPY", 290, 118, 100, 32, CardColor, TextColor);
        copyButton.Click += delegate
        {
            try
            {
                Clipboard.SetText(_service.DeviceKey);
                SetStatus("Copied to clipboard!", false);
            }
            catch { }
        };
        Controls.Add(copyButton);

        _actionButton = MakeButton("START", 30, 175, 360, 48, PrimaryColor, Color.White);
        _actionButton.Font = new Font("Segoe UI", 13f, FontStyle.Bold);
        _actionButton.Click += async delegate { 
            if (_actionButton.Text == "START") 
                await StartAppAsync(); 
            else 
                await CaptureAsync(); 
        };
        Controls.Add(_actionButton);

        _status = new Label
        {
            Text = "Ready to connect",
            ForeColor = MutedColor,
            AutoEllipsis = true,
            Location = new Point(30, 235),
            Size = new Size(360, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(_status);

        FormClosing += delegate { _service.Dispose(); };
    }

    private async Task StartAppAsync()
    {
        _actionButton.Enabled = false;
        _actionButton.Text = "CONNECTING...";
        SetStatus("Connecting to server...", false);
        
        try
        {
            _service.ServerUrl = LoadServerUrl();
            await _service.ConnectAsync();
            
            _actionButton.Text = "📸 CAPTURE (X)";
            _actionButton.BackColor = SuccessColor;
            SetStatus("Connected. Press button or X to capture.", false, true);
        }
        catch (Exception ex)
        {
            _actionButton.Text = "START";
            _actionButton.BackColor = PrimaryColor;
            SetStatus("Error: " + ex.Message, true);
        }
        finally
        {
            _actionButton.Enabled = true;
        }
    }

    private async Task CaptureAsync()
    {
        _actionButton.Enabled = false;
        _actionButton.Text = "⏳ PROCESSING...";
        SetStatus("Capturing screen...", false);
        
        await _service.CaptureFullScreenAsync();
        
        bool hasError = _service.CaptureStatus.Contains("that bai", StringComparison.OrdinalIgnoreCase) || 
                        _service.CaptureStatus.Contains("thất bại", StringComparison.OrdinalIgnoreCase);
        
        SetStatus(_service.CaptureStatus, hasError, !hasError);
        
        _actionButton.Text = "📸 CAPTURE (X)";
        _actionButton.Enabled = true;
    }

    private void SetStatus(string text, bool error, bool success = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? ErrorColor : success ? SuccessColor : MutedColor;
    }

    private static string LoadServerUrl()
    {
        string configured = Environment.GetEnvironmentVariable("ZENCONNECT_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenConnect", "server-url.txt");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "http://localhost:5080";
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
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
