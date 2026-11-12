using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Remember;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--replace-install" &&
            int.TryParse(args[2], out int previousPid))
        {
            AppUpdater.ReplaceInstall(args[1], previousPid, args[3]);
            return;
        }

        if (args.Length == 3 && args[0] == "--cleanup-update" &&
            int.TryParse(args[2], out int helperPid))
            _ = Task.Run(() => AppUpdater.CleanupStage(args[1], helperPid));

        using var mutex = new Mutex(true, @"Local\REMEMBERWidget", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("REMEMBER 已经在运行。", "REMEMBER",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        try { Application.Run(new BoardForm()); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "REMEMBER 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal static class AppUpdater
{
    private const string LatestRelease =
        "https://api.github.com/repos/meowworry-blip/REMEMBER/releases/latest";
    private const string AssetName = "REMEMBER-win-x64.zip";

    public static async Task<bool> CheckAndInstallAsync(Form owner)
    {
        string? stage = null;
        bool handedOff = false;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("REMEMBER/1.0");
            using JsonDocument release = JsonDocument.Parse(
                await client.GetStringAsync(LatestRelease));
            JsonElement root = release.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
                throw new InvalidDataException("无法识别最新版本号。");

            Version assembly = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0);
            Version current = new(assembly.Major, assembly.Minor, Math.Max(0, assembly.Build));
            if (latest <= current)
            {
                MessageBox.Show(owner, $"当前已是最新版本 v{current}。", "REMEMBER",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            JsonElement asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(x => x.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("最新版本缺少 Windows 安装包。");

            if (MessageBox.Show(owner,
                $"发现 REMEMBER {tag}。现在下载并安装，然后重启程序？",
                "REMEMBER 更新", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
                return false;

            string url = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("安装包地址无效。");
            string digest = asset.GetProperty("digest").GetString() ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包缺少 SHA-256 校验值。");

            stage = Path.Combine(Path.GetTempPath(), "REMEMBER-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string archive = Path.Combine(stage, AssetName);
            using (HttpResponseMessage response = await client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using Stream input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(archive);
                await input.CopyToAsync(output);
            }

            using var archiveStream = File.OpenRead(archive);
            string actual = Convert.ToHexString(SHA256.HashData(archiveStream));
            if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载的安装包校验失败。");

            string unpacked = Path.Combine(stage, "unpacked");
            ZipFile.ExtractToDirectory(archive, unpacked);
            string stagedExe = Path.Combine(unpacked, "REMEMBER", "REMEMBER.exe");
            if (!File.Exists(stagedExe))
                throw new InvalidDataException("安装包中没有 REMEMBER.exe。");

            var helper = new ProcessStartInfo(stagedExe) { UseShellExecute = false };
            helper.ArgumentList.Add("--replace-install");
            helper.ArgumentList.Add(Application.ExecutablePath);
            helper.ArgumentList.Add(Environment.ProcessId.ToString());
            helper.ArgumentList.Add(stage);
            _ = Process.Start(helper) ?? throw new IOException("无法启动更新程序。");
            handedOff = true;
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "更新失败：" + ex.Message, "REMEMBER",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            // A successful update transfers ownership of this folder to the helper.
            if (stage != null && !handedOff)
                TryDeleteStage(stage);
        }
    }

    public static void ReplaceInstall(string target, int previousPid, string stage)
    {
        try
        {
            try
            {
                using Process previous = Process.GetProcessById(previousPid);
                if (!previous.WaitForExit(30000))
                    throw new IOException("旧版没有退出。请从托盘退出后重试。");
            }
            catch (ArgumentException) { /* The old process has already exited. */ }

            string source = Environment.ProcessPath
                ?? throw new IOException("无法定位更新程序。");
            if (!Path.GetFileName(target).Equals("REMEMBER.exe",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新目标不是 REMEMBER.exe。");
            string incoming = target + ".incoming";
            File.Copy(source, incoming, true);
            File.Move(incoming, target, true);

            var launch = new ProcessStartInfo(target) { UseShellExecute = false };
            launch.ArgumentList.Add("--cleanup-update");
            launch.ArgumentList.Add(stage);
            launch.ArgumentList.Add(Environment.ProcessId.ToString());
            _ = Process.Start(launch) ?? throw new IOException("新版安装完成，但无法自动启动。");
        }
        catch (Exception ex)
        {
            MessageBox.Show("更新安装失败：" + ex.Message, "REMEMBER",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void CleanupStage(string stage, int helperPid)
    {
        try
        {
            using (Process helper = Process.GetProcessById(helperPid))
                helper.WaitForExit(30000);
        }
        catch (ArgumentException) { }
        TryDeleteStage(stage);
    }

    private static void TryDeleteStage(string stage)
    {
        try
        {
            string full = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
            string parent = Path.GetDirectoryName(full) ?? "";
            if (!parent.Equals(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("REMEMBER-update-", StringComparison.Ordinal))
                return;
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
        catch { /* A temporary download can be removed on the next cleanup. */ }
    }
}

internal sealed class BoardItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    public string ImageFile { get; set; } = "";
    public int Row { get; set; }
    public int Column { get; set; }
    public int ColumnSpan { get; set; } = 2;
    public int RowSpan { get; set; } = 2;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

internal enum DockEdge { Top, Bottom, Left, Right }

internal sealed class BoardLayout
{
    public string Display { get; set; } = "";
    public DockEdge Edge { get; set; } = DockEdge.Top;
    public double Position { get; set; } = 0.5;
    public int Rows { get; set; } = 6;
    public int Columns { get; set; } = 13;
}

internal sealed class BoardStore
{
    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "REMEMBER");
    public string ImagesPath => Path.Combine(DirectoryPath, "images");
    private string JsonPath => Path.Combine(DirectoryPath, "board.json");
    private string DisplayPath => Path.Combine(DirectoryPath, "display.txt");
    private string LayoutPath => Path.Combine(DirectoryPath, "layout.json");

    public BoardStore()
    {
        Directory.CreateDirectory(ImagesPath);
        string legacy = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "FeishuBoard");
        if (File.Exists(Path.Combine(legacy, "board.json")) && !File.Exists(JsonPath))
        {
            File.Copy(Path.Combine(legacy, "board.json"), JsonPath);
            string legacyImages = Path.Combine(legacy, "images");
            if (Directory.Exists(legacyImages))
                foreach (string file in Directory.GetFiles(legacyImages))
                {
                    string destination = Path.Combine(ImagesPath, Path.GetFileName(file));
                    if (!File.Exists(destination)) File.Copy(file, destination);
                }
        }
    }

    public List<BoardItem> Load()
    {
        if (!File.Exists(JsonPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BoardItem>>(File.ReadAllText(JsonPath)) ?? [];
        }
        catch (Exception ex)
        {
            throw new IOException($"无法读取已保存的黑板数据：{JsonPath}\n{ex.Message}", ex);
        }
    }

    public void Save(List<BoardItem> items)
    {
        string temporary = JsonPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(items,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, JsonPath, true);
    }

    public string SaveImage(Image image, string id)
    {
        string name = id + ".png";
        image.Save(Path.Combine(ImagesPath, name), System.Drawing.Imaging.ImageFormat.Png);
        return name;
    }

    public string FullImagePath(BoardItem item) => Path.Combine(ImagesPath, item.ImageFile);

    public string? LoadDisplay() => File.Exists(DisplayPath) ? File.ReadAllText(DisplayPath).Trim() : null;

    public void SaveDisplay(string deviceName) => File.WriteAllText(DisplayPath, deviceName);

    public BoardLayout? LoadLayout()
    {
        if (!File.Exists(LayoutPath)) return null;
        try { return JsonSerializer.Deserialize<BoardLayout>(File.ReadAllText(LayoutPath)); }
        catch { return null; }
    }

    public void SaveLayout(BoardLayout layout)
    {
        string temporary = LayoutPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(layout));
        File.Move(temporary, LayoutPath, true);
    }
}

internal static class StartupSettings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "REMEMBER";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, false);
        }
    }
}

internal static class Ui
{
    public static readonly Color Background = Color.FromArgb(18, 24, 32);
    public static readonly Color Surface = Color.FromArgb(30, 40, 49);
    public static readonly Color SurfaceAlt = Color.FromArgb(37, 51, 59);
    public static readonly Color Accent = Color.FromArgb(243, 176, 88);
    public static readonly Color Secondary = Color.FromArgb(119, 210, 199);
    public static readonly Color Text = Color.FromArgb(235, 232, 214);
    public static readonly Color Muted = Color.FromArgb(143, 160, 166);
    public static readonly Color Line = Color.FromArgb(73, 94, 102);

    public static GraphicsPath RoundRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Button Button(string caption, Color color, Color fore, int width)
    {
        var button = new Button
        {
            Text = caption, BackColor = color, ForeColor = fore, Width = width, Height = 36,
            FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Consolas", 10F, FontStyle.Bold),
            TabStop = false
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Ui.Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 87, 91);
        return button;
    }

    public static Button ToolbarButton(string caption, int width, bool primary = false)
    {
        var button = Button(caption, primary ? Accent : Surface,
            primary ? Background : Text, width);
        button.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(255, 199, 120) : SurfaceAlt;
        return button;
    }

    public static ContextMenuStrip Menu() => new()
    {
        BackColor = Surface,
        ForeColor = Text,
        Font = new Font("Microsoft YaHei UI", 9F),
        Renderer = new ToolStripProfessionalRenderer(new RememberMenuColors())
    };

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

    public static void AddDialogChrome(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.None;
        var mark = new RememberMark { Location = new Point(16, 4) };
        mark.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) DragWindow(form);
        };
        form.Controls.Add(mark);
        var close = Button("×", Surface, Accent, 38);
        close.Location = new Point(form.ClientSize.Width - 50, 7);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.Click += (_, _) => form.Close();
        form.Controls.Add(close);
        form.Paint += (_, e) =>
        {
            using var border = new Pen(Line, 2);
            using var accent = new SolidBrush(Accent);
            e.Graphics.DrawRectangle(border, 1, 1, form.ClientSize.Width - 3, form.ClientSize.Height - 3);
            e.Graphics.FillRectangle(accent, 1, 1, 108, 3);
            e.Graphics.DrawLine(border, 12, 48, form.ClientSize.Width - 13, 48);
        };
    }

    private static void DragWindow(Form form)
    {
        ReleaseCapture();
        SendMessage(form.Handle, 0xA1, 2, 0);
    }
}

internal sealed class RememberMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Ui.Surface;
    public override Color ImageMarginGradientBegin => Ui.Surface;
    public override Color ImageMarginGradientMiddle => Ui.Surface;
    public override Color ImageMarginGradientEnd => Ui.Surface;
    public override Color MenuItemSelected => Ui.SurfaceAlt;
    public override Color MenuItemBorder => Ui.Accent;
    public override Color MenuBorder => Ui.Line;
    public override Color SeparatorDark => Ui.Line;
    public override Color SeparatorLight => Ui.Line;
}

internal sealed class RememberMark : Control
{
    private static readonly Font WordFont = new("Consolas", 16F, FontStyle.Bold);
    private static readonly Font MicroFont = new("Consolas", 6F, FontStyle.Bold);

    public RememberMark()
    {
        DoubleBuffered = true;
        Size = new Size(230, 40);
        BackColor = Ui.Background;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;
        using var accent = new SolidBrush(Ui.Accent);
        using var cyan = new SolidBrush(Ui.Secondary);
        e.Graphics.FillRectangle(accent, 2, 13, 7, 7);
        e.Graphics.FillRectangle(cyan, 2, 23, 4, 4);
        for (int i = 0; i < 8; i++)
            TextRenderer.DrawText(e.Graphics, "REMEMBER"[i].ToString(), WordFont,
                new Rectangle(20 + i * 22, 5, 23, 28), Ui.Text,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        e.Graphics.FillRectangle(cyan, 21, 34, 99, 2);
        e.Graphics.FillRectangle(accent, 124, 34, 18, 2);
        TextRenderer.DrawText(e.Graphics, "01", MicroFont,
            new Rectangle(203, 22, 22, 14), Ui.Accent,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

internal sealed class EdgeHandle : Control
{
    private bool hovered;
    public DockEdge Edge { get; set; } = DockEdge.Top;

    public EdgeHandle()
    {
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Size = new Size(160, 22);
        MouseEnter += (_, _) => { hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { hovered = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Ui.Surface);
        using var accent = new SolidBrush(Ui.Accent);
        using var cyan = new SolidBrush(Ui.Secondary);
        using var text = new SolidBrush(Ui.Text);
        using var border = new Pen(Ui.Line);
        using var font = new Font("Consolas", 9F, FontStyle.Bold);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        int size = hovered ? 4 : 2;
        if (Edge is DockEdge.Left or DockEdge.Right)
        {
            int x = Edge == DockEdge.Right ? Width - size : 0;
            e.Graphics.FillRectangle(accent, x, 0, size, Height);
            e.Graphics.FillRectangle(cyan, x, Height / 2 - 13, size, 26);
            GraphicsState state = e.Graphics.Save();
            e.Graphics.TranslateTransform(Width / 2f, Height / 2f);
            e.Graphics.RotateTransform(Edge == DockEdge.Left ? -90 : 90);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            e.Graphics.DrawString("REMEMBER  GRID", font, text,
                new RectangleF(-Height / 2f, -Width / 2f, Height, Width), format);
            e.Graphics.Restore(state);
        }
        else
        {
            int y = Edge == DockEdge.Bottom ? Height - size : 0;
            e.Graphics.FillRectangle(accent, 0, y, Width, size);
            e.Graphics.FillRectangle(cyan, Width / 2 - 13, y, 26, size);
            TextRenderer.DrawText(e.Graphics, "REMEMBER", font,
                new Rectangle(12, 3, 102, Height - 5), Ui.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, "GRID", font,
                new Rectangle(118, 3, 36, Height - 5), Ui.Secondary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

internal sealed class BoardForm : Form
{
    private const int ExpandedWidth = 1008;
    private const int ExpandedHeight = 480;
    private const int CollapsedWidth = 160;
    private const int CollapsedHeight = 22;
    private const int DefaultColumns = 13;
    private const int DefaultRows = 6;
    private static int activeColumns = DefaultColumns;
    private static int activeRows = DefaultRows;
    private const int CellSize = 63;
    private const int Gap = 6;
    private const int StartX = 11;
    private const int StartY = 10;

    private readonly BoardStore store = new();
    private readonly BoardLayout layout;
    private readonly List<BoardItem> items;
    private readonly Dictionary<string, Image> images = [];
    private readonly Panel content = new();
    private readonly GridPanel grid = new();
    private readonly ResizeGhostForm resizeGhost = new();
    private readonly ContextMenuStrip screenMenu = Ui.Menu();
    private readonly Button screenButton;
    private readonly Button retractButton = Ui.ToolbarButton("↑", 40);
    private readonly Button addRow = Ui.ToolbarButton("+", 30);
    private readonly Button removeRow = Ui.ToolbarButton("−", 30);
    private readonly Button addColumn = Ui.ToolbarButton("+", 30);
    private readonly Button removeColumn = Ui.ToolbarButton("−", 30);
    private readonly Label rowLabel = new();
    private readonly Label columnLabel = new();
    private readonly EdgeHandle handle;
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer idleTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer hoverTimer = new() { Interval = 350 };
    private readonly BoardActivityFilter activityFilter;
    private Screen currentScreen;
    private DateTime lastActivity = DateTime.UtcNow;
    private Point cursorAtCollapse;
    private Rectangle expandedBounds;
    private Rectangle collapsedBounds;
    private Rectangle animationFrom;
    private Rectangle animationTo;
    private DateTime animationStart;
    private bool collapsed;
    private bool exiting;
    private bool movingWindow;
    private Point moveOffset;

    public BoardForm()
    {
        items = store.Load();
        layout = store.LoadLayout() ?? new BoardLayout { Display = store.LoadDisplay() ?? "" };
        layout.Rows = Math.Max(DefaultRows, Math.Max(layout.Rows,
            items.Count == 0 ? DefaultRows : items.Max(x => x.Row + x.RowSpan)));
        layout.Columns = Math.Max(DefaultColumns, Math.Max(layout.Columns,
            items.Count == 0 ? DefaultColumns : items.Max(x => x.Column + x.ColumnSpan)));
        if (!Enum.IsDefined(layout.Edge)) layout.Edge = DockEdge.Top;
        if (!double.IsFinite(layout.Position)) layout.Position = 0.5;
        layout.Position = Math.Clamp(layout.Position, 0, 1);
        activeColumns = layout.Columns;
        activeRows = layout.Rows;
        Text = "REMEMBER";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ui.Background;
        DoubleBuffered = true;
        KeyPreview = true;
        MinimumSize = new Size(CollapsedHeight, CollapsedHeight);

        string? preferredDisplay = layout.Display;
        currentScreen = Screen.AllScreens.FirstOrDefault(x => x.DeviceName == preferredDisplay)
            ?? Screen.FromPoint(Cursor.Position);
        CalculateBounds(currentScreen);
        Bounds = expandedBounds;

        content.Dock = DockStyle.Fill;
        content.BackColor = Ui.Background;
        Controls.Add(content);
        BuildContent();
        screenButton = CreateScreenButton();
        content.Controls.Add(screenButton);
        UpdateScreenButton();

        handle = new EdgeHandle();
        handle.Dock = DockStyle.Fill;
        handle.Edge = layout.Edge;
        var handleTip = new ToolTip();
        handleTip.SetToolTip(handle, "REMEMBER / GRID · 移到边缘展开");
        handle.Visible = false;
        handle.Click += (_, _) => SetCollapsed(false);
        handle.MouseEnter += (_, _) => { if (collapsed && !animation.Enabled) hoverTimer.Start(); };
        handle.MouseLeave += (_, _) => hoverTimer.Stop();
        Controls.Add(handle);

        var menu = Ui.Menu();
        menu.Items.Add("展开 / 收起", null, (_, _) => SetCollapsed(!collapsed));
        var screens = new ToolStripMenuItem("切换显示器") { DropDown = Ui.Menu() };
        screens.DropDownOpening += (_, _) => PopulateScreenMenu(screens.DropDownItems);
        menu.Items.Add(screens);
        var checkUpdates = new ToolStripMenuItem("检查更新");
        checkUpdates.Click += async (_, _) =>
        {
            checkUpdates.Enabled = false;
            checkUpdates.Text = "正在检查 / 下载更新…";
            bool installing = await AppUpdater.CheckAndInstallAsync(this);
            if (installing)
            {
                exiting = true;
                Close();
            }
            else if (!IsDisposed)
            {
                checkUpdates.Text = "检查更新";
                checkUpdates.Enabled = true;
            }
        };
        menu.Items.Add(checkUpdates);
        var startAtLogin = new ToolStripMenuItem("开机自启");
        menu.Opening += (_, _) => startAtLogin.Checked = StartupSettings.IsEnabled();
        startAtLogin.Click += (_, _) =>
        {
            try
            {
                StartupSettings.SetEnabled(!StartupSettings.IsEnabled());
                startAtLogin.Checked = StartupSettings.IsEnabled();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法更改开机自启设置：{ex.Message}", "REMEMBER",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        menu.Items.Add(startAtLogin);
        menu.Items.Add("退出", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application,
            Text = "REMEMBER", Visible = true, ContextMenuStrip = menu
        };
        tray.DoubleClick += (_, _) => SetCollapsed(!collapsed);
        animation.Tick += Animate;
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (collapsed && handle.Visible && handle.ClientRectangle.Contains(
                handle.PointToClient(Cursor.Position)) &&
                Cursor.Position != cursorAtCollapse)
                SetCollapsed(false);
        };
        idleTimer.Tick += (_, _) =>
        {
            if (collapsed && !animation.Enabled &&
                handle.RectangleToScreen(handle.ClientRectangle).Contains(Cursor.Position) &&
                Cursor.Position != cursorAtCollapse)
            {
                if (!hoverTimer.Enabled) hoverTimer.Start();
            }
            else if (collapsed && hoverTimer.Enabled)
            {
                hoverTimer.Stop();
            }
            if (!collapsed && !movingWindow && !animation.Enabled && !screenMenu.Visible &&
                !OwnedForms.Any(x => x.Visible) &&
                (DateTime.UtcNow - lastActivity).TotalSeconds >= 5)
                SetCollapsed(true);
        };
        activityFilter = new BoardActivityFilter(this);
        Application.AddMessageFilter(activityFilter);
        idleTimer.Start();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        FormClosing += (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                SetCollapsed(true);
            }
        };
        FormClosed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Application.RemoveMessageFilter(activityFilter);
            animation.Dispose();
            idleTimer.Dispose();
            hoverTimer.Dispose();
            resizeGhost.Dispose();
            handleTip.Dispose();
            screenMenu.Dispose();
            tray.Visible = false;
            tray.Dispose();
            foreach (Image image in images.Values) image.Dispose();
        };
        content.Paint += (_, e) =>
        {
            using var border = new Pen(Ui.Line, 2);
            using var accent = new SolidBrush(Ui.Accent);
            e.Graphics.DrawRectangle(border, 1, 1, content.Width - 3, content.Height - 3);
            e.Graphics.FillRectangle(accent, 1, 1, 128, 3);
            e.Graphics.DrawLine(border, 12, 53, content.Width - 13, 53);
            e.Graphics.DrawLine(border, 12, content.Height - 43,
                content.Width - 13, content.Height - 43);
            e.Graphics.DrawLine(border, content.Width - 61, 58,
                content.Width - 61, content.Height - 50);
        };
        RebuildGrid();
    }

    private void BuildContent()
    {
        var mark = new RememberMark { Location = new Point(17, 5) };
        mark.Cursor = Cursors.SizeAll;
        mark.MouseDown += (_, e) => BeginWindowMove(mark, e);
        mark.MouseMove += (_, _) => UpdateWindowMove();
        mark.MouseUp += (_, _) => EndWindowMove(mark);
        mark.MouseCaptureChanged += (_, _) => EndWindowMove(mark);
        new ToolTip().SetToolTip(mark, "拖动到屏幕边缘");
        content.Controls.Add(mark);

        retractButton.Location = new Point(Width - 58, 11);
        retractButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        retractButton.ForeColor = Ui.Accent;
        retractButton.FlatAppearance.BorderColor = Ui.Accent;
        retractButton.Click += (_, _) => SetCollapsed(true);
        content.Controls.Add(retractButton);
        UpdateRetractButton();

        var paste = Ui.ToolbarButton("粘贴", 84, primary: true);
        paste.Location = new Point(Width - 362, 11);
        paste.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        paste.Click += (_, _) => PasteClipboard();
        content.Controls.Add(paste);

        var write = Ui.ToolbarButton("+ 文字", 92);
        write.Location = new Point(Width - 266, 11);
        write.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        write.Click += (_, _) => CreateText();
        content.Controls.Add(write);

        var import = Ui.ToolbarButton("+ 图片", 92);
        import.Location = new Point(Width - 162, 11);
        import.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        import.Click += (_, _) => ImportImages();
        content.Controls.Add(import);

        grid.Location = new Point(12, 58);
        grid.Size = new Size(Width - 78, Height - 102);
        grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        grid.BackColor = Ui.Background;
        grid.AllowDrop = true;
        grid.CanPlace = CanPlace;
        grid.GetItemSize = id =>
        {
            BoardItem? item = items.FirstOrDefault(x => x.Id == id);
            return item == null ? (1, 1) : (item.ColumnSpan, item.RowSpan);
        };
        grid.FileDrop += paths => AddImageFiles(paths);
        content.Controls.Add(grid);

        ConfigureGridButton(addColumn, "增加一列", 136, Height - 36,
            AnchorStyles.Left | AnchorStyles.Bottom, () => ChangeGridSize(0, 1));
        ConfigureGridButton(removeColumn, "减少最右侧的空列", 100, Height - 36,
            AnchorStyles.Left | AnchorStyles.Bottom, () => ChangeGridSize(0, -1));
        ConfigureGridButton(addRow, "增加一行", Width - 48, 126,
            AnchorStyles.Right | AnchorStyles.Top, () => ChangeGridSize(1, 0));
        ConfigureGridButton(removeRow, "减少最下方的空行", Width - 48, 92,
            AnchorStyles.Right | AnchorStyles.Top, () => ChangeGridSize(-1, 0));
        ConfigureGridLabel(columnLabel, 12, Height - 36, 80, "列 13",
            AnchorStyles.Left | AnchorStyles.Bottom);
        ConfigureGridLabel(rowLabel, Width - 50, 58, 46, "行 06",
            AnchorStyles.Right | AnchorStyles.Top);
        UpdateGridControls();
    }

    private void ConfigureGridButton(Button button, string tip, int x, int y,
        AnchorStyles anchor, Action click)
    {
        button.Location = new Point(x, y);
        button.Size = new Size(30, 28);
        button.ForeColor = button.Text == "+" ? Ui.Accent : Ui.Text;
        button.FlatAppearance.BorderColor = Ui.Line;
        button.Anchor = anchor;
        button.Click += (_, _) => click();
        new ToolTip().SetToolTip(button, tip);
        content.Controls.Add(button);
    }

    private void ConfigureGridLabel(Label label, int x, int y, int width,
        string caption, AnchorStyles anchor)
    {
        label.Location = new Point(x, y);
        label.Size = new Size(width, 28);
        label.Anchor = anchor;
        label.Text = caption;
        label.ForeColor = Ui.Muted;
        label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        label.TextAlign = ContentAlignment.MiddleCenter;
        content.Controls.Add(label);
    }

    private Button CreateScreenButton()
    {
        var button = Ui.ToolbarButton("屏幕 1 ▾", 104);
        button.Location = new Point(Width - 478, 11);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        button.Click += (_, _) =>
        {
            PopulateScreenMenu(screenMenu.Items);
            screenMenu.Show(button, new Point(0, button.Height));
        };
        return button;
    }

    private void PopulateScreenMenu(ToolStripItemCollection menuItems)
    {
        menuItems.Clear();
        Screen[] connected = Screen.AllScreens;
        for (int index = 0; index < connected.Length; index++)
        {
            Screen target = connected[index];
            string title = $"屏幕 {index + 1}  ·  {target.Bounds.Width} × {target.Bounds.Height}";
            if (target.Primary) title += "  主屏";
            var item = new ToolStripMenuItem(title)
            {
                Checked = target.DeviceName == currentScreen.DeviceName
            };
            item.Click += (_, _) => ChangeScreen(target);
            menuItems.Add(item);
        }
    }

    private void UpdateScreenButton()
    {
        int index = Array.FindIndex(Screen.AllScreens,
            x => x.DeviceName == currentScreen.DeviceName);
        screenButton.Text = $"屏幕 {Math.Max(1, index + 1)} ▾";
    }

    private void UpdateRetractButton() => retractButton.Text = layout.Edge switch
    {
        DockEdge.Top => "↑",
        DockEdge.Bottom => "↓",
        DockEdge.Left => "←",
        _ => "→"
    };

    private void CalculateBounds(Screen screen)
    {
        Rectangle work = screen.WorkingArea;
        int width = Math.Min(ExpandedWidth, Math.Max(CollapsedWidth + 24, work.Width - 24));
        int height = Math.Min(ExpandedHeight, Math.Max(CollapsedWidth + 24, work.Height - 24));
        int x = work.Left + (int)Math.Round(Math.Max(0, work.Width - width) * layout.Position);
        int y = work.Top + (int)Math.Round(Math.Max(0, work.Height - height) * layout.Position);
        int handleX = x + (width - CollapsedWidth) / 2;
        int handleY = y + (height - CollapsedWidth) / 2;
        switch (layout.Edge)
        {
            case DockEdge.Top:
                expandedBounds = new Rectangle(x, work.Top, width, height);
                collapsedBounds = new Rectangle(handleX, work.Top, CollapsedWidth, CollapsedHeight);
                break;
            case DockEdge.Bottom:
                expandedBounds = new Rectangle(x, work.Bottom - height, width, height);
                collapsedBounds = new Rectangle(handleX, work.Bottom - CollapsedHeight,
                    CollapsedWidth, CollapsedHeight);
                break;
            case DockEdge.Left:
                expandedBounds = new Rectangle(work.Left, y, width, height);
                collapsedBounds = new Rectangle(work.Left, handleY, CollapsedHeight, CollapsedWidth);
                break;
            default:
                expandedBounds = new Rectangle(work.Right - width, y, width, height);
                collapsedBounds = new Rectangle(work.Right - CollapsedHeight, handleY,
                    CollapsedHeight, CollapsedWidth);
                break;
        }
    }

    private void BeginWindowMove(Control grip, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || collapsed) return;
        animation.Stop();
        movingWindow = true;
        moveOffset = new Point(Cursor.Position.X - Left, Cursor.Position.Y - Top);
        grip.Capture = true;
        RegisterActivity();
    }

    private void UpdateWindowMove()
    {
        if (!movingWindow) return;
        Point cursor = Cursor.Position;
        Location = new Point(cursor.X - moveOffset.X, cursor.Y - moveOffset.Y);
        RegisterActivity();
    }

    private void EndWindowMove(Control grip)
    {
        if (!movingWindow) return;
        movingWindow = false;
        grip.Capture = false;
        Point cursor = Cursor.Position;
        currentScreen = Screen.FromPoint(cursor);
        Rectangle work = currentScreen.WorkingArea;
        int top = Math.Abs(cursor.Y - work.Top);
        int bottom = Math.Abs(cursor.Y - work.Bottom);
        int left = Math.Abs(cursor.X - work.Left);
        int right = Math.Abs(cursor.X - work.Right);
        int nearest = Math.Min(Math.Min(top, bottom), Math.Min(left, right));
        layout.Edge = nearest == top ? DockEdge.Top : nearest == bottom ? DockEdge.Bottom
            : nearest == left ? DockEdge.Left : DockEdge.Right;
        bool horizontal = layout.Edge is DockEdge.Top or DockEdge.Bottom;
        int travel = horizontal ? work.Width - Width : work.Height - Height;
        int offset = horizontal ? Left - work.Left : Top - work.Top;
        layout.Position = travel <= 0 ? 0.5 : Math.Clamp((double)offset / travel, 0, 1);
        layout.Display = currentScreen.DeviceName;
        store.SaveLayout(layout);
        store.SaveDisplay(layout.Display);
        CalculateBounds(currentScreen);
        handle.Edge = layout.Edge;
        handle.Invalidate();
        Bounds = expandedBounds;
        UpdateScreenButton();
        UpdateRetractButton();
        RegisterActivity();
    }

    private void ChangeScreen(Screen screen)
    {
        animation.Stop();
        resizeGhost.Hide();
        currentScreen = screen;
        store.SaveDisplay(screen.DeviceName);
        layout.Display = screen.DeviceName;
        store.SaveLayout(layout);
        CalculateBounds(screen);
        collapsed = false;
        handle.Visible = false;
        content.Visible = true;
        StartPosition = FormStartPosition.Manual;
        Bounds = expandedBounds;
        UpdateScreenButton();
        RegisterActivity();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            Screen screen = Screen.AllScreens.FirstOrDefault(
                x => x.DeviceName == currentScreen.DeviceName)
                ?? Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
            currentScreen = screen;
            layout.Display = screen.DeviceName;
            store.SaveLayout(layout);
            CalculateBounds(screen);
            animation.Stop();
            StartPosition = FormStartPosition.Manual;
            Bounds = collapsed ? collapsedBounds : expandedBounds;
            UpdateScreenButton();
        }));
    }

    internal void RegisterActivity() => lastActivity = DateTime.UtcNow;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V) && !collapsed)
        {
            PasteClipboard();
            return true;
        }
        if (keyData == Keys.Escape && !collapsed)
        {
            SetCollapsed(true);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void SetCollapsed(bool value)
    {
        if (collapsed == value && !animation.Enabled) return;
        collapsed = value;
        animation.Stop();
        hoverTimer.Stop();
        resizeGhost.Hide();
        if (!value) RegisterActivity();
        handle.Visible = false;
        content.Visible = false;
        animationFrom = Bounds;
        animationTo = value ? collapsedBounds : expandedBounds;
        animationStart = DateTime.UtcNow;
        animation.Start();
    }

    private void Animate(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.UtcNow - animationStart).TotalMilliseconds / 220);
        double eased = 1 - Math.Pow(1 - t, 3);
        int width = Interpolate(animationFrom.Width, animationTo.Width, eased);
        int height = Interpolate(animationFrom.Height, animationTo.Height, eased);
        int x = layout.Edge == DockEdge.Right ? animationTo.Right - width
            : layout.Edge is DockEdge.Top or DockEdge.Bottom
                ? animationTo.Left + (animationTo.Width - width) / 2 : animationTo.Left;
        int y = layout.Edge == DockEdge.Bottom ? animationTo.Bottom - height
            : layout.Edge is DockEdge.Left or DockEdge.Right
                ? animationTo.Top + (animationTo.Height - height) / 2 : animationTo.Top;
        Bounds = new Rectangle(x, y, width, height);
        if (t >= 1)
        {
            animation.Stop();
            Bounds = animationTo;
            if (collapsed)
            {
                cursorAtCollapse = Cursor.Position;
                content.Visible = false;
                handle.Visible = true;
            }
            else
            {
                handle.Visible = false;
                content.Visible = true;
            }
        }
    }

    private static int Interpolate(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    private void PasteClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage())
            {
                using Image? image = Clipboard.GetImage();
                if (image != null) { AddImage(image); return; }
            }
            if (Clipboard.ContainsFileDropList())
            {
                AddImageFiles(Clipboard.GetFileDropList().Cast<string>().ToArray());
                return;
            }
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText().Trim();
                if (text.Length > 0) { AddText(text); return; }
            }
            // Empty clipboard: keep the board quiet.
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取剪贴板失败：" + ex.Message, "粘贴失败",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CreateText()
    {
        using var editor = new TextEditorForm("");
        if (editor.ShowDialog(this) == DialogResult.OK) AddText(editor.Value);
    }

    private void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var (row, column) = FirstFreeCell(2, 2);
        items.Add(new BoardItem { Kind = "text", Text = text, Row = row, Column = column,
            ColumnSpan = 2, RowSpan = 2 });
        SaveAndRefresh();
    }

    private void AddImage(Image source)
    {
        var (row, column) = FirstFreeCell(3, 2);
        var item = new BoardItem { Kind = "image", Row = row, Column = column,
            ColumnSpan = 3, RowSpan = 2 };
        item.ImageFile = store.SaveImage(source, item.Id);
        items.Add(item);
        SaveAndRefresh();
    }

    private void ImportImages()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要贴到黑板上的图片", Multiselect = true,
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|所有文件|*.*"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddImageFiles(dialog.FileNames);
    }

    private void AddImageFiles(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                using var source = Image.FromFile(path);
                AddImage(source);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"无法导入图片：{path}\n{ex.Message}", "导入失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (collapsed) SetCollapsed(false);
    }

    private (int row, int column) FirstFreeCell(int width, int height)
    {
        for (int row = 0; ; row++)
            for (int column = 0; column <= layout.Columns - width; column++)
                if (CanPlace(null, row, column, width, height))
                {
                    if (row + height > layout.Rows)
                    {
                        layout.Rows = row + height;
                        activeRows = layout.Rows;
                        store.SaveLayout(layout);
                    }
                    return (row, column);
                }
    }

    private bool CanPlace(string? movingId, int row, int column, int width, int height)
    {
        if (row < 0 || column < 0 || width < 1 || height < 1 || column + width > layout.Columns)
            return false;
        return !items.Any(x => x.Id != movingId &&
            column < x.Column + x.ColumnSpan && column + width > x.Column &&
            row < x.Row + x.RowSpan && row + height > x.Row);
    }

    private void MoveItem(string id, int row, int column)
    {
        BoardItem? source = items.FirstOrDefault(x => x.Id == id);
        if (source == null || (source.Row == row && source.Column == column)) return;
        if (!CanPlace(id, row, column, source.ColumnSpan, source.RowSpan)) return;
        source.Row = row;
        source.Column = column;
        SaveAndRefresh();
    }

    private void ResizeItem(BoardItem item, int width, int height)
    {
        if (!CanPlace(item.Id, item.Row, item.Column, width, height))
        {
            RebuildGrid();
            return;
        }
        item.ColumnSpan = width;
        item.RowSpan = height;
        if (item.Row + height > layout.Rows)
        {
            layout.Rows = item.Row + height;
            activeRows = layout.Rows;
            store.SaveLayout(layout);
        }
        SaveAndRefresh();
    }

    private void ChangeGridSize(int rows, int columns)
    {
        if (rows < 0 && (layout.Rows <= DefaultRows ||
            items.Any(x => x.Row + x.RowSpan >= layout.Rows))) return;
        if (columns < 0 && (layout.Columns <= DefaultColumns ||
            items.Any(x => x.Column + x.ColumnSpan >= layout.Columns))) return;
        layout.Rows = Math.Max(DefaultRows, layout.Rows + rows);
        layout.Columns = Math.Max(DefaultColumns, layout.Columns + columns);
        activeRows = layout.Rows;
        activeColumns = layout.Columns;
        store.SaveLayout(layout);
        RebuildGrid();
        if (rows > 0) grid.ScrollToBottom();
        if (columns > 0) grid.ScrollToRight();
        RegisterActivity();
    }

    private void UpdateGridControls()
    {
        rowLabel.Text = $"行 {layout.Rows:00}";
        columnLabel.Text = $"列 {layout.Columns:00}";
        removeRow.Enabled = layout.Rows > DefaultRows &&
            !items.Any(x => x.Row + x.RowSpan >= layout.Rows);
        removeColumn.Enabled = layout.Columns > DefaultColumns &&
            !items.Any(x => x.Column + x.ColumnSpan >= layout.Columns);
    }

    private void PreviewResize(BoardItem item, int width, int height)
    {
        bool valid = CanPlace(item.Id, item.Row, item.Column, width, height);
        int step = CellSize + Gap;
        Rectangle target = new(grid.PointToScreen(grid.CellLocation(item.Row, item.Column)),
            new Size(width * step - Gap, height * step - Gap));
        Rectangle viewport = grid.RectangleToScreen(grid.ClientRectangle);
        target = Rectangle.Intersect(target, viewport);
        if (target.Width < 1 || target.Height < 1)
        {
            resizeGhost.Hide();
            return;
        }
        resizeGhost.ShowPreview(this, target, width, height, valid);
    }

    private void EditItem(BoardItem item)
    {
        if (item.Kind == "image")
        {
            using var viewer = new ImageViewerForm(store.FullImagePath(item));
            viewer.ShowDialog(this);
            return;
        }
        using var editor = new TextEditorForm(item.Text);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            item.Text = editor.Value;
            SaveAndRefresh();
        }
    }

    private void DeleteItem(BoardItem item)
    {
        items.Remove(item);
        if (images.Remove(item.Id, out Image? image)) image.Dispose();
        if (item.Kind == "image")
        {
            try { File.Delete(store.FullImagePath(item)); } catch { /* Data remains safe if file is locked. */ }
        }
        SaveAndRefresh();
    }

    private Image? GetImage(BoardItem item)
    {
        if (item.Kind != "image") return null;
        if (images.TryGetValue(item.Id, out Image? image)) return image;
        try
        {
            using var stream = File.OpenRead(store.FullImagePath(item));
            using var original = Image.FromStream(stream);
            image = new Bitmap(original);
            images[item.Id] = image;
            return image;
        }
        catch { return null; }
    }

    private void SaveAndRefresh()
    {
        store.Save(items);
        RebuildGrid();
    }

    private void RebuildGrid()
    {
        resizeGhost.Hide();
        int scrollY = grid.ScrollOffset;
        int scrollX = grid.HorizontalOffset;
        grid.SuspendLayout();
        foreach (Control control in grid.Controls.Cast<Control>().ToArray())
        {
            grid.Controls.Remove(control);
            control.Dispose();
        }
        grid.ResetScrollForRebuild();
        grid.SetSize(layout.Rows, layout.Columns);
        foreach (BoardItem item in items)
        {
            var tile = new TileControl(item, GetImage(item))
            {
                Location = new Point(StartX + item.Column * (CellSize + Gap),
                    StartY + item.Row * (CellSize + Gap)),
                Size = new Size(item.ColumnSpan * (CellSize + Gap) - Gap,
                    item.RowSpan * (CellSize + Gap) - Gap)
            };
            tile.ResizePreview += (width, height) => PreviewResize(item, width, height);
            tile.ResizeRequested += (width, height) =>
            {
                resizeGhost.Hide();
                ResizeItem(item, width, height);
            };
            tile.ResizeCancelled += () => resizeGhost.Hide();
            tile.MovePreview += (screen, offset) =>
            {
                Point? preview = grid.PreviewMove(item.Id, screen, offset);
                if (preview.HasValue) tile.Location = grid.CellLocation(preview.Value.Y, preview.Value.X);
            };
            tile.MoveFinished += (screen, offset) =>
            {
                Point? destination = grid.CompleteMove(item.Id, screen, offset);
                if (destination.HasValue) MoveItem(item.Id, destination.Value.Y, destination.Value.X);
                if (!tile.IsDisposed) tile.Location = grid.CellLocation(item.Row, item.Column);
            };
            tile.MoveCancelled += () =>
            {
                grid.CancelPreview();
                if (!tile.IsDisposed) tile.Location = grid.CellLocation(item.Row, item.Column);
            };
            tile.FileDrop += paths => AddImageFiles(paths);
            tile.ScrollRequested += steps => grid.ScrollBy(steps * (CellSize + Gap));
            tile.HorizontalScrollRequested += steps =>
                grid.ScrollHorizontally(grid.HorizontalOffset + steps * (CellSize + Gap));
            tile.OpenRequested += () => EditItem(item);
            tile.DeleteRequested += () => DeleteItem(item);
            grid.Controls.Add(tile);
        }
        grid.ResumeLayout();
        grid.ScrollTo(scrollY);
        grid.ScrollHorizontally(scrollX);
        grid.Invalidate();
        UpdateGridControls();
    }

    internal static int GridColumns => activeColumns;
    internal static int GridRows => activeRows;
    internal static int GridCellSize => CellSize;
    internal static int CellGap => Gap;
    internal static int CellStartX => StartX;
    internal static int CellStartY => StartY;
}

internal sealed class BoardActivityFilter : IMessageFilter
{
    private readonly BoardForm board;

    public BoardActivityFilter(BoardForm board) => this.board = board;

    public bool PreFilterMessage(ref Message message)
    {
        int code = message.Msg;
        if (code is not (0x0200 or 0x0201 or 0x0202 or 0x020A or 0x0100 or 0x0102 or 0x0104))
            return false;
        Control? control = Control.FromChildHandle(message.HWnd);
        if (control?.FindForm() == board) board.RegisterActivity();
        return false;
    }
}

internal sealed class GridPanel : Panel
{
    public int RowCount { get; set; } = 6;
    public int ColumnCount { get; private set; } = 13;
    public int ScrollOffset { get; private set; }
    public int HorizontalOffset { get; private set; }
    public Func<string?, int, int, int, int, bool>? CanPlace { get; set; }
    public Func<string, (int width, int height)>? GetItemSize { get; set; }
    public event Action<string[]>? FileDrop;
    private bool draggingThumb;
    private bool draggingHorizontalThumb;
    private int dragStartY;
    private int dragStartX;
    private int dragStartOffset;
    private const int BarWidth = 16;
    private int ContentHeight => BoardForm.CellStartY * 2 +
        RowCount * (BoardForm.GridCellSize + BoardForm.CellGap);
    private int ContentWidth => BoardForm.CellStartX * 2 +
        ColumnCount * (BoardForm.GridCellSize + BoardForm.CellGap);
    private int MaxScroll => Math.Max(0, ContentHeight - ClientSize.Height);
    private int MaxHorizontalScroll => Math.Max(0, ContentWidth - ClientSize.Width);

    public GridPanel()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) FileDrop?.Invoke(files);
        };
    }

    public void SetSize(int rows, int columns)
    {
        RowCount = rows;
        ColumnCount = columns;
        ScrollTo(ScrollOffset);
        ScrollHorizontally(HorizontalOffset);
    }

    public void ResetScrollForRebuild()
    {
        ScrollOffset = 0;
        HorizontalOffset = 0;
    }

    public void ScrollBy(int amount) => ScrollTo(ScrollOffset + amount);
    public void ScrollToBottom() => ScrollTo(MaxScroll);
    public void ScrollToRight() => ScrollHorizontally(MaxHorizontalScroll);

    public void ScrollTo(int offset)
    {
        int next = Math.Clamp(offset, 0, MaxScroll);
        int delta = ScrollOffset - next;
        if (delta == 0) { Invalidate(); return; }
        ScrollOffset = next;
        foreach (Control control in Controls) control.Top += delta;
        Invalidate();
    }

    public void ScrollHorizontally(int offset)
    {
        int next = Math.Clamp(offset, 0, MaxHorizontalScroll);
        int delta = HorizontalOffset - next;
        if (delta == 0) { Invalidate(); return; }
        HorizontalOffset = next;
        foreach (Control control in Controls) control.Left += delta;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ScrollTo(ScrollOffset);
        ScrollHorizontally(HorizontalOffset);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int amount = -Math.Sign(e.Delta) * (BoardForm.GridCellSize + BoardForm.CellGap);
        if ((ModifierKeys & Keys.Shift) != 0) ScrollHorizontally(HorizontalOffset + amount);
        else ScrollBy(amount);
        base.OnMouseWheel(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && MaxHorizontalScroll > 0 && e.Y >= Height - BarWidth)
        {
            Rectangle horizontalThumb = HorizontalThumbBounds();
            if (!horizontalThumb.Contains(e.Location))
            {
                int horizontalRange = Math.Max(1, Width - horizontalThumb.Width);
                ScrollHorizontally((int)Math.Round((double)(e.X - horizontalThumb.Width / 2) /
                    horizontalRange * MaxHorizontalScroll));
            }
            draggingHorizontalThumb = true;
            dragStartX = e.X;
            dragStartOffset = HorizontalOffset;
            Capture = true;
            return;
        }
        if (e.Button != MouseButtons.Left || MaxScroll == 0 || e.X < Width - BarWidth) return;
        Rectangle thumb = ThumbBounds();
        if (!thumb.Contains(e.Location))
        {
            int range = Math.Max(1, Height - thumb.Height);
            ScrollTo((int)Math.Round((double)(e.Y - thumb.Height / 2) / range * MaxScroll));
        }
        draggingThumb = true;
        dragStartY = e.Y;
        dragStartOffset = ScrollOffset;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (draggingHorizontalThumb)
        {
            int horizontalRange = Math.Max(1, Width - HorizontalThumbBounds().Width);
            ScrollHorizontally(dragStartOffset + (int)Math.Round(
                (double)(e.X - dragStartX) * MaxHorizontalScroll / horizontalRange));
            return;
        }
        if (!draggingThumb) return;
        int range = Math.Max(1, Height - ThumbBounds().Height);
        ScrollTo(dragStartOffset + (int)Math.Round((double)(e.Y - dragStartY) * MaxScroll / range));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        draggingThumb = false;
        draggingHorizontalThumb = false;
        Capture = false;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        draggingThumb = false;
        draggingHorizontalThumb = false;
    }

    private Rectangle ThumbBounds()
    {
        int viewportHeight = Math.Max(1, Height);
        int height = Math.Clamp((int)Math.Round((double)viewportHeight * viewportHeight / ContentHeight),
            Math.Min(38, viewportHeight), viewportHeight);
        int y = MaxScroll == 0 ? 0 : (int)Math.Round((double)ScrollOffset / MaxScroll * (Height - height));
        return new Rectangle(Width - BarWidth + 3, y + 2, BarWidth - 6, Math.Max(4, height - 4));
    }

    private Rectangle HorizontalThumbBounds()
    {
        int viewportWidth = Math.Max(1, Width);
        int width = Math.Clamp((int)Math.Round((double)viewportWidth * viewportWidth / ContentWidth),
            Math.Min(38, viewportWidth), viewportWidth);
        int x = MaxHorizontalScroll == 0 ? 0 : (int)Math.Round(
            (double)HorizontalOffset / MaxHorizontalScroll * (Width - width));
        return new Rectangle(x + 2, Height - BarWidth + 3, Math.Max(4, width - 4), BarWidth - 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Ui.Line, 1) { DashStyle = DashStyle.Dot };
        for (int row = 0; row < RowCount; row++)
        for (int column = 0; column < ColumnCount; column++)
        {
            var bounds = new Rectangle(BoardForm.CellStartX + column * (BoardForm.GridCellSize + BoardForm.CellGap) - HorizontalOffset,
                BoardForm.CellStartY + row * (BoardForm.GridCellSize + BoardForm.CellGap) - ScrollOffset,
                BoardForm.GridCellSize, BoardForm.GridCellSize);
            if (bounds.Bottom < 0 || bounds.Top > Height) continue;
            if (bounds.Right < 0 || bounds.Left > Width) continue;
            e.Graphics.DrawRectangle(pen, bounds);
        }
        if (MaxScroll > 0)
        {
            var track = new Rectangle(Width - BarWidth, 0, BarWidth, Height);
            using var trackBrush = new SolidBrush(Ui.Surface);
            using var thumbBrush = new SolidBrush(Ui.Accent);
            using var border = new Pen(Ui.Line);
            e.Graphics.FillRectangle(trackBrush, track);
            e.Graphics.DrawLine(border, track.Left, 0, track.Left, Height);
            Rectangle thumb = ThumbBounds();
            e.Graphics.FillRectangle(thumbBrush, thumb);
            for (int i = 0; i < 3; i++)
                e.Graphics.DrawLine(border, thumb.X + 2, thumb.Y + 13 + i * 5,
                    thumb.Right - 3, thumb.Y + 13 + i * 5);
        }
        if (MaxHorizontalScroll > 0)
        {
            var track = new Rectangle(0, Height - BarWidth, Width, BarWidth);
            using var trackBrush = new SolidBrush(Ui.Surface);
            using var thumbBrush = new SolidBrush(Ui.Accent);
            using var border = new Pen(Ui.Line);
            e.Graphics.FillRectangle(trackBrush, track);
            e.Graphics.DrawLine(border, 0, track.Top, Width, track.Top);
            e.Graphics.FillRectangle(thumbBrush, HorizontalThumbBounds());
        }
        using var frame = new SolidBrush(Ui.Line);
        e.Graphics.FillRectangle(frame, 0, 0, Width, 1);
        e.Graphics.FillRectangle(frame, 0, Height - 1, Width, 1);
        e.Graphics.FillRectangle(frame, 0, 0, 1, Height);
        e.Graphics.FillRectangle(frame, Width - 1, 0, 1, Height);
    }

    public Point CellLocation(int row, int column) => new(
        BoardForm.CellStartX + column * (BoardForm.GridCellSize + BoardForm.CellGap) - HorizontalOffset,
        BoardForm.CellStartY + row * (BoardForm.GridCellSize + BoardForm.CellGap) - ScrollOffset);

    private Point? TargetCell(string id, Point screen, Point mouseOffset)
    {
        Point cursor = PointToClient(screen);
        if (!ClientRectangle.Contains(cursor)) return null;
        Point origin = PointToClient(new Point(screen.X - mouseOffset.X, screen.Y - mouseOffset.Y));
        int step = BoardForm.GridCellSize + BoardForm.CellGap;
        int column = (int)Math.Round((double)(origin.X - BoardForm.CellStartX + HorizontalOffset) / step,
            MidpointRounding.AwayFromZero);
        int row = (int)Math.Round((double)(origin.Y - BoardForm.CellStartY + ScrollOffset) / step,
            MidpointRounding.AwayFromZero);
        if (row < 0 || row >= RowCount) return null;
        var size = GetItemSize?.Invoke(id) ?? (1, 1);
        return CanPlace?.Invoke(id, row, column, size.Item1, size.Item2) == true
            ? new Point(column, row) : null;
    }

    public Point? PreviewMove(string id, Point screen, Point mouseOffset) =>
        TargetCellWithAutoScroll(id, screen, mouseOffset);

    private Point? TargetCellWithAutoScroll(string id, Point screen, Point mouseOffset)
    {
        Point cursor = PointToClient(screen);
        if (!ClientRectangle.Contains(cursor)) return null;
        const int edge = 30;
        if (cursor.X >= Width - edge) ScrollHorizontally(HorizontalOffset + 12);
        else if (cursor.X < edge) ScrollHorizontally(HorizontalOffset - 12);
        if (cursor.Y >= Height - edge) ScrollBy(12);
        else if (cursor.Y < edge) ScrollBy(-12);
        return TargetCell(id, screen, mouseOffset);
    }

    public Point? CompleteMove(string id, Point screen, Point mouseOffset) =>
        TargetCell(id, screen, mouseOffset);

    public void CancelPreview() { }
}

