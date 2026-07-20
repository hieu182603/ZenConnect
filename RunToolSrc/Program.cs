using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Threading;

// ─── Entry Point ────────────────────────────────────────────────────
static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

// ─── Custom Rounded Panel ───────────────────────────────────────────
public class RoundPanel : Panel
{
    private int _radius = 12;
    private Color _fillColor = Color.FromArgb(38, 38, 48);

    public int Radius { get { return _radius; } set { _radius = value; Invalidate(); } }
    public Color FillColor { get { return _fillColor; } set { _fillColor = value; Invalidate(); } }

    public RoundPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = MakeRoundRect(new Rectangle(0, 0, Width - 1, Height - 1), _radius))
        using (var brush = new SolidBrush(_fillColor))
        {
            e.Graphics.FillPath(brush, path);
        }
    }

    public static GraphicsPath MakeRoundRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

// ─── Custom Flat Button with rounded corners ────────────────────────
public class FlatButton : Control
{
    private Color _baseColor = Color.FromArgb(46, 125, 50);
    private Color _hoverColor = Color.FromArgb(56, 142, 60);
    private Color _disabledColor = Color.FromArgb(50, 50, 60);
    private int _radius = 8;
    private bool _hover;
    private bool _pressed;

    public Color BaseColor { get { return _baseColor; } set { _baseColor = value; Invalidate(); } }
    public Color HoverColor { get { return _hoverColor; } set { _hoverColor = value; Invalidate(); } }
    public Color DisabledColor { get { return _disabledColor; } set { _disabledColor = value; Invalidate(); } }
    public int Radius { get { return _radius; } set { _radius = value; Invalidate(); } }

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        ForeColor = Color.White;
        Size = new Size(140, 44);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg;
        if (!Enabled)
            bg = _disabledColor;
        else if (_pressed)
            bg = DarkenColor(_baseColor, 30);
        else if (_hover)
            bg = _hoverColor;
        else
            bg = _baseColor;

        using (var path = RoundPanel.MakeRoundRect(new Rectangle(0, 0, Width - 1, Height - 1), _radius))
        using (var brush = new SolidBrush(bg))
        {
            e.Graphics.FillPath(brush, path);
        }

        var sf = new StringFormat();
        sf.Alignment = StringAlignment.Center;
        sf.LineAlignment = StringAlignment.Center;
        Color tc = Enabled ? ForeColor : Color.FromArgb(100, 100, 115);
        using (var tb = new SolidBrush(tc))
        {
            e.Graphics.DrawString(Text, Font, tb, ClientRectangle, sf);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    private static Color DarkenColor(Color c, int amt)
    {
        return Color.FromArgb(c.A, Math.Max(0, c.R - amt), Math.Max(0, c.G - amt), Math.Max(0, c.B - amt));
    }
}

// ─── Animated Status Dot ────────────────────────────────────────────
public class StatusDot : Control
{
    private Color _dotColor = Color.FromArgb(220, 50, 50);
    private float _pulse = 1f;
    private float _dir = 0.05f;
    private System.Windows.Forms.Timer _anim;

    public Color DotColor { get { return _dotColor; } set { _dotColor = value; Invalidate(); } }

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(20, 20);

        _anim = new System.Windows.Forms.Timer();
        _anim.Interval = 40;
        _anim.Tick += delegate { _pulse += _dir; if (_pulse >= 1f || _pulse <= 0.3f) _dir = -_dir; Invalidate(); };
        _anim.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int sz = Math.Min(Width, Height) - 6;
        int x = (Width - sz) / 2;
        int y = (Height - sz) / 2;

        // Glow ring
        int ga = (int)(50 * _pulse);
        using (var glow = new SolidBrush(Color.FromArgb(ga, _dotColor)))
        {
            e.Graphics.FillEllipse(glow, x - 4, y - 4, sz + 8, sz + 8);
        }
        using (var fill = new SolidBrush(_dotColor))
        {
            e.Graphics.FillEllipse(fill, x, y, sz, sz);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _anim != null) { _anim.Stop(); _anim.Dispose(); }
        base.Dispose(disposing);
    }
}

// ─── Main Form ──────────────────────────────────────────────────────
public class MainForm : Form
{
    // Theme colors (Light)
    static readonly Color BgColor       = Color.FromArgb(245, 245, 248);
    static readonly Color PanelColor    = Color.FromArgb(255, 255, 255);
    static readonly Color HeaderColor   = Color.FromArgb(255, 255, 255);
    static readonly Color TextColor     = Color.FromArgb(30, 30, 40);
    static readonly Color SubTextColor  = Color.FromArgb(110, 110, 130);
    static readonly Color GreenColor    = Color.FromArgb(46, 160, 70);
    static readonly Color RedColor      = Color.FromArgb(210, 45, 45);
    static readonly Color OrangeColor   = Color.FromArgb(230, 115, 0);
    static readonly Color SepColor      = Color.FromArgb(220, 220, 228);

