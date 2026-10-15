using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\WantedRP.Launcher", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Launcher jest już uruchomiony.", "WantedRP Launcher",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        Application.Run(new MainForm(args));
    }

    static void ShowFatal(Exception? ex)
    {
        Log.Error("Unhandled exception", ex);
        MessageBox.Show(ex?.Message ?? "Nieznany błąd", "WantedRP Launcher - błąd",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

static class Log
{
    const long MaxSize = 512 * 1024;
    static readonly object gate = new();
    static readonly string file = AppPaths.Resolve("launcher.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (gate)
            {
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxSize)
                {
                    File.Copy(file, file + ".old", true);
                    File.Delete(file);
                }

                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

static class AppPaths
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WantedLauncher");

    public static string Resolve(string fileName)
    {
        try { Directory.CreateDirectory(DataDir); }
        catch { }

        string target = Path.Combine(DataDir, fileName);
        string legacy = Path.Combine(AppContext.BaseDirectory, fileName);

        try
        {
            if (!File.Exists(target) && File.Exists(legacy))
            {
                File.Copy(legacy, target);
                File.Delete(legacy);
            }
        }
        catch { }

        return target;
    }
}

static class Files
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static void WriteAtomic(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read " + Path.GetFileName(path), ex);
        }
        return null;
    }
}

class LauncherConfig
{
    static string FilePath => AppPaths.Resolve("launcher-config.json");

    public string? RedMExePath { get; set; }
    public string? type { get; set; }

    public static LauncherConfig Load() => Files.ReadJson<LauncherConfig>(FilePath) ?? new LauncherConfig();

    public void Save()
    {
        try { Files.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Files.Json)); }
        catch (Exception ex) { Log.Error("Failed to save config", ex); }
    }
}

class PendingRestore
{
    static string FilePath => AppPaths.Resolve("launcher-state.json");

    public string IniPath { get; set; } = "";
    public string? Original { get; set; }

    public static PendingRestore? Load() => Files.ReadJson<PendingRestore>(FilePath);

    public void Save()
    {
        try { Files.WriteAtomic(FilePath, JsonSerializer.Serialize(this, Files.Json)); }
        catch (Exception ex) { Log.Error("Failed to save restore state", ex); }
    }

    public static void Delete()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) { Log.Error("Failed to delete restore state", ex); }
    }
}

static class CitizenIni
{
    public static string? ReadChannel(string path)
    {
        if (!File.Exists(path)) return null;

        string section = "";
        foreach (var line in File.ReadAllLines(path))
        {
            if (TryParseSection(line, out var name))
            {
                section = name;
                continue;
            }

            if (section == "game" && IsChannelLine(line))
                return line.Substring(line.IndexOf('=') + 1).Trim();
        }
        return null;
    }

    public static void WriteChannel(string path, string? value)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var result = new List<string>();
        string section = "";
        bool written = false, hasGame = false;

        foreach (var line in lines)
        {
            if (TryParseSection(line, out var name))
            {
                if (section == "game" && !written && value != null)
                {
                    result.Add("UpdateChannel=" + value);
                    written = true;
                }

                section = name;
                hasGame |= section == "game";
                result.Add(line);
                continue;
            }

            if (section == "game" && IsChannelLine(line))
            {
                if (!written && value != null)
                {
                    result.Add("UpdateChannel=" + value);
                    written = true;
                }
                continue;
            }

            result.Add(line);
        }

        if (!written && value != null)
        {
            if (!hasGame) result.Add("[Game]");
            result.Add("UpdateChannel=" + value);
        }

        Files.WriteAtomic(path, string.Join(Environment.NewLine, result) + Environment.NewLine);
    }

    static bool TryParseSection(string line, out string name)
    {
        string t = line.Trim();
        if (t.StartsWith("[") && t.EndsWith("]"))
        {
            name = t.Substring(1, t.Length - 2).Trim().ToLowerInvariant();
            return true;
        }
        name = "";
        return false;
    }

    static bool IsChannelLine(string line)
    {
        int eq = line.IndexOf('=');
        return eq > 0 && line.Substring(0, eq).Trim()
            .Equals("UpdateChannel", StringComparison.OrdinalIgnoreCase);
    }
}