internal sealed class ResizeGhostForm : Form
{
    private static readonly Font SizeFont = new("Consolas", 11F, FontStyle.Bold);
    private bool valid;
    private string label = "";

    public ResizeGhostForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ui.SurfaceAlt;
        Opacity = 0.72;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x20 | 0x08000000; // Click through; do not activate.
            return parameters;
        }
    }

    public void ShowPreview(Form owner, Rectangle bounds, int columns, int rows, bool canPlace)
    {
        valid = canPlace;
        label = $"{columns} × {rows}";
        Bounds = bounds;
        if (!Visible) Show(owner);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Color color = valid ? Ui.Secondary : Color.FromArgb(231, 107, 96);
        using var border = new Pen(color, 3);
        using var hatch = new Pen(Color.FromArgb(70, color), 1);
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        for (int x = -Height; x < Width; x += 18)
            e.Graphics.DrawLine(hatch, x, Height, x + Height, 0);
        TextRenderer.DrawText(e.Graphics, label, SizeFont,
            new Rectangle(8, 7, Math.Max(1, Width - 16), 24), color,
            TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }
}

internal sealed class TileControl : Control
{
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 10F);
    private static readonly Font CompactFont = new("Microsoft YaHei UI", 8F);
    private readonly BoardItem item;
    private readonly Image? image;
    private Point mouseDown;
    private bool pressed;
    private bool moving;
    private bool resizing;
    private Size resizePreview;
    public event Action<int, int>? ResizePreview;
    public event Action<int, int>? ResizeRequested;
    public event Action? ResizeCancelled;
    public event Action<Point, Point>? MovePreview;
    public event Action<Point, Point>? MoveFinished;
    public event Action? MoveCancelled;
    public event Action<string[]>? FileDrop;
    public event Action<int>? ScrollRequested;
    public event Action<int>? HorizontalScrollRequested;
    public event Action? OpenRequested;
    public event Action? DeleteRequested;

    public TileControl(BoardItem item, Image? image)
    {
        this.item = item;
        this.image = image;
        DoubleBuffered = true;
        AllowDrop = true;
        Cursor = Cursors.Hand;
        resizePreview = new Size(item.ColumnSpan, item.RowSpan);
        ContextMenuStrip = Ui.Menu();
        ContextMenuStrip.Items.Add(item.Kind == "image" ? "查看图片" : "编辑文字", null,
            (_, _) => OpenRequested?.Invoke());
        ContextMenuStrip.Items.Add("删除方块", null, (_, _) => DeleteRequested?.Invoke());
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) FileDrop?.Invoke(files);
        };
        MouseWheel += (_, e) =>
        {
            int direction = -Math.Sign(e.Delta);
            if ((ModifierKeys & Keys.Shift) != 0) HorizontalScrollRequested?.Invoke(direction);
            else ScrollRequested?.Invoke(direction);
        };
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            mouseDown = e.Location;
            resizing = e.X >= Width - 28 && e.Y >= Height - 28;
            Capture = true;
            if (resizing)
            {
                Cursor = Cursors.SizeNWSE;
                ResizePreview?.Invoke(resizePreview.Width, resizePreview.Height);
            }
        };
        MouseMove += (_, e) =>
        {
            if (resizing && pressed)
            {
                int step = BoardForm.GridCellSize + BoardForm.CellGap;
                resizePreview = new Size(Math.Clamp(item.ColumnSpan + (int)Math.Round(
                    (double)(e.X - mouseDown.X) / step), 1, BoardForm.GridColumns),
                    Math.Clamp(item.RowSpan + (int)Math.Round((double)(e.Y - mouseDown.Y) / step),
                        1, Math.Max(12, BoardForm.GridRows)));
                ResizePreview?.Invoke(resizePreview.Width, resizePreview.Height);
                return;
            }
            Cursor = e.X >= Width - 28 && e.Y >= Height - 28 ? Cursors.SizeNWSE : Cursors.Hand;
            if (!pressed) return;
            if (!moving && Math.Abs(e.X - mouseDown.X) + Math.Abs(e.Y - mouseDown.Y) < 7) return;
            moving = true;
            MovePreview?.Invoke(PointToScreen(e.Location), mouseDown);
        };
        MouseUp += (_, e) =>
        {
            if (!pressed) return;
            bool wasResizing = resizing;
            bool wasMoving = moving;
            pressed = false;
            resizing = false;
            moving = false;
            Capture = false;
            Cursor = Cursors.Hand;
            if (wasResizing)
            {
                ResizeCancelled?.Invoke();
                if (resizePreview.Width != item.ColumnSpan || resizePreview.Height != item.RowSpan)
                    ResizeRequested?.Invoke(resizePreview.Width, resizePreview.Height);
            }
            else if (wasMoving) MoveFinished?.Invoke(PointToScreen(e.Location), mouseDown);
        };
        MouseCaptureChanged += (_, _) =>
        {
            if (!pressed) return;
            pressed = false;
            moving = false;
            bool wasResizing = resizing;
            resizing = false;
            if (wasResizing) ResizeCancelled?.Invoke();
            MoveCancelled?.Invoke();
        };
        DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
        using var background = new SolidBrush(Ui.SurfaceAlt);
        using var border = new Pen(Ui.Line, 2);
        using var accent = new SolidBrush(item.Kind == "image" ? Ui.Secondary : Ui.Accent);
        e.Graphics.FillRectangle(background, bounds);
        e.Graphics.DrawRectangle(border, bounds);
        e.Graphics.FillRectangle(accent, 4, 4, Math.Min(18, Width - 8), 3);

        bool compact = Width < 115 || Height < 105;
        if (item.Kind == "image")
        {
            if (image != null)
            {
                Rectangle imageBounds = new(6, 6, Width - 12, Height - 12);
                e.Graphics.FillRectangle(Brushes.Black, imageBounds);
                float ratio = Math.Min((float)imageBounds.Width / image.Width, (float)imageBounds.Height / image.Height);
                int w = Math.Max(1, (int)(image.Width * ratio));
                int h = Math.Max(1, (int)(image.Height * ratio));
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(image, imageBounds.X + (imageBounds.Width - w) / 2,
                    imageBounds.Y + (imageBounds.Height - h) / 2, w, h);
            }
            else TextRenderer.DrawText(e.Graphics, "图片未找到", Font,
                new Rectangle(9, 9, Width - 18, 40), Ui.Muted);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, item.Text, compact ? CompactFont : BodyFont,
                new Rectangle(11, 11, Width - 22, Height - 22), Ui.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        using var gripPen = new Pen(Ui.Accent, 2);
        for (int i = 0; i < 3; i++)
            e.Graphics.DrawLine(gripPen, Width - 9 - i * 6, Height - 7,
                Width - 7, Height - 9 - i * 6);
    }
}

internal sealed class TextEditorForm : Form
{
    private readonly TextBox editor;
    public string Value => editor.Text.Trim();

    public TextEditorForm(string initial)
    {
        Text = string.IsNullOrEmpty(initial) ? "写一张文字便签" : "编辑文字便签";
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ui.Background;
        ForeColor = Ui.Text;
        ClientSize = new Size(470, 340);
        Ui.AddDialogChrome(this);
        editor = new TextBox
        {
            Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical,
            Text = initial, Location = new Point(2, 2), Size = new Size(426, 214),
            BackColor = Ui.Surface, ForeColor = Ui.Text, BorderStyle = BorderStyle.None,
            Font = new Font("Microsoft YaHei UI", 10F)
        };
        var frame = new Panel { Location = new Point(20, 62), Size = new Size(430, 218),
            BackColor = Ui.Line };
        frame.Controls.Add(editor);
        Controls.Add(frame);

        var save = Ui.Button("保存", Ui.Accent, Ui.Background, 94);
        save.Location = new Point(356, 294);
        save.DialogResult = DialogResult.OK;
        save.Enabled = Value.Length > 0;
        editor.TextChanged += (_, _) => save.Enabled = Value.Length > 0;
        Controls.Add(save);
        var cancel = Ui.Button("取消", Ui.SurfaceAlt, Ui.Text, 94);
        cancel.Location = new Point(252, 294);
        cancel.DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
        Shown += (_, _) => editor.Focus();
    }
}