    static readonly Color StatusRunBg   = Color.FromArgb(230, 248, 235);
    static readonly Color StatusStopBg  = Color.FromArgb(255, 235, 235);

    // Controls
    private Label _statusLabel;
    private Label _detailLabel;
    private RoundPanel _statusCard;
    private StatusDot _dot;
    private FlatButton _btnStart;
    private FlatButton _btnStop;
    private FlatButton _btnRestart;
    private FlatButton _btnScreenReview;
    private Label _resultLabel;
    private Label _zenStatusLabel;
    private ScreenReviewForm _screenReviewService;

    private System.Windows.Forms.Timer _refreshTimer;
    private string _appDir;

    public MainForm()
    {
        _appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatMonitorTool");
        ExtractPayload();

        // Form setup
        Text = "WinStatMonitor Controller";
        ClientSize = new Size(480, 330);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = BgColor;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9);

        BuildUI();

        _refreshTimer = new System.Windows.Forms.Timer();
        _refreshTimer.Interval = 1500;
        _refreshTimer.Tick += delegate { RefreshUI(); };
        _refreshTimer.Start();

        Load += delegate { RefreshUI(); };
        FormClosing += delegate
        {
            _refreshTimer.Stop();
            if (_screenReviewService != null && !_screenReviewService.IsDisposed)
                _screenReviewService.Close();
        };
    }

    private void BuildUI()
    {
        // ── Header ──
        var header = new Panel();
        header.Dock = DockStyle.Top;
        header.Height = 44;
        header.BackColor = HeaderColor;
        Controls.Add(header);

        var logo = new Label();
        logo.Text = "WinStatMonitor";
        logo.Font = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        logo.ForeColor = TextColor;
        logo.AutoSize = true;
        logo.Location = new Point(14, 11);
        header.Controls.Add(logo);

        var badge = new Label();
        badge.Text = " ADMIN ";
        badge.Font = new Font("Segoe UI", 7f, FontStyle.Bold);
        badge.ForeColor = Color.White;
        badge.BackColor = GreenColor;
        badge.AutoSize = true;
        badge.Location = new Point(418, 14);
        header.Controls.Add(badge);

        // ── Separator ──
        var sep = new Panel();
        sep.Location = new Point(0, 44);
        sep.Size = new Size(480, 1);
        sep.BackColor = SepColor;
        Controls.Add(sep);

        // ── Status Card ──
        _statusCard = new RoundPanel();
        _statusCard.Location = new Point(14, 56);
        _statusCard.Size = new Size(452, 88);
        _statusCard.FillColor = PanelColor;
        _statusCard.Radius = 10;
        Controls.Add(_statusCard);

        _dot = new StatusDot();
        _dot.Location = new Point(14, 14);
        _dot.Size = new Size(16, 16);
        _dot.DotColor = RedColor;
        _statusCard.Controls.Add(_dot);

        _statusLabel = new Label();
        _statusLabel.Text = "DETECTING...";
        _statusLabel.Font = new Font("Segoe UI", 12f, FontStyle.Bold);
        _statusLabel.ForeColor = TextColor;
        _statusLabel.AutoSize = true;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.Location = new Point(34, 10);
        _statusCard.Controls.Add(_statusLabel);

        _detailLabel = new Label();
        _detailLabel.Text = "";
        _detailLabel.Font = new Font("Segoe UI", 8f);
        _detailLabel.ForeColor = SubTextColor;
        _detailLabel.AutoSize = true;
        _detailLabel.BackColor = Color.Transparent;
        _detailLabel.Location = new Point(36, 38);
        _statusCard.Controls.Add(_detailLabel);

        _resultLabel = new Label();
        _resultLabel.Text = "";
        _resultLabel.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _resultLabel.ForeColor = SubTextColor;
        _resultLabel.AutoSize = true;
        _resultLabel.BackColor = Color.Transparent;
        _resultLabel.Location = new Point(36, 60);
        _statusCard.Controls.Add(_resultLabel);

        // ── Buttons ──
        int btnY = 158;
        int btnH = 36;

        _btnStart = new FlatButton();
        _btnStart.Text = "START";
        _btnStart.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnStart.BaseColor = GreenColor;
        _btnStart.HoverColor = Color.FromArgb(56, 200, 95);
        _btnStart.Radius = 6;
        _btnStart.Location = new Point(14, btnY);
        _btnStart.Size = new Size(146, btnH);
        _btnStart.Click += delegate { DoStart(); };
        Controls.Add(_btnStart);

        _btnStop = new FlatButton();
        _btnStop.Text = "STOP";
        _btnStop.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnStop.BaseColor = RedColor;
        _btnStop.HoverColor = Color.FromArgb(245, 65, 65);
        _btnStop.Radius = 6;
        _btnStop.Location = new Point(168, btnY);
        _btnStop.Size = new Size(146, btnH);
        _btnStop.Click += delegate { DoStop(); };
        Controls.Add(_btnStop);

        _btnRestart = new FlatButton();
        _btnRestart.Text = "RESTART";
        _btnRestart.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnRestart.BaseColor = OrangeColor;
        _btnRestart.HoverColor = Color.FromArgb(255, 150, 25);
        _btnRestart.Radius = 6;
        _btnRestart.Location = new Point(322, btnY);
        _btnRestart.Size = new Size(144, btnH);
        _btnRestart.Click += delegate { DoStop(); Thread.Sleep(400); DoStart(); };
        Controls.Add(_btnRestart);

        _btnScreenReview = new FlatButton();
        _btnScreenReview.Text = "ZENCONNECT";
        _btnScreenReview.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _btnScreenReview.BaseColor = Color.FromArgb(35, 105, 145);
        _btnScreenReview.HoverColor = Color.FromArgb(45, 135, 180);
        _btnScreenReview.Radius = 6;
        _btnScreenReview.Location = new Point(14, 204);
        _btnScreenReview.Size = new Size(452, btnH);
        _btnScreenReview.Click += async delegate { await OpenScreenReviewAsync(); };
        Controls.Add(_btnScreenReview);

        _zenStatusLabel = new Label();
        _zenStatusLabel.Text = "ZenConnect chua ket noi";
        _zenStatusLabel.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
        _zenStatusLabel.ForeColor = SubTextColor;
        _zenStatusLabel.AutoEllipsis = true;
        _zenStatusLabel.Location = new Point(14, 250);
        _zenStatusLabel.Size = new Size(452, 22);
        Controls.Add(_zenStatusLabel);

        // ── Footer ──
        var footer = new Label();
        footer.Text = "Admin  |  X\u00f3a delete_to_quit.file \u0111\u1ec3 d\u1eebng tool";
        footer.Font = new Font("Segoe UI", 7f, FontStyle.Italic);
        footer.ForeColor = Color.FromArgb(160, 160, 175);
        footer.AutoSize = true;
        footer.Location = new Point(14, 298);
        Controls.Add(footer);
    }

    private async System.Threading.Tasks.Task OpenScreenReviewAsync()
    {
        _btnScreenReview.Enabled = false;
        _zenStatusLabel.Text = "ZenConnect dang ket noi...";
        _zenStatusLabel.ForeColor = SubTextColor;

        try
        {
            if (_screenReviewService == null || _screenReviewService.IsDisposed)
                _screenReviewService = new ScreenReviewForm();

            await _screenReviewService.ConnectInBackgroundAsync();
            string key = _screenReviewService.DeviceKey;
            try { Clipboard.SetText(key); } catch { }

            _btnScreenReview.Text = "ZENCONNECT - DA KET NOI";
            _zenStatusLabel.Text = "Da ket noi  |  Key: " + key + "  |  Da sao chep";
            _zenStatusLabel.ForeColor = Color.FromArgb(30, 140, 50);
        }
        catch (Exception ex)
        {
            _btnScreenReview.Text = "ZENCONNECT";
            _zenStatusLabel.Text = "Ket noi that bai: " + ex.Message;
            _zenStatusLabel.ForeColor = RedColor;
        }
        finally
        {
            _btnScreenReview.Enabled = true;
        }
    }

    // ── Process helpers ──
    private Process GetTarget()
    {
        return null;
    }

    private void RefreshUI()
    {
        var proc = GetTarget();
        bool running = proc != null;

        if (running)
        {
            _dot.DotColor = GreenColor;
            _statusLabel.Text = "\u0110ANG CH\u1EA0Y";
            string st = "N/A";
            try { st = proc.StartTime.ToString("HH:mm:ss  dd/MM/yyyy"); } catch { }
            _detailLabel.Text = "PID: " + proc.Id + "  \u2022  " + st;
            _statusCard.FillColor = StatusRunBg;

            _btnStart.Enabled = false;
            _btnStop.Enabled = true;
            _btnRestart.Enabled = true;
        }
        else
        {
            _dot.DotColor = RedColor;
            _statusLabel.Text = "\u0110\u00c3 D\u1eeeNG";
            _detailLabel.Text = "Nh\u1ea5n START \u0111\u1ec3 ch\u1ea1y tool.";
            _statusCard.FillColor = StatusStopBg;

            _btnStart.Enabled = true;
            _btnStop.Enabled = false;
            _btnRestart.Enabled = false;
        }
        _statusCard.Invalidate();

        // Read text.txt for inject result
        string logPath = Path.Combine(_appDir, "text.txt");
        if (running && File.Exists(logPath))
        {
            try
            {
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    string allText = sr.ReadToEnd();
                    string[] lines = allText.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    // Find the last inject result line
                    string resultLine = null;
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string trimmed = lines[i].Trim();
                        if (trimmed.Contains("Inject result"))
                        {
                            resultLine = trimmed;
                            break;
                        }
                    }

                    if (resultLine != null)
                    {
                        // Extract process name from "WMI: Process 123 (Name.exe): Inject result = XX"
                        string procName = "";
                        int p1 = resultLine.IndexOf('(');
                        int p2 = resultLine.IndexOf(')');
                        if (p1 >= 0 && p2 > p1)
                            procName = resultLine.Substring(p1 + 1, p2 - p1 - 1);

                        _resultLabel.Text = "\u2714  Inject th\u00e0nh c\u00f4ng" + (procName.Length > 0 ? " \u2192 " + procName : "");
                        _resultLabel.ForeColor = Color.FromArgb(30, 140, 50);
                    }
                    else if (allText.Contains("All checks passed"))
                    {
                        _resultLabel.Text = "\u2714  Ki\u1ec3m tra xong, \u0111ang ch\u1edd inject...";
                        _resultLabel.ForeColor = Color.FromArgb(180, 140, 0);
                    }
                    else
                    {
                        _resultLabel.Text = "\u23F3  \u0110ang ch\u1edd k\u1ebft qu\u1ea3...";
                        _resultLabel.ForeColor = SubTextColor;
                    }
                }
            }
            catch
            {
                _resultLabel.Text = "";
            }
        }
        else if (!running)
        {
            _resultLabel.Text = "";
        }
    }

    private void DoStart()
    {
        if (Application.OpenForms.OfType<ScreenReviewForm>().Any())
        {
            MessageBox.Show(
                "Hay dong ZenConnect truoc khi chay WinStatMonitor.",
                "WinStatMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        string exe = Path.Combine(_appDir, "WinStatMonitor.exe");
        if (!File.Exists(exe))
        {
            MessageBox.Show("Cannot find WinStatMonitor.exe in:\n" + _appDir, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string qf = Path.Combine(_appDir, "delete_to_quit.file");
        File.WriteAllText(qf, "abc");

        var psi = new ProcessStartInfo();
        psi.FileName = exe;
        psi.WorkingDirectory = _appDir;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.CreateNoWindow = true;
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to launch:\n" + ex.Message, "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Thread.Sleep(500);
        RefreshUI();
    }

    private void DoStop()
    {
        string qf = Path.Combine(_appDir, "delete_to_quit.file");
        try { if (File.Exists(qf)) File.Delete(qf); } catch { }

        int waited = 0;
        while (waited < 3000)
        {
            if (GetTarget() == null) break;
            Thread.Sleep(300);
            waited += 300;
        }

        var p = GetTarget();
        if (p != null) try { p.Kill(); } catch { }

        Thread.Sleep(200);
        RefreshUI();
    }

    private void ExtractPayload()
    {
        try
        {
            if (!Directory.Exists(_appDir))
            {
                Directory.CreateDirectory(_appDir);
            }

            string versionPath = Path.Combine(_appDir, "launcher_version.txt");
            string currentVersion = "1.0.0.0";
            
            bool needExtract = true;
            if (File.Exists(versionPath) && File.Exists(Path.Combine(_appDir, "WinStatMonitor.exe")))
            {
                string existingVersion = File.ReadAllText(versionPath);
                if (existingVersion == currentVersion)
                {
                    needExtract = false;
                }
            }

            if (needExtract)
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("RunTool.Resources.payload.zip"))
                {
                    if (stream == null)
                    {
                        MessageBox.Show("Cannot find embedded payload.zip!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    try
                    {
                        var procs = Process.GetProcessesByName("WinStatMonitor");
                        foreach (var p in procs)
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch { }

                    using (var archive = new ZipArchive(stream))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            string destPath = Path.Combine(_appDir, entry.FullName);
                            string destDir = Path.GetDirectoryName(destPath);
                            if (!Directory.Exists(destDir))
                            {
                                Directory.CreateDirectory(destDir);
                            }

                            if (!string.IsNullOrEmpty(entry.Name))
                            {
                                entry.ExtractToFile(destPath, true);
                            }
                        }
                    }
                }
                File.WriteAllText(versionPath, currentVersion);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to extract WinStatMonitor files:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