public class MainForm : Form
{
    const string UpdateChannel = "specific/feature/redm-physical-player-extension";
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 2;

    static string gameRoot = "";
    static readonly int mySession = Process.GetCurrentProcess().SessionId;

    readonly WebView2 web = new();
    readonly string[] args;
    readonly CancellationTokenSource cts = new();
    readonly object restoreLock = new();
    string? iniPath;
    string? originalChannel;
    bool restorePending;

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    public MainForm(string[] args)
    {
        this.args = args ?? Array.Empty<string>();

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(525, 525);
        BackColor = Color.FromArgb(0x20, 0x20, 0x20);
        KeyPreview = true;
        Text = "WantedRP Launcher";

        try
        {
            var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (icon != null) Icon = icon;
        }
        catch { }

        web.Dock = DockStyle.Fill;
        Controls.Add(web);

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Load += async (_, _) => await StartAsync();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && AnyRunning())
            {
                var result = MessageBox.Show(this,
                    "Zamknięcie launchera wyłączy grę siłowo.\n\n" +
                    "Twoja postać zostanie na serwerze jeszcze przez ok. 3 minuty. " +
                    "Nie zaleca się tego robić.\n\n" +
                    "Zamiast tego zamknij grę normalnie z poziomu RedM.\n\n" +
                    "Czy na pewno chcesz zamknąć launcher i wyłączyć grę?",
                    "WantedRP Launcher - ostrzeżenie",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes)
                {
                    Log.Info("Close cancelled by user");
                    e.Cancel = true;
                    return;
                }
            }

            Log.Info("Window closing");
            cts.Cancel();
            KillGame();
            RestoreChannel();
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRoundedCorners();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated) ApplyRoundedCorners();
    }

    void ApplyRoundedCorners()
    {
        Region?.Dispose();
        Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 12, 12));
    }

    async Task StartAsync()
    {
        Log.Info("Launcher started, args: " + string.Join(" ", args));

        try
        {
            string userData = Path.Combine(AppPaths.DataDir, "WebView2");

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init failed", ex);
            MessageBox.Show("Brak WebView2 Runtime.\n" + ex.Message, "Błąd",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var settings = web.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        web.DefaultBackgroundColor = Color.FromArgb(0x20, 0x20, 0x20);

        web.CoreWebView2.WebMessageReceived += OnWebMessage;
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "wanted.local", AppContext.BaseDirectory,
            CoreWebView2HostResourceAccessKind.Allow);

        string html = LoadUiHtml();

        var navigated = new TaskCompletionSource();
        web.NavigationCompleted += (_, _) => navigated.TrySetResult();
        web.NavigateToString(html);
        await Task.WhenAny(navigated.Task, Task.Delay(5000));

        try
        {
            await Task.Run(Run);
        }
        catch (OperationCanceledException)
        {
            Log.Info("Cancelled");
        }
        catch (Exception ex)
        {
            Log.Error("Launcher failed", ex);
            SetUi("Błąd", ex.Message, 100);
        }
        finally
        {
            RestoreChannel();
        }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        switch (message)
        {
            case "close":
                Close();
                break;
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "drag":
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                break;
        }
    }

    static string LoadUiHtml()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "ui.html");
        if (File.Exists(path)) return File.ReadAllText(path);

        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ui.html");
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        Log.Warn("ui.html not found");
        return "<body style='background:#202020;color:#ABABAB;font-family:sans-serif'>Not Found</body>";
    }

    void RunJs(string js)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!IsDisposed && !web.IsDisposed && web.CoreWebView2 != null)
                        _ = web.CoreWebView2.ExecuteScriptAsync(js);
                }
                catch { }
            }));
        }
        catch { }
    }

    void SetUi(string title, string sub, double? percent)
    {
        Log.Info($"Status: {title} - {sub}" + (percent.HasValue ? $" ({percent}%)" : ""));

        string js = $"setStatus({JsonSerializer.Serialize(title)}, {JsonSerializer.Serialize(sub)});";
        if (percent.HasValue)
            js += $" setProgress({percent.Value.ToString(CultureInfo.InvariantCulture)});";
        RunJs(js);
    }

    void SetLoading(bool on) => RunJs($"setLoading({(on ? "true" : "false")});");

    void CloseSafe()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(Close)); }
        catch { }
    }

    bool Wait(int ms) => !cts.Token.WaitHandle.WaitOne(ms);

    void Run()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        SetUi("Start", "Szukanie RedM.exe...", 5);
        string? exePath = FindExe(localAppData);
        if (exePath == null)
        {
            Log.Error("RedM.exe not found");
            SetUi("Błąd", "Nie znaleziono RedM.exe", 100);
            if (Wait(2500)) CloseSafe();
            return;
        }
        Log.Info("Using " + exePath);
        gameRoot = Path.GetDirectoryName(exePath)!;

        string ini = Path.Combine(gameRoot, "RedM.app", "CitizenFX.ini");
        var config = LauncherConfig.Load();