internal sealed class ImageViewerForm : Form
{
    private readonly Image? image;

    public ImageViewerForm(string path)
    {
        Text = "查看图片";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Ui.Background;
        ForeColor = Ui.Text;
        ClientSize = new Size(900, 680);
        ShowInTaskbar = false;
        TopMost = true;
        Ui.AddDialogChrome(this);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        try
        {
            using var stream = File.OpenRead(path);
            using var original = Image.FromStream(stream);
            image = new Bitmap(original);

            var canvas = new ImageCanvas(image)
            {
                Location = new Point(12, 54),
                Size = new Size(ClientSize.Width - 24, ClientSize.Height - 118),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(canvas);

            var hint = new Label
            {
                Text = "滚轮缩放  ·  拖动平移",
                Location = new Point(16, ClientSize.Height - 40),
                Size = new Size(250, 28),
                Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
                ForeColor = Ui.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(hint);

            var zoomOut = Ui.ToolbarButton("−", 36);
            zoomOut.Location = new Point(ClientSize.Width - 236, ClientSize.Height - 42);
            zoomOut.Size = new Size(36, 32);
            zoomOut.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            zoomOut.Click += (_, _) => canvas.ZoomAt(1f / 1.25f, canvas.Center);
            Controls.Add(zoomOut);

            var zoomText = new Label
            {
                Location = new Point(ClientSize.Width - 192, ClientSize.Height - 42),
                Size = new Size(62, 32),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
                ForeColor = Ui.Accent,
                Font = new Font("Consolas", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(zoomText);

            var zoomIn = Ui.ToolbarButton("+", 36);
            zoomIn.Location = new Point(ClientSize.Width - 122, ClientSize.Height - 42);
            zoomIn.Size = new Size(36, 32);
            zoomIn.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            zoomIn.Click += (_, _) => canvas.ZoomAt(1.25f, canvas.Center);
            Controls.Add(zoomIn);

            var fit = Ui.ToolbarButton("适应", 64);
            fit.Location = new Point(ClientSize.Width - 78, ClientSize.Height - 42);
            fit.Size = new Size(64, 32);
            fit.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            fit.Click += (_, _) => canvas.FitToWindow();
            Controls.Add(fit);

            canvas.ZoomChanged += () => zoomText.Text = $"{canvas.Zoom * 100:0}%";
            Paint += (_, e) =>
            {
                using var line = new Pen(Ui.Line);
                e.Graphics.DrawLine(line, 12, ClientSize.Height - 52,
                    ClientSize.Width - 13, ClientSize.Height - 52);
            };
            Shown += (_, _) => { canvas.FitToWindow(); canvas.Focus(); };
        }
        catch (Exception ex)
        {
            Controls.Add(new Label
            {
                Location = new Point(12, 54), Size = new Size(ClientSize.Width - 24, ClientSize.Height - 66),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Text = "无法打开图片：" + ex.Message,
                ForeColor = Ui.Text, TextAlign = ContentAlignment.MiddleCenter
            });
        }
        FormClosed += (_, _) => image?.Dispose();
    }
}

internal sealed class ImageCanvas : Control
{
    private readonly Image image;
    private float zoom = 1f;
    private PointF origin;
    private bool fitted = true;
    private bool dragging;
    private Point lastMouse;

    public event Action? ZoomChanged;
    public float Zoom => zoom;
    public Point Center => new(Width / 2, Height / 2);

    public ImageCanvas(Image image)
    {
        this.image = image;
        DoubleBuffered = true;
        TabStop = true;
        BackColor = Ui.Surface;
    }

    public void FitToWindow()
    {
        if (Width <= 0 || Height <= 0) return;
        zoom = FitZoom();
        origin = new PointF((Width - image.Width * zoom) / 2f,
            (Height - image.Height * zoom) / 2f);
        fitted = true;
        Cursor = Cursors.Default;
        ZoomChanged?.Invoke();
        Invalidate();
    }

    public void ZoomAt(float factor, Point anchor)
    {
        if (Width <= 0 || Height <= 0) return;
        float fitZoom = FitZoom();
        float next = Math.Clamp(zoom * factor,
            Math.Min(0.05f, fitZoom / 4f), Math.Max(16f, fitZoom * 4f));
        if (Math.Abs(next - zoom) < 0.0001f) return;

        float imageX = (anchor.X - origin.X) / zoom;
        float imageY = (anchor.Y - origin.Y) / zoom;
        zoom = next;
        origin = new PointF(anchor.X - imageX * zoom, anchor.Y - imageY * zoom);
        fitted = false;
        ConstrainOrigin();
        Cursor = CanPan ? Cursors.SizeAll : Cursors.Default;
        ZoomChanged?.Invoke();
        Invalidate();
    }

    private float FitZoom() => Math.Min(Width / (float)image.Width,
        Height / (float)image.Height);

    private bool CanPan => image.Width * zoom > Width + 1 ||
        image.Height * zoom > Height + 1;

    private void ConstrainOrigin()
    {
        float width = image.Width * zoom;
        float height = image.Height * zoom;
        origin.X = width <= Width ? (Width - width) / 2f
            : Math.Clamp(origin.X, Width - width, 0f);
        origin.Y = height <= Height ? (Height - height) / 2f
            : Math.Clamp(origin.Y, Height - height, 0f);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (image is null || Width <= 0 || Height <= 0) return;
        if (fitted) FitToWindow();
        else
        {
            ConstrainOrigin();
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.InterpolationMode = zoom >= 1f
            ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = zoom >= 1f
            ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(image, new RectangleF(origin.X, origin.Y,
            image.Width * zoom, image.Height * zoom));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ZoomAt((float)Math.Pow(1.2, e.Delta / 120d), e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !CanPan) return;
        dragging = true;
        lastMouse = e.Location;
        Capture = true;
        Focus();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging) return;
        origin = new PointF(origin.X + e.X - lastMouse.X,
            origin.Y + e.Y - lastMouse.Y);
        lastMouse = e.Location;
        ConstrainOrigin();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = false;
        Capture = false;
    }
}