#if DEBUG
        bool dev = true;
#else
        bool dev = HasArg("--dev") || IsType(config, "development");
#endif
#if CREATIVE
        bool creative = true;
#else
        bool creative = HasArg("--creative") || IsType(config, "creative");
#endif    
        string mode = dev ? "development" : creative ? "creative" : "production";
        string server = dev ? "wantedrp.pl:30690" : creative ? "wantedrp.pl:30420" : "wantedrp.pl";
        Log.Info($"Mode: {mode}, server: {server}");
        SetUi("Konfiguracja", $"Tryb: {mode}, serwer: {server}", 10);

        RecoverAfterCrash();
        cts.Token.ThrowIfCancellationRequested();

        if (AnyRunning())
        {
            Log.Warn("RedM is already running, closing it");
            SetUi("Zamykanie", "Zamykanie działającego RedM...", 12);
            if (!KillGame())
            {
                Log.Error("Could not close running RedM");
                SetUi("Błąd", "Nie udało się zamknąć działającego RedM", 100);
                if (Wait(3000)) CloseSafe();
                return;
            }
            SetUi("Zamykanie", "Poprzednia instancja zamknięta", 15);
        }

        string? original = CitizenIni.ReadChannel(ini);
        new PendingRestore { IniPath = ini, Original = original }.Save();
        lock (restoreLock)
        {
            iniPath = ini;
            originalChannel = original;
            restorePending = true;
        }
        Log.Info("Original channel: " + (original ?? "(none)"));

        SetUi("Kanał aktualizacji", "Ustawianie kanału...", 20);
        CitizenIni.WriteChannel(ini, UpdateChannel);

        bool launched = false;
        try
        {
            SetUi("Trwa łączenie", "Uruchamianie RedM...", 35);
            SetLoading(true);

            using var process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Arguments = "+connect " + server,
                WorkingDirectory = gameRoot,
            });
            Log.Info(process == null ? "Process.Start returned null" : "Started PID " + process.Id);

            SetUi("Trwa łączenie", "Czekam na proces RedM...", 50);
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !AnyRunning())
                if (!Wait(500)) return;

            if (!AnyRunning())
            {
                Log.Error("RedM did not start within 60s");
                SetLoading(false);
                SetUi("Błąd", "RedM nie wystartował w ciągu 60 s", 100);
            }
            else
            {
                launched = true;
                if (!WaitForGameWindow()) return;

                SetLoading(false);
                SetUi("Połączono", "Trwa gra, zamknięcie okna wyłączy RedM", 100);
                WaitForExit();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Launch failed", ex);
            SetLoading(false);
            SetUi("Błąd", ex.Message, 100);
        }
        finally
        {
            SetUi("Sprzątanie", "Przywracanie ustawień kanału...", null);
            RestoreChannel();
        }

        if (launched) SetUi("Gotowe", "Przywrócono ustawienia", 100);
        if (Wait(800)) CloseSafe();
    }

    bool WaitForGameWindow()
    {
        var started = DateTime.UtcNow;
        var lastDump = DateTime.MinValue;
        DateTime? missingSince = null;
        string current = "";

        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(120))
        {
            if (GameHasWindow())
            {
                Log.Info("Game window detected: " + Describe(IsGameProcess));
                return true;
            }

            if (DateTime.UtcNow - lastDump >= TimeSpan.FromSeconds(10))
            {
                lastDump = DateTime.UtcNow;
                Log.Info("Waiting for game, related processes: " + Describe(LooksRelated));
            }

            string next = current;
            double percent = 0;

            if (IsGameRunning())
            {
                next = "Ładowanie gry";
                percent = 85;
            }
            else if (IsRunning("RedM"))
            {
                next = "Uruchamianie RedM";
                percent = 60;
            }

            if (!AnyRunning())
            {
                missingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - missingSince.Value >= TimeSpan.FromSeconds(3))
                {
                    Log.Warn("Game process disappeared during startup");
                    return true;
                }
            }
            else
            {
                missingSince = null;
            }

            if (next != current && percent > 0)
            {
                current = next;
                SetUi("Trwa łączenie", next + "...", percent);
            }

            if (!Wait(500)) return false;
        }

        Log.Warn("Game window not detected within 120s: " + Describe(LooksRelated));
        return true;
    }

    void WaitForExit()
    {
        const int pollMs = 500;
        const int stepMs = 250;
        const int confirmMs = 1500;

        while (true)
        {
            if (AnyRunning())
            {
                if (!Wait(pollMs)) return;
                continue;
            }

            bool reappeared = false;
            var until = DateTime.UtcNow.AddMilliseconds(confirmMs);
            while (DateTime.UtcNow < until)
            {
                if (!Wait(stepMs)) return;
                if (AnyRunning())
                {
                    reappeared = true;
                    break;
                }
            }

            if (!reappeared)
            {
                Log.Info("Game process gone");
                return;
            }

            Log.Info("Game process reappeared, still waiting");
        }
    }

    bool HasArg(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    static bool IsType(LauncherConfig config, string value) =>
        string.Equals(config.type, value, StringComparison.OrdinalIgnoreCase);

    void RestoreChannel()
    {
        string? ini, original;
        lock (restoreLock)
        {
            if (!restorePending) return;
            restorePending = false;
            ini = iniPath;
            original = originalChannel;
        }
        if (ini == null) return;

        try
        {
            CitizenIni.WriteChannel(ini, original);
            PendingRestore.Delete();
            Log.Info("Channel restored to " + (original ?? "(none)"));
        }
        catch (Exception ex)
        {
            Log.Error("Failed to restore channel", ex);
        }
    }

    void RecoverAfterCrash()
    {
        var pending = PendingRestore.Load();
        if (pending == null || string.IsNullOrEmpty(pending.IniPath)) return;

        Log.Warn("Found unfinished session, restoring " + pending.IniPath);
        SetUi("Naprawa", "Przywracanie ustawień z poprzedniej sesji...", 8);
        try
        {
            CitizenIni.WriteChannel(pending.IniPath, pending.Original);
            PendingRestore.Delete();
        }
        catch (Exception ex)
        {
            Log.Error("Recovery failed", ex);
        }
    }

    string? FindExe(string localAppData)
    {
        var config = LauncherConfig.Load();
        if (!string.IsNullOrEmpty(config.RedMExePath) && File.Exists(config.RedMExePath))
            return config.RedMExePath;

        string defaultPath = Path.Combine(localAppData, "RedM", "RedM.exe");
        if (File.Exists(defaultPath)) return defaultPath;

        SetUi("Nie znaleziono RedM.exe", "Wskaż plik RedM.exe...", 5);
        string? picked = PickExe();
        if (picked == null) return null;

        config.RedMExePath = picked;
        config.Save();
        return picked;
    }

    string? PickExe()
    {
        string? result = null;
        try
        {
            Invoke(new Action(() =>
            {
                using var dialog = new OpenFileDialog
                {
                    Title = "Wskaż plik RedM.exe",
                    Filter = "RedM (RedM.exe)|RedM.exe|Pliki wykonywalne (*.exe)|*.exe|Wszystkie pliki (*.*)|*.*",
                    CheckFileExists = true,
                };
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    result = dialog.FileName;
            }));
        }
        catch (Exception ex)
        {
            Log.Error("File dialog failed", ex);
        }
        return result;
    }

    static bool IsLauncherProcess(Process p) =>
        p.ProcessName.Equals("RedM", StringComparison.OrdinalIgnoreCase);

    static bool IsGameProcess(Process p)
    {
        string name = p.ProcessName;
        if (name.Equals("RDR2", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase))
            return true;

        if (gameRoot.Length == 0) return false;
        if (name.Contains("crash", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            if (p.SessionId != mySession) return false;

            string? file = p.MainModule?.FileName;
            if (file == null) return false;

            string appDir = Path.Combine(gameRoot, "RedM.app") + Path.DirectorySeparatorChar;
            return file.StartsWith(appDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    static bool IsAnyGame(Process p) => IsLauncherProcess(p) || IsGameProcess(p);

    static bool LooksRelated(Process p)
    {
        string n = p.ProcessName;
        return IsAnyGame(p)
            || n.Contains("redm", StringComparison.OrdinalIgnoreCase)
            || n.Contains("rdr", StringComparison.OrdinalIgnoreCase)
            || n.Contains("gta", StringComparison.OrdinalIgnoreCase)
            || n.Contains("cfx", StringComparison.OrdinalIgnoreCase)
            || n.Contains("citizen", StringComparison.OrdinalIgnoreCase);
    }

    static List<Process> Find(Func<Process, bool> match)
    {
        var result = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            bool ok = false;
            try { ok = match(p); }
            catch { }

            if (ok) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    static bool Exists(Func<Process, bool> match)
    {
        var list = Find(match);
        bool found = list.Count > 0;
        foreach (var p in list) p.Dispose();
        return found;
    }

    static bool IsRunning(string name) =>
        Exists(p => p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));

    static bool IsGameRunning() => Exists(IsGameProcess);

    static bool AnyRunning() => Exists(IsAnyGame);

    static string Describe(Func<Process, bool> match)
    {
        var list = Find(match);
        var parts = new List<string>();
        foreach (var proc in list)
        {
            bool window = false;
            try
            {
                proc.Refresh();
                window = proc.MainWindowHandle != IntPtr.Zero;
            }
            catch { }

            parts.Add($"{proc.ProcessName}[{proc.Id}]{(window ? " window" : "")}");
            proc.Dispose();
        }
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    static bool GameHasWindow()
    {
        var list = Find(IsGameProcess);
        bool found = false;
        foreach (var p in list)
        {
            try
            {
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero) found = true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return found;
    }

    static bool KillGame()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var list = Find(IsAnyGame);
            if (list.Count == 0) return true;

            foreach (var p in list)
            {
                try
                {
                    Log.Info($"Killing {p.ProcessName} (PID {p.Id})");
                    p.Kill(true);
                    p.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to kill process", ex);
                }
                finally
                {
                    p.Dispose();
                }
            }

            Thread.Sleep(300);
        }

        return !AnyRunning();
    }
}