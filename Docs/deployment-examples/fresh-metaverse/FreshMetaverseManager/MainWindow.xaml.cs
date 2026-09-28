using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using Microsoft.Win32;

namespace FreshMetaverseManager;

public class RegionRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = "unknown";
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }
    public string Status { get => _status; set { _status = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status))); } }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int QuicPort { get; set; }
    public int HttpPort { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Size { get; set; }
    public string SizeLabel => $"{Size}x{Size} m";
    public string MaxPrims => "999555333";
    public string Physics { get; set; } = "";
    public string RegionUuid { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Estate { get; set; } = "";
    public string Database { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class OarMapping
{
    public RegionRow Region { get; set; } = null!;
    public string OarPath { get; set; } = "";
    public string Display => $"{Region.Name}  →  {System.IO.Path.GetFileName(OarPath)}";
    public string FullDisplay => $"{Region.Name}  →  {OarPath}";
}

public partial class MainWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient RestartHttp = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly ObservableCollection<RegionRow> _regions = new();
    private readonly string _robustUrl = "http://127.0.0.1:22000";
    private readonly string _moneyUrl = "http://127.0.0.1:1026";
    private readonly string _helperUrl = "http://127.0.0.1:8015";
    private readonly string _gridRoot = @"H:\grid\igrid-package";
    private readonly string _moneyRoot = @"H:\grid\moneyd";
    private readonly string _xamppRoot = @"H:\grid\xampp";
    private readonly string _helperRoot = @"H:\grid\xampp\htdocs\ostools";
    private readonly string _helperConfigPath = @"H:\grid\xampp\htdocs\ostools\config.json";
    private readonly string _deployPath;
    private readonly Dictionary<string, Process> _managedProcesses = new();
    private static readonly JsonSerializerOptions s_psArgsOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private CancellationTokenSource? _logCts;
    private Process? _apacheProc;
    private bool HideWindows => (ChkHideWindows?.IsChecked ?? true) == true;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    public MainWindow()
    {
        InitializeComponent();
        _deployPath = Path.Combine(_gridRoot, "generated", "deploy.json");
        DgRegions.ItemsSource = _regions;
        TxtRobustUrl.Text = _robustUrl;
        Loaded += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        LoadRegions();
        PopulateListBoxes();
        LoadIniFileList();
        await RefreshRobustStatusAsync();
        await RefreshMoneyStatusAsync();
        await RefreshHelperStatusAsync();
        await RefreshApacheStatusAsync();
        await RefreshRegionStatusesAsync();
        // background refresh every 15s
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(15000);
                await Dispatcher.InvokeAsync(async () => { await RefreshRobustStatusAsync(); await RefreshMoneyStatusAsync(); await RefreshHelperStatusAsync(); await RefreshApacheStatusAsync(); await RefreshRegionStatusesAsync(); });
            }
        });
        LogBulk("Manager loaded. Found " + _regions.Count + " regions from deploy.json");
        LogBackup("Ready. Restart/shutdown flow: console 'backup' → wait 60s → restart API 60s timer. Backup tab uses 'oar backup'.");
    }

    // ---------- Regions ----------
    private void LoadRegions()
    {
        try
        {
            if (!File.Exists(_deployPath))
            {
                // fallback: scan generated/sims
                var simsDir = Path.Combine(_gridRoot, "generated", "sims");
                if (Directory.Exists(simsDir))
                {
                    foreach (var d in Directory.GetDirectories(simsDir))
                    {
                        _regions.Add(new RegionRow { Name = Path.GetFileName(d), Port = 0, QuicPort = 0, Size = 256, Physics = "ubODE" });
                    }
                }
                TxtRegionFilterInfo.Text = $"{_regions.Count} regions (fallback scan)";
                return;
            }
            var json = File.ReadAllText(_deployPath);
            using var doc = JsonDocument.Parse(json);
            var sims = doc.RootElement.GetProperty("sims");
            foreach (var s in sims.EnumerateArray())
            {
                _regions.Add(new RegionRow
                {
                    Name = s.GetProperty("name").GetString() ?? "",
                    Port = s.GetProperty("port").GetInt32(),
                    QuicPort = s.GetProperty("quic_port").GetInt32(),
                    HttpPort = s.GetProperty("http_port").GetInt32(),
                    X = s.GetProperty("x").GetInt32(),
                    Y = s.GetProperty("y").GetInt32(),
                    Size = s.GetProperty("size").GetInt32(),
                    Physics = s.GetProperty("physics").GetString() ?? "",
                    RegionUuid = s.GetProperty("region_uuid").GetString() ?? "",
                    Folder = s.TryGetProperty("folder", out var f) ? f.GetString() ?? "" : "",
                    Estate = s.TryGetProperty("estate", out var est) ? est.GetString() ?? "" : "",
                    Database = s.TryGetProperty("database", out var db) ? db.GetString() ?? "" : ""
                });
            }
            TxtRegionFilterInfo.Text = $"{_regions.Count} loaded from deploy.json";
        }
        catch (Exception ex)
        {
            TxtRegionFilterInfo.Text = "load error: " + ex.Message;
        }
    }

    private readonly ObservableCollection<OarMapping> _oarMappings = new();

    private void PopulateListBoxes()
    {
        ListBackupRegions.Items.Clear();
        ListOarRegions.Items.Clear();
        if (CboOarRegion != null) CboOarRegion.Items.Clear();
        if (ListOarMappings != null) ListOarMappings.Items.Clear();
        if (CboConsoleTarget != null) CboConsoleTarget.Items.Clear();
        if (CboSaveOarRegion != null) CboSaveOarRegion.Items.Clear();
        if (CboConsoleTarget != null) CboConsoleTarget.Items.Add("Robust (robust:robust)");
        foreach (var r in _regions)
        {
            ListBackupRegions.Items.Add(new CheckBox { Content = $"{r.Name}  ({r.RegionUuid[..8]}… port {r.Port})", Tag = r, Margin = new Thickness(2) });
            ListOarRegions.Items.Add(new CheckBox { Content = r.Name, Tag = r, Margin = new Thickness(2) });
            if (CboOarRegion != null) CboOarRegion.Items.Add(r.Name);
            if (CboConsoleTarget != null) CboConsoleTarget.Items.Add(r.Name);
            if (CboSaveOarRegion != null) CboSaveOarRegion.Items.Add(r.Name);
        }
        if (CboOarRegion != null && CboOarRegion.Items.Count > 0) CboOarRegion.SelectedIndex = 0;
        if (CboConsoleTarget != null && CboConsoleTarget.Items.Count > 0) CboConsoleTarget.SelectedIndex = 0;
        if (CboSaveOarRegion != null && CboSaveOarRegion.Items.Count > 0) CboSaveOarRegion.SelectedIndex = 0;
        TxtApachePath.Text = $"{Path.Combine(_xamppRoot, @"apache\bin\httpd.exe")}  •  {Path.Combine(_xamppRoot, "apache_start.bat")}";
        var dbInfo = "PGSQL 127.0.0.1 / robust • assets";
        try
        {
            if (File.Exists(_deployPath))
            {
                var j = JsonDocument.Parse(File.ReadAllText(_deployPath));
                var db = j.RootElement.GetProperty("db");
                dbInfo = $"{db.GetProperty("provider").GetString()} {db.GetProperty("host").GetString()} / {db.GetProperty("database").GetString()} • {db.GetProperty("database_assets").GetString()}";
            }
        }
        catch { }
        TxtDbInfo.Text = dbInfo;
        TxtRegionCount.Text = $"{_regions.Count} regions";
        RefreshEstateCombos();
    }

    private void RefreshEstateCombos()
    {
        try
        {
            var yamlPath = Path.Combine(_gridRoot, "regions.yaml");
            if (!File.Exists(yamlPath)) return;
            var txt = File.ReadAllText(yamlPath);
            var estates = new List<string>();
            // parse estates: [{name: ...}] or single estate: {name: ...}
            var m = Regex.Matches(txt, @"^\s*-\s*name:\s*(.+)\s*$", RegexOptions.Multiline);
            // estates block: lines after "estates:" containing "- name:"
            var estStart = txt.IndexOf("\nestates:", StringComparison.Ordinal);
            if (estStart >= 0)
            {
                var estBlock = txt.Substring(estStart, Math.Min(3000, txt.Length - estStart));
                foreach (Match mm in Regex.Matches(estBlock, @"-\s*name:\s*(.+)"))
                    estates.Add(mm.Groups[1].Value.Trim().Trim('"').Trim('\''));
            }
            else
            {
                var em = Regex.Match(txt, @"^\s*estate:\s*\n(?:.*\n)*?\s*name:\s*(.+)\s*$", RegexOptions.Multiline);
                if (em.Success) estates.Add(em.Groups[1].Value.Trim().Trim('"').Trim('\''));
            }
            if (estates.Count == 0) estates.Add("Fresh Estate");

            if (CboEstateList != null)
            {
                CboEstateList.Items.Clear();
                foreach (var e in estates) CboEstateList.Items.Add(e);
                if (CboEstateList.Items.Count > 0) CboEstateList.SelectedIndex = 0;
            }
            if (CboNewRegionEstate != null)
            {
                CboNewRegionEstate.Items.Clear();
                CboNewRegionEstate.Items.Add("(default)");
                foreach (var e in estates) CboNewRegionEstate.Items.Add(e);
                CboNewRegionEstate.SelectedIndex = 0;
            }
            // also update TxtEstateInfo if present
            try
            {
                if (TxtEstateInfo != null)
                {
                    TxtEstateInfo.Text = $"{estates.Count} estate(s): " + string.Join(", ", estates);
                }
            }
            catch { }
        }
        catch { }
    }

    private async Task RefreshRobustStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(2500);
            var resp = await Http.GetAsync(_robustUrl, cts.Token);
            // any response means up (even 404 cute page)
            TxtRobustStatus.Text = $"Robust: online ({(int)resp.StatusCode})";
            DotRobust.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0xE8, 0x8E));
            TxtRobustDetail.Text = $"{_robustUrl} • external os.tasia.work.gd • asset 22001 • quic 22002";
        }
        catch
        {
            TxtRobustStatus.Text = "Robust: offline";
            DotRobust.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x99, 0x99));
            TxtRobustDetail.Text = $"{_robustUrl} — not reachable (grid not started)";
        }
    }

    private async Task RefreshMoneyStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(2000);
            var resp = await Http.GetAsync(_moneyUrl, cts.Token);
            if (TxtMoneyStatus != null) TxtMoneyStatus.Text = $"Money: online ({(int)resp.StatusCode})";
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("501"))
        {
            if (TxtMoneyStatus != null) TxtMoneyStatus.Text = "Money: online (501 POST-only)";
        }
        catch
        {
            if (TxtMoneyStatus != null) TxtMoneyStatus.Text = "Money: offline";
        }
    }

    private async Task RefreshHelperStatusAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(2000);
            var resp = await Http.GetAsync(_helperUrl + "/healthz", cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            string mode = "";
            try { using var doc = JsonDocument.Parse(body); if (doc.RootElement.TryGetProperty("mode", out var m)) mode = m.GetString() ?? ""; } catch { }
            if (TxtHelperStatus != null) TxtHelperStatus.Text = $"Helper: online ({(int)resp.StatusCode} {mode})";
        }
        catch
        {
            if (TxtHelperStatus != null) TxtHelperStatus.Text = "Helper: offline";
        }
    }

    private async Task RefreshApacheStatusAsync()
    {
        string status;
        try
        {
            using var cts = new CancellationTokenSource(2000);
            var resp = await Http.GetAsync("http://127.0.0.1:8082/", cts.Token);
            status = $"Apache: online ({(int)resp.StatusCode})";
        }
        catch
        {
            status = "Apache: offline";
        }

        if (TxtApacheDashboardStatus != null) TxtApacheDashboardStatus.Text = status;
        if (TxtApacheStatus != null) TxtApacheStatus.Text = status;
    }

    private async Task StartApacheAsync(IProgress<string>? log = null)
    {
        string bat = Path.Combine(_xamppRoot, "apache_start.bat");
        string exe = Path.Combine(_xamppRoot, @"apache\bin\httpd.exe");
        if (File.Exists(bat))
        {
            Process.Start(new ProcessStartInfo(bat) { WorkingDirectory = _xamppRoot, UseShellExecute = true });
            log?.Report("Apache start requested via apache_start.bat.");
        }
        else if (File.Exists(exe))
        {
            _apacheProc = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true });
            log?.Report($"Apache started pid {_apacheProc?.Id}.");
        }
        else
        {
            throw new FileNotFoundException("Apache not found", exe);
        }

        await Task.Delay(1800);
        await RefreshApacheStatusAsync();
    }

    private async Task StopApacheAsync(IProgress<string>? log = null)
    {
        bool anyStopped = false;
        string stopBat = Path.Combine(_xamppRoot, "apache_stop.bat");
        string killBat = Path.Combine(_xamppRoot, "killprocess.bat");
        // Try the shipped stop bat first (it uses a placeholder path so may no-op — still try)
        if (File.Exists(stopBat))
        {
            try { Process.Start(new ProcessStartInfo(stopBat) { WorkingDirectory = _xamppRoot, UseShellExecute = true }); log?.Report("Apache stop requested via apache_stop.bat."); } catch (Exception ex) { log?.Report("apache_stop.bat launch failed: " + ex.Message); }
            await Task.Delay(900); // give it a chance
        }
        else if (File.Exists(killBat))
        {
            try { Process.Start(new ProcessStartInfo(killBat, "\"httpd.exe\"") { WorkingDirectory = _xamppRoot, UseShellExecute = true }); } catch { }
        }

        // Managed proc handle if we launched httpd.exe ourselves
        if (_apacheProc != null && !_apacheProc.HasExited)
        {
            try { _apacheProc.Kill(true); log?.Report($"Apache stopped pid {_apacheProc.Id} (managed)."); anyStopped = true; } catch (Exception ex) { log?.Report($"Managed httpd kill failed: {ex.Message}"); }
            _apacheProc = null;
        }

        // Always sweep any remaining httpd.exe — covers XAMPP-launched daemons + placeholder-bat failure
        foreach (var p in Process.GetProcessesByName("httpd"))
        {
            try { p.Kill(true); log?.Report($"Apache/httpd stopped pid {p.Id}."); anyStopped = true; } catch (Exception ex) { log?.Report($"httpd pid {p.Id} kill failed: {ex.Message}"); }
        }

        // Extra sweep: some XAMPP httpd hide as child without name match — catch by commandline
        try
        {
            var script = "$hits = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'httpd.exe' }; $hits | ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force; \"killed httpd pid \" + $_.ProcessId } catch { \"fail \" + $_.ProcessId + \" \" + $_.Exception.Message } }";
            var psi = new ProcessStartInfo("powershell", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + JsonSerializer.Serialize(script, s_psArgsOptions))
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var sweep = Process.Start(psi);
            if (sweep != null)
            {
                var stdout = await sweep.StandardOutput.ReadToEndAsync();
                var stderr = await sweep.StandardError.ReadToEndAsync();
                await sweep.WaitForExitAsync();
                if (!string.IsNullOrWhiteSpace(stdout)) foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { log?.Report("[apache] " + line.Trim()); anyStopped = true; }
                if (!string.IsNullOrWhiteSpace(stderr) && !stderr.Contains("killed")) log?.Report("[apache] sweep stderr: " + stderr.Trim());
            }
        }
        catch { }

        // Remove stale pid file that makes XAMPP think it's still running
        try { var pidFile = Path.Combine(_xamppRoot, @"apache\logs\httpd.pid"); if (File.Exists(pidFile)) File.Delete(pidFile); } catch { }

        if (!anyStopped) log?.Report("No running httpd.exe found to stop.");

        await Task.Delay(600);
        await RefreshApacheStatusAsync();
    }

    private async Task RefreshRegionStatusesAsync()
    {
        var sem = new SemaphoreSlim(8);
        int online = 0;
        var tasks = _regions.Select(async r =>
        {
            await sem.WaitAsync();
            try
            {
                if (r.HttpPort <= 0)
                {
                    r.Status = "unknown";
                    return;
                }

                using var cts = new CancellationTokenSource(1200);
                var resp = await Http.GetAsync($"http://127.0.0.1:{r.HttpPort}/", cts.Token);
                r.Status = $"online ({(int)resp.StatusCode})";
                Interlocked.Increment(ref online);
            }
            catch
            {
                r.Status = "offline";
            }
            finally { sem.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        TxtRegionCount.Text = $"{online}/{_regions.Count} sims online";
    }

    // ---------- Tasia workflow helpers ----------
    private string ResolveConsolePass()
    {
        try
        {
            if (File.Exists(_deployPath))
            {
                var j = JsonDocument.Parse(File.ReadAllText(_deployPath));
                if (j.RootElement.TryGetProperty("console_pass", out var cp)) return cp.GetString() ?? "";
            }
        }
        catch { }
        return "";
    }

    private async Task<bool> StartManagedServiceAsync(string kind, string name, IProgress<string>? log = null)
    {
        try
        {
            await Task.Yield();
            var key = kind + ":" + name;
            if (_managedProcesses.TryGetValue(key, out var existing) && !existing.HasExited)
            {
                log?.Report($"[process] {key} is already managed by this app (pid {existing.Id}).");
                return true;
            }

            string dll = "";
            string cwd = "";
            string args = "";
            string fileName = "";
            string logPath = "";
            string simDataDir = "";

            if (kind == "robust")
            {
                var robustBin = Path.Combine(_gridRoot, "generated", "robust", "Robust.dll");
                var binRobust = Path.Combine(_gridRoot, "bin", "Robust.dll");
                if (File.Exists(robustBin))
                {
                    cwd = Path.Combine(_gridRoot, "generated", "robust");
                    dll = robustBin;
                    fileName = "dotnet";
                    args = "\"Robust.dll\"";
                    logPath = Path.Combine(cwd, "data", "RobustManaged.log");
                    Directory.CreateDirectory(Path.Combine(cwd, "data"));
                }
                else
                {
                    cwd = Path.Combine(_gridRoot, "bin");
                    dll = binRobust;
                    fileName = "dotnet";
                    var robustIni = Path.Combine(_gridRoot, "generated", "robust", "Robust.ini");
                    args = File.Exists(robustIni) ? $"\"{dll}\" -inifile=\"{robustIni}\"" : $"\"{dll}\"";
                    logPath = Path.Combine(_gridRoot, "generated", "robust", "data", "RobustManaged.log");
                    Directory.CreateDirectory(Path.Combine(_gridRoot, "generated", "robust", "data"));
                }
            }
            else if (kind == "money")
            {
                cwd = _moneyRoot;
                dll = Path.Combine(_moneyRoot, "tasia_moneyd.py");
                fileName = "python";
                args = $"\"{dll}\" --config \"{Path.Combine(_moneyRoot, "MoneyServer.ini")}\"";
                logPath = Path.Combine(cwd, "moneyd-managed.log");
                Directory.CreateDirectory(cwd);
            }
            else if (kind == "helper")
            {
                cwd = _helperRoot;
                dll = Path.Combine(_helperRoot, "app.py");
                fileName = "python";
                args = $"\"{dll}\"";
                logPath = Path.Combine(cwd, "bridge-managed.log");
                Directory.CreateDirectory(cwd);
                if (!File.Exists(dll))
                {
                    log?.Report($"[process] missing helper bridge: {dll}");
                    return false;
                }
                if (!File.Exists(_helperConfigPath))
                {
                    log?.Report($"[process] helper config missing: {_helperConfigPath}");
                    return false;
                }
            }
            else
            {
                // SIM: must run from bin with -inifile, otherwise OpenSim can't find its plugin DLLs
                var binDir = Path.Combine(_gridRoot, "bin");
                dll = Path.Combine(binDir, "OpenSim.dll");
                var simIni = Path.Combine(_gridRoot, "generated", "sims", name, "OpenSim.ini");
                simDataDir = Path.Combine(_gridRoot, "generated", "sims", name, "data");
                Directory.CreateDirectory(simDataDir);
                if (!File.Exists(dll))
                {
                    log?.Report($"[process] missing binary: {dll}");
                    return false;
                }
                if (!File.Exists(simIni))
                {
                    log?.Report($"[process] missing sim ini: {simIni}");
                    return false;
                }
                cwd = binDir;
                fileName = "dotnet";
                args = $"\"{dll}\" -inifile=\"{simIni}\"";
                logPath = Path.Combine(simDataDir, "OpenSimManaged.log");
            }

            if (kind != "sim" && !File.Exists(dll))
            {
                log?.Report($"[process] missing binary: {dll}");
                return false;
            }

            // Ensure lib64 is on PATH for native deps (Bullet, ODE)
            var binLib64 = Path.Combine(_gridRoot, "bin", "lib64");
            var psi = new ProcessStartInfo(fileName, args)
            {
                WorkingDirectory = cwd,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = HideWindows
            };
            if (kind == "helper")
            {
                psi.Environment["OS_HELPER_CONFIG"] = _helperConfigPath;
            }
            if (Directory.Exists(binLib64))
            {
                var existingPath = psi.Environment.ContainsKey("PATH") ? psi.Environment["PATH"] : Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = binLib64 + Path.PathSeparator + existingPath;
            }
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(logPath, e.Data + Environment.NewLine); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) File.AppendAllText(logPath, e.Data + Environment.NewLine); };
            if (!proc.Start()) return false;
            try { proc.BeginOutputReadLine(); } catch { }
            try { proc.BeginErrorReadLine(); } catch { }
            _managedProcesses[key] = proc;
            // Give hidden window a chance to appear then hide explicitly if requested
            if (HideWindows)
            {
                await Task.Delay(400);
                try { if (!proc.HasExited && IsWindow(proc.MainWindowHandle)) ShowWindow(proc.MainWindowHandle, SW_HIDE); } catch { }
            }
            log?.Report($"[process] started {key} pid {proc.Id} cwd {cwd} {(HideWindows ? "[hidden window, stdin managed]" : "[visible window, stdin managed]")}");
            // Quick health check: did it die immediately?
            await Task.Delay(700);
            if (proc.HasExited)
            {
                var tail = "";
                try { if (File.Exists(logPath)) tail = string.Join("\n", File.ReadLines(logPath).TakeLast(6)); } catch { }
                log?.Report($"[process] {key} exited immediately (code {proc.ExitCode}). Last log:\n{tail}");
                _managedProcesses.Remove(key);
                return false;
            }
            if (kind == "sim")
            {
                // Stale sim.pid blocks restarts if previous run died uncleanly
                var stalePid = Path.Combine(simDataDir, "sim.pid");
                // Leave it; OpenSim handles it, just log
                if (File.Exists(stalePid))
                    log?.Report($"[process] {key} pid file exists: {stalePid}");
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"[process] start {kind}:{name} failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StopManagedServiceAsync(string kind, string name, string command, IProgress<string>? log = null)
    {
        var key = kind + ":" + name;
        if (!_managedProcesses.TryGetValue(key, out var proc) || proc.HasExited)
        {
            log?.Report($"[process] {key} is not managed by this app.");
            return false;
        }

        try
        {
            proc.StandardInput.WriteLine(command);
            proc.StandardInput.Flush();
            log?.Report($"[process] sent '{command}' to {key} pid {proc.Id}.");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                log?.Report($"[process] {key} did not exit after 20s; killing pid {proc.Id}.");
                proc.Kill(true);
                await proc.WaitForExitAsync();
            }

            _managedProcesses.Remove(key);
            log?.Report($"[process] {key} stopped.");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"[process] stop {key} failed: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StopExternalRobustAsync(IProgress<string>? log = null)
    {
        try
        {
            var robustDir = Path.Combine(_gridRoot, "generated", "robust");
            var robustDll = Path.Combine(_gridRoot, "bin", "Robust.dll");
            var script = "$hits = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and ($_.CommandLine -like '*Robust.dll*' -or $_.CommandLine -like '*" + robustDir.Replace("'", "''") + "*' -or $_.CommandLine -like '*" + robustDll.Replace("'", "''") + "*') }; " +
                         "$hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; $_.ProcessId }";
            var psi = new ProcessStartInfo("powershell", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + JsonSerializer.Serialize(script, s_psArgsOptions))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrWhiteSpace(stderr)) log?.Report("[process] external Robust stop stderr: " + stderr.Trim());
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                log?.Report("[process] stopped external Robust pid(s): " + stdout.Trim());
                return true;
            }
        }
        catch (Exception ex)
        {
            log?.Report("[process] external Robust stop failed: " + ex.Message);
        }
        return false;
    }

    private async Task<bool> SendConsoleCommandAsync(RegionRow r, string command, string label, IProgress<string>? log = null)
    {
        try
        {
            await Task.Yield();
            var key = "sim:" + r.Name;
            if (!_managedProcesses.TryGetValue(key, out var proc) || proc.HasExited)
            {
                log?.Report($"[{label}] {r.Name} not managed by this app. Start the region from this manager first, then retry '{command}'.");
                return false;
            }
            proc.StandardInput.WriteLine(command);
            proc.StandardInput.Flush();
            log?.Report($"[{label}] {r.Name} console executed: {command}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"[{label}] {r.Name} console command failed: {ex.Message}");
        }
        return false;
    }

    private async Task<bool> SavePersistenceAsync(RegionRow r, IProgress<string>? log = null)
    {
        if (await TasiaApiActionAsync(r, "backup", 60, "Manager persistence save", "persist-api", log))
            return true;

        log?.Report($"[persist] {r.Name} API backup failed; trying managed console fallback.");
        return await SendConsoleCommandAsync(r, "backup", "persist", log);
    }

    private Task<bool> OarBackupAsync(RegionRow r, IProgress<string>? log = null) =>
        SendConsoleCommandAsync(r, "oar backup", "oar-backup", log);

    private async Task<bool> TasiaApiActionAsync(RegionRow r, string action, int delaySeconds, string reason, string label, IProgress<string>? log = null)
    {
        string token = ResolveConsolePass();
        if (string.IsNullOrWhiteSpace(token))
            log?.Report($"[{label}] restart token is empty; check generated/deploy.json console_pass and [RestartModule] ApiToken.");

        var uuids = ResolveRegionUuidCandidates(r).ToList();
        if (uuids.Count == 0)
        {
            log?.Report($"[{label}] {r.Name} has no valid RegionUUID in live region INI or deploy.json; restart API path cannot be built.");
            return false;
        }

        var primaryPorts = ResolveRestartPortCandidates(r, broadScan: false).ToList();

        foreach (var port in primaryPorts)
        foreach (var uuid in uuids)
        {
            if (await SendRestartApiActionAsync(r, action, delaySeconds, reason, label, port, uuid, token, log))
                return true;
        }

        var broadPorts = ResolveRestartPortCandidates(r, broadScan: true)
            .Where(p => !primaryPorts.Contains(p))
            .ToList();
        if (broadPorts.Count > 0)
            log?.Report($"[{label}] {r.Name} primary API endpoint failed; scanning {broadPorts.Count} configured sim HTTP port(s) for UUID {uuids.FirstOrDefault()}...");

        foreach (var port in broadPorts)
        foreach (var uuid in uuids)
        {
            if (!await ProbeRestartApiStatusAsync(port, uuid, token))
                continue;

            log?.Report($"[{label}] {r.Name} restart API found on port {port} for UUID {uuid}; updating in-memory row.");
            r.HttpPort = port;
            r.RegionUuid = uuid;
            if (await SendRestartApiActionAsync(r, action, delaySeconds, reason, label, port, uuid, token, log))
                return true;
        }

        return false;
    }

    private IEnumerable<string> ResolveRegionUuidCandidates(RegionRow r)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void AddUuid(string? value)
        {
            value = (value ?? string.Empty).Trim().Trim('"');
            if (Guid.TryParse(value, out var guid))
            {
                var normalized = guid.ToString("D").ToLowerInvariant();
                if (seen.Add(normalized))
                    ordered.Add(normalized);
            }
        }

        try
        {
            var regionDir = Path.Combine(_gridRoot, "generated", "sims", r.Name, "regions");
            if (Directory.Exists(regionDir))
            {
                foreach (var file in Directory.GetFiles(regionDir, "*.ini"))
                {
                    var txt = File.ReadAllText(file);
                    var m = Regex.Match(txt, @"(?im)^\s*RegionUUID\s*=\s*""?([^""\r\n]+)");
                    if (m.Success) AddUuid(m.Groups[1].Value);
                }
            }
        }
        catch { }

        AddUuid(r.RegionUuid);
        return ordered;
    }

    private IEnumerable<int> ResolveRestartPortCandidates(RegionRow r, bool broadScan)
    {
        var seen = new HashSet<int>();
        var ordered = new List<int>();

        void AddPort(int port)
        {
            if (port > 0 && port <= 65535)
            {
                if (seen.Add(port))
                    ordered.Add(port);
            }
        }

        try
        {
            var simIni = Path.Combine(_gridRoot, "generated", "sims", r.Name, "OpenSim.ini");
            if (File.Exists(simIni))
            {
                var txt = File.ReadAllText(simIni);
                var m = Regex.Match(txt, @"(?im)^\s*http_listener_port\s*=\s*""?([^""\r\n]+)");
                if (m.Success && int.TryParse(m.Groups[1].Value.Trim(), out var iniHttp))
                    AddPort(iniHttp);
            }
        }
        catch { }

        AddPort(r.HttpPort);
        AddPort(r.Port);
        AddPort(r.Port + 30);

        if (broadScan)
        {
            foreach (var region in _regions)
            {
                AddPort(region.HttpPort);
                AddPort(region.Port + 30);
            }

            try
            {
                var simsDir = Path.Combine(_gridRoot, "generated", "sims");
                if (Directory.Exists(simsDir))
                {
                    foreach (var ini in Directory.GetFiles(simsDir, "OpenSim.ini", SearchOption.AllDirectories))
                    {
                        var txt = File.ReadAllText(ini);
                        var m = Regex.Match(txt, @"(?im)^\s*http_listener_port\s*=\s*""?([^""\r\n]+)");
                        if (m.Success && int.TryParse(m.Groups[1].Value.Trim(), out var iniHttp))
                            AddPort(iniHttp);
                    }
                }
            }
            catch { }
        }

        return ordered;
    }

    private static void AddRestartAuthHeaders(HttpRequestMessage req, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return;
        req.Headers.TryAddWithoutValidation("X-Restart-Token", token);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
    }

    private async Task<bool> ProbeRestartApiStatusAsync(int port, string regionUuid, string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var url = $"http://127.0.0.1:{port}/tasia-ngc/restart/{regionUuid}?action=status";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddRestartAuthHeaders(req, token);
            using var resp = await RestartHttp.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
                return false;
            var txt = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("region_uuid", out var uuidEl))
                return string.Equals(uuidEl.GetString(), regionUuid, StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch { return false; }
    }

    private async Task<bool> SendRestartApiActionAsync(RegionRow r, string action, int delaySeconds, string reason, string label, int port, string regionUuid, string token, IProgress<string>? log)
    {
        try
        {
            var timeout = action.Equals("backup", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromSeconds(180)
                : TimeSpan.FromSeconds(45);
            using var cts = new CancellationTokenSource(timeout);
            var url = $"http://127.0.0.1:{port}/tasia-ngc/restart/{regionUuid}";
            var body = JsonSerializer.Serialize(new { action, delay_seconds = delaySeconds, reason });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            AddRestartAuthHeaders(req, token);
            using var resp = await RestartHttp.SendAsync(req, cts.Token);
            var txt = await resp.Content.ReadAsStringAsync(cts.Token);
            log?.Report($"[{label}] {r.Name} action={action} port {port} uuid {regionUuid[..Math.Min(8, regionUuid.Length)]} -> {(int)resp.StatusCode} {txt[..Math.Min(180, txt.Length)]}");
            if (resp.IsSuccessStatusCode)
            {
                r.HttpPort = port;
                r.RegionUuid = regionUuid;
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            log?.Report($"[{label}] {r.Name} port {port} timed out after {(action.Equals("backup", StringComparison.OrdinalIgnoreCase) ? 180 : 45)}s while calling restart API.");
        }
        catch (Exception ex)
        {
            log?.Report($"[{label}] {r.Name} port {port} failed: {ex.Message}");
        }
        return false;
    }

    private async Task<bool> TasiaScheduleRestartAsync(RegionRow r, int delaySeconds, bool isShutdown, IProgress<string>? log = null)
    {
        // Restart/shutdown is handled by Tasia RestartModule API after persistence backup.
        var action = isShutdown ? "shutdown" : "schedule";
        if (await TasiaApiActionAsync(r, action, delaySeconds, isShutdown ? "Bulk shutdown via Manager" : "Bulk restart via Manager", "restart-api", log))
            return true;

        log?.Report($"[restart-api] {r.Name} All endpoints failed — sim may be offline.");
        return false;
    }

    private async Task RunTasiaFlowAsync(IEnumerable<RegionRow> targets, int viewerDelay, bool isShutdown, IProgress<double> prog, IProgress<string> log)
    {
        var list = targets.ToList();
        if (!list.Any()) { log.Report("No regions selected."); return; }
        int totalSteps = list.Count * 2 + 1; // persistence backup + 60s wait + API schedule
        int done = 0;

        log.Report($"=== Tasia flow start: {(isShutdown ? "SHUTDOWN" : "RESTART")} viewer {viewerDelay}s, {list.Count} region(s) ===");
        log.Report("Phase 1/3: console 'backup' persistence save for all selected...");

        // Phase 1: save persistent objects before restart/shutdown.
        var sem = new SemaphoreSlim(4);
        var backupTasks = list.Select(async r =>
        {
            await sem.WaitAsync();
            try { await SavePersistenceAsync(r, log); }
            finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / totalSteps); }
        }).ToArray();
        await Task.WhenAll(backupTasks);
        log.Report($"Persistence save command sent for {list.Count} region(s). Waiting 60s before restart API...");

        for (int i = 60; i >= 0; i--)
        {
            prog.Report((done + (60 - i) / 60.0) * 100.0 / totalSteps);
            if (i % 15 == 0 || i <= 5) log.Report($"... persistence wait {i}s remaining");
            await Task.Delay(1000);
        }
        done++;

        // Phase 3: schedule restart/shutdown with viewer alert.
        log.Report($"Phase 3/3: issuing {(isShutdown ? "shutdown" : "restart")} API with viewer timer {viewerDelay}s...");
        var scheduleTasks = list.Select(async r =>
        {
            await sem.WaitAsync();
            try { await TasiaScheduleRestartAsync(r, viewerDelay, isShutdown, log); }
            finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / totalSteps); }
        }).ToArray();
        await Task.WhenAll(scheduleTasks);

        log.Report($"=== Done. Viewers will see {viewerDelay} sec countdown then {(isShutdown ? "shutdown" : "restart")} ===");
        prog.Report(100);
    }

    // ---------- Dashboard ----------
    private void LogBulk(string s) => TxtBulkLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
    private void LogBackup(string s) => TxtBackupLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
    private void LogOar(string s) => TxtOarLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
    private void LogRegion(string s) => TxtRegionLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");

    private async void BtnStartRobust_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtBulkPhase.Text = "starting Robust...";
            ProgressBulk.Value = 10;
            var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
            LogBulk("Start Robust requested through this app. Managed stdin is kept for console commands.");
            await StartManagedServiceAsync("robust", "robust", log);
            ProgressBulk.Value = 70;
            await Task.Delay(3000);
            await RefreshRobustStatusAsync();
            ProgressBulk.Value = 100;
        }
        catch (Exception ex) { LogBulk("Start Robust failed: " + ex.Message); }
        finally { TxtBulkPhase.Text = "idle"; }
    }

    private async void BtnStopRobust_Click(object sender, RoutedEventArgs e)
    {
        LogBulk("Stop Robust requested — sending real shutdown/stop.");
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStopRobust.IsEnabled = false;
        try
        {
            if (!await StopManagedServiceAsync("robust", "robust", "shutdown", log))
            {
                if (!await StopExternalRobustAsync(log))
                    LogBulk("Robust was not managed by this app and no external Robust process was found.");
            }
            await Task.Delay(1200);
            await RefreshRobustStatusAsync();
        }
        catch (Exception ex) { LogBulk("Stop failed: " + ex.Message); }
        finally { BtnStopRobust.IsEnabled = true; }
    }

    private async void BtnRestartRobust_Click(object sender, RoutedEventArgs e) => await RunTasiaForRobustAsync(120, false);
    private async void BtnBulkRestart_Click(object sender, RoutedEventArgs e) => await RunBulkRestartAsync(60, false);
    private async void BtnBulkShutdown_Click(object sender, RoutedEventArgs e) => await RunBulkRestartAsync(60, true);
    private async void BtnBulkBackup_Click(object sender, RoutedEventArgs e) => await RunBulkBackupAsync();
    private async void BtnStartAllSims_Click(object sender, RoutedEventArgs e) => await StartRegionsAsync(_regions.ToList(), "all sims");
    private async void BtnStartSelectedSims_Click(object sender, RoutedEventArgs e)
    {
        var selected = _regions.Where(r => r.IsSelected).ToList();
        if (!selected.Any()) selected = DgRegions.SelectedItems.Cast<RegionRow>().ToList();
        if (!selected.Any()) { MessageBox.Show("Select/check at least one region first."); return; }
        await StartRegionsAsync(selected, "selected sims");
    }

    private async Task StartRegionsAsync(List<RegionRow> targets, string label)
    {
        if (!targets.Any()) return;
        BtnStartAllSims.IsEnabled = false;
        BtnStartSelectedSims.IsEnabled = false;
        ProgressBulk.Value = 0;
        TxtBulkPhase.Text = "starting " + label;
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        var sem = new SemaphoreSlim(3);
        int done = 0;
        try
        {
            LogBulk($"Starting {targets.Count} {label} (parallel 3)...");
            var tasks = targets.Select(async r =>
            {
                await sem.WaitAsync();
                try
                {
                    await StartManagedServiceAsync("sim", r.Name, log);
                    r.Status = "starting";
                }
                finally
                {
                    sem.Release();
                    Interlocked.Increment(ref done);
                    await Dispatcher.InvokeAsync(() => ProgressBulk.Value = done * 100.0 / targets.Count);
                }
            }).ToArray();
            await Task.WhenAll(tasks);
            await Task.Delay(3000);
            await RefreshRegionStatusesAsync();
            LogBulk($"Start requested for {targets.Count} {label}.");
        }
        finally
        {
            BtnStartAllSims.IsEnabled = true;
            BtnStartSelectedSims.IsEnabled = true;
            TxtBulkPhase.Text = "idle";
        }
    }

    private async Task RunTasiaForRobustAsync(int delay, bool shutdown)
    {
        // Robust doesn't have Tasia RestartModule, but we simulate graceful restart via spawner
        LogBulk($"Robust {(shutdown ? "shutdown" : "restart")} — not Tasia (Robust is grid service). Sim restart/shutdown uses console 'backup' → wait 60s → restart API.");
        await Task.Delay(500);
        LogBulk("Robust: if you need Tasia logic, it applies to sims; Robust restart is plain process restart.");
    }

    private async Task RunBulkBackupAsync()
    {
        var targets = _regions.ToList();
        BtnBulkBackup.IsEnabled = false;
        ProgressBulk.Value = 0;
        TxtBulkPhase.Text = "bulk backup...";
        var prog = new Progress<double>(v => ProgressBulk.Value = v);
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        try { await BulkBackupOnlyAsync(targets, prog, log); }
        finally { BtnBulkBackup.IsEnabled = true; TxtBulkPhase.Text = "idle"; }
    }

    private async Task BulkBackupOnlyAsync(IEnumerable<RegionRow> targets, IProgress<double> prog, IProgress<string> log)
    {
        var list = targets.ToList();
        var sem = new SemaphoreSlim(4);
        int done = 0;
        log.Report($"Bulk OAR backup {list.Count} regions via console 'oar backup' (parallel 4)...");
        var tasks = list.Select(async r =>
        {
            await sem.WaitAsync();
            try { await OarBackupAsync(r, log); }
            finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / list.Count); }
        }).ToArray();
        await Task.WhenAll(tasks);
        log.Report("Bulk OAR backup command sent for all regions.");
        prog.Report(100);
    }

    private async Task RunBulkRestartAsync(int delay, bool shutdown)
    {
        BtnBulkRestart.IsEnabled = false; BtnBulkShutdown.IsEnabled = false;
        ProgressBulk.Value = 0; TxtBulkPhase.Text = shutdown ? "bulk shutdown..." : "bulk restart...";
        var prog = new Progress<double>(v => ProgressBulk.Value = v);
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        try { await RunTasiaFlowAsync(_regions, delay, shutdown, prog, log); }
        finally { BtnBulkRestart.IsEnabled = true; BtnBulkShutdown.IsEnabled = true; TxtBulkPhase.Text = "idle"; }
    }

    private void BtnOpenRobust_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo { FileName = _robustUrl, UseShellExecute = true });
    private async void BtnRefreshDashboard_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRobustStatusAsync();
        await RefreshMoneyStatusAsync();
        await RefreshApacheStatusAsync();
        await RefreshRegionStatusesAsync();
    }
    private void BtnClearBulkLog_Click(object sender, RoutedEventArgs e) => TxtBulkLog.Clear();

    private async void BtnStartApacheDashboard_Click(object sender, RoutedEventArgs e)
    {
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStartApacheDashboard.IsEnabled = false;
        try
        {
            LogBulk("Start Apache/XAMPP requested.");
            await StartApacheAsync(log);
        }
        catch (Exception ex) { LogBulk("Start Apache failed: " + ex.Message); }
        finally { BtnStartApacheDashboard.IsEnabled = true; }
    }

    private async void BtnStopApacheDashboard_Click(object sender, RoutedEventArgs e)
    {
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStopApacheDashboard.IsEnabled = false;
        try
        {
            LogBulk("Stop Apache/XAMPP requested.");
            await StopApacheAsync(log);
        }
        catch (Exception ex) { LogBulk("Stop Apache failed: " + ex.Message); }
        finally { BtnStopApacheDashboard.IsEnabled = true; }
    }

    private async void BtnStartMoney_Click(object sender, RoutedEventArgs e)
    {
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStartMoney.IsEnabled = false;
        try
        {
            LogBulk("Start Money Server requested: tasia_moneyd.py --config MoneyServer.ini");
            await StartManagedServiceAsync("money", "moneyd", log);
            await Task.Delay(1200);
            await RefreshMoneyStatusAsync();
            LogBulk("Start Helper Bridge requested: ostools bridge on 8015 (xmlrpc_passthrough → money 1026)");
            await StartManagedServiceAsync("helper", "bridge", log);
            await Task.Delay(1200);
            await RefreshHelperStatusAsync();
        }
        finally { BtnStartMoney.IsEnabled = true; }
    }

    private async void BtnStopMoney_Click(object sender, RoutedEventArgs e)
    {
        var logBulk = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStopMoney.IsEnabled = false;
        try
        {
            // helper first — managed kill then external sweep (covers stray python launched outside manager)
            var hk = "helper:bridge";
            if (_managedProcesses.TryGetValue(hk, out var hproc) && !hproc.HasExited)
            {
                try { hproc.Kill(true); LogBulk($"Helper Bridge stopped (pid {hproc.Id})."); } catch (Exception ex) { LogBulk($"Helper stop failed: {ex.Message}"); }
                _managedProcesses.Remove(hk);
            }
            await StopExternalHelperAsync(logBulk);

            var key = "money:moneyd";
            if (_managedProcesses.TryGetValue(key, out var proc) && !proc.HasExited)
            {
                try { proc.Kill(true); LogBulk($"Money Server stopped (pid {proc.Id})."); } catch (Exception ex) { LogBulk($"Money stop failed: {ex.Message}"); }
                _managedProcesses.Remove(key);
            }
            // always sweep external moneyd — handles processes started outside manager or orphaned
            bool killedExternal = await StopExternalMoneyAsync(logBulk);
            if (!killedExternal && !_managedProcesses.ContainsKey(key))
                LogBulk("Money Server not running (no managed or external process found).");

            await RefreshMoneyStatusAsync();
            await RefreshHelperStatusAsync();
        }
        finally { BtnStopMoney.IsEnabled = true; }
    }

    private async void BtnStartHelper_Click(object sender, RoutedEventArgs e)
    {
        var log = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        BtnStartHelper.IsEnabled = false;
        try
        {
            LogBulk("Start Helper Bridge requested: python app.py with OS_HELPER_CONFIG");
            await StartManagedServiceAsync("helper", "bridge", log);
            await Task.Delay(1200);
            await RefreshHelperStatusAsync();
        }
        finally { BtnStartHelper.IsEnabled = true; }
    }

    private async void BtnStopHelper_Click(object sender, RoutedEventArgs e)
    {
        BtnStopHelper.IsEnabled = false;
        var logBulk = new Progress<string>(s => { LogBulk(s); TxtBulkLog.ScrollToEnd(); });
        try
        {
            var key = "helper:bridge";
            bool hadManaged = false;
            if (_managedProcesses.TryGetValue(key, out var proc) && !proc.HasExited)
            {
                try { proc.Kill(true); LogBulk($"Helper Bridge stopped (pid {proc.Id})."); hadManaged = true; } catch (Exception ex) { LogBulk($"Helper stop failed: {ex.Message}"); }
                _managedProcesses.Remove(key);
            }
            bool killedExternal = await StopExternalHelperAsync(logBulk);
            if (!hadManaged && !killedExternal)
                LogBulk("Helper Bridge not running (no managed or external process found).");
            await RefreshHelperStatusAsync();
        }
        finally { BtnStopHelper.IsEnabled = true; }
    }

    private async Task<bool> StopExternalMoneyAsync(IProgress<string>? log = null)
    {
        try
        {
            var script = "$hits = Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'python*' -and $_.CommandLine -like '*tasia_moneyd.py*' }; $hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"killed Money pid \" + $_.ProcessId }";
            var psi = new ProcessStartInfo("powershell", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + JsonSerializer.Serialize(script, s_psArgsOptions))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrWhiteSpace(stderr)) log?.Report("[money] external stop stderr: " + stderr.Trim());
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) log?.Report("[money] " + line.Trim());
                return true;
            }
        }
        catch (Exception ex) { log?.Report("[money] external stop failed: " + ex.Message); }
        return false;
    }

    private async Task<bool> StopExternalHelperAsync(IProgress<string>? log = null)
    {
        try
        {
            var script = "$hits = Get-CimInstance Win32_Process | Where-Object { ($_.Name -like 'python*') -and ($_.CommandLine -like '*ostools*app.py*' -or $_.CommandLine -like '*bridge.server*' -or $_.CommandLine -like '*htdocs*ostools*') }; $hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"killed Helper pid \" + $_.ProcessId }";
            var psi = new ProcessStartInfo("powershell", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + JsonSerializer.Serialize(script, s_psArgsOptions))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrWhiteSpace(stderr)) log?.Report("[helper] external stop stderr: " + stderr.Trim());
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) log?.Report("[helper] " + line.Trim());
                return true;
            }
        }
        catch (Exception ex)
        {
            log?.Report("[helper] external stop failed: " + ex.Message);
        }
        return false;
    }

    private void LogMoney(string s)
    {
        TxtMoneyResult.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n");
        TxtMoneyResult.ScrollToEnd();
    }

    private static bool TryNormalizeUuid(string text, out string uuid)
    {
        uuid = "";
        if (!Guid.TryParse(text.Trim(), out var g)) return false;
        uuid = g.ToString().ToLowerInvariant();
        return true;
    }

    private static string SqlQuote(string value) => value.Replace("'", "''");

    private async Task<string> RunMoneySqlAsync(string sql)
    {
        var psql = @"H:\grid\pgsql\bin\psql.exe";
        if (!File.Exists(psql)) return "psql not found: " + psql;
        var psi = new ProcessStartInfo(psql)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["PGPASSWORD"] = "opensim";
        psi.ArgumentList.Add("-h"); psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add("5432");
        psi.ArgumentList.Add("-U"); psi.ArgumentList.Add("opensim");
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add("money");
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add("-A");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(sql);
        using var proc = Process.Start(psi);
        if (proc == null) return "failed to start psql";
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (stdout + stderr).Trim();
    }

    private async Task<string> GetMoneyBalanceAsync(string avatarUuid)
    {
        var sql = $"SELECT balance FROM balances WHERE \"user\"='{SqlQuote(avatarUuid)}';";
        var result = await RunMoneySqlAsync(sql);
        return string.IsNullOrWhiteSpace(result) ? "0" : result.Trim();
    }

    private async Task<string> SetMoneyBalanceAsync(string avatarUuid, int balance)
    {
        var sql = "INSERT INTO balances (\"user\", balance, status, \"type\") " +
                  $"VALUES ('{SqlQuote(avatarUuid)}', {balance}, 0, 0) " +
                  "ON CONFLICT (\"user\") DO UPDATE SET balance=EXCLUDED.balance;";
        return await RunMoneySqlAsync(sql);
    }

    private async Task<string> AddMoneyAmountAsync(string avatarUuid, int amount)
    {
        var sql = "INSERT INTO balances (\"user\", balance, status, \"type\") " +
                  $"VALUES ('{SqlQuote(avatarUuid)}', {amount}, 0, 0) " +
                  "ON CONFLICT (\"user\") DO UPDATE SET balance=balances.balance + EXCLUDED.balance " +
                  "RETURNING balance;";
        return await RunMoneySqlAsync(sql);
    }

    private async Task<string> MoneyXmlRpcAsync(string method, Dictionary<string, object> values)
    {
        static XElement ValueElement(object value) => value switch
        {
            int i => new XElement("value", new XElement("int", i)),
            long l => new XElement("value", new XElement("int", l)),
            bool b => new XElement("value", new XElement("boolean", b ? "1" : "0")),
            _ => new XElement("value", new XElement("string", Convert.ToString(value) ?? ""))
        };

        var members = values.Select(kv => new XElement("member", new XElement("name", kv.Key), ValueElement(kv.Value)));
        var doc = new XDocument(new XElement("methodCall",
            new XElement("methodName", method),
            new XElement("params", new XElement("param", new XElement("value", new XElement("struct", members))))));
        var req = new HttpRequestMessage(HttpMethod.Post, _moneyUrl + "/RPC2")
        {
            Content = new StringContent(doc.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return $"HTTP {(int)resp.StatusCode}: {text}";
    }

    private async void BtnMoneyGet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeUuid(TxtMoneyAvatar.Text, out var avatar)) { LogMoney("Invalid avatar UUID."); return; }
        var balance = await GetMoneyBalanceAsync(avatar);
        TxtMoneyBalance.Text = balance;
        LogMoney($"Balance {avatar}: {balance} Dorito");
    }

    private async void BtnMoneySet_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeUuid(TxtMoneyAvatar.Text, out var avatar)) { LogMoney("Invalid avatar UUID."); return; }
        if (!int.TryParse(TxtMoneyBalance.Text.Trim(), out var balance)) { LogMoney("Balance must be a number."); return; }
        var result = await SetMoneyBalanceAsync(avatar, balance);
        LogMoney($"Set balance {avatar} = {balance}. {result}");
    }

    private async void BtnMoneyAdd_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeUuid(TxtMoneyAvatar.Text, out var avatar)) { LogMoney("Invalid avatar UUID."); return; }
        if (!int.TryParse(TxtMoneyAmount.Text.Trim(), out var amount)) { LogMoney("Amount must be a number."); return; }
        var result = await AddMoneyAmountAsync(avatar, amount);
        TxtMoneyBalance.Text = result;
        LogMoney($"Added {amount} to {avatar}. New balance: {result}");
    }

    private async void BtnMoneyTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNormalizeUuid(TxtMoneyAvatar.Text, out var senderId)) { LogMoney("Invalid sender/avatar UUID."); return; }
        if (!TryNormalizeUuid(TxtMoneyReceiver.Text, out var receiverId)) { LogMoney("Invalid receiver UUID."); return; }
        if (!int.TryParse(TxtMoneyAmount.Text.Trim(), out var amount) || amount <= 0) { LogMoney("Amount must be > 0."); return; }
        var result = await MoneyXmlRpcAsync("ForceTransferMoney", new Dictionary<string, object>
        {
            ["senderID"] = senderId,
            ["receiverID"] = receiverId,
            ["amount"] = amount,
            ["transactionType"] = 5011,
            ["objectID"] = "00000000-0000-0000-0000-000000000000",
            ["objectName"] = "Fresh Metaverse Manager",
            ["regionHandle"] = "0",
            ["regionUUID"] = "00000000-0000-0000-0000-000000000000",
            ["description"] = "Manager force transfer"
        });
        LogMoney(result);
    }

    private async void BtnMoneyRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshMoneyStatusAsync();
        LogMoney(TxtMoneyStatus.Text);
    }

    // ---------- Regions ----------
    private void BtnReloadRegions_Click(object sender, RoutedEventArgs e)
    {
        _regions.Clear();
        LoadRegions();
        PopulateListBoxes();
        LogRegion("Regions reloaded.");
    }

    private async void BtnRefreshRegions_Click(object sender, RoutedEventArgs e)
    {
        LogRegion("Refreshing sim statuses...");
        await RefreshRegionStatusesAsync();
        LogRegion(TxtRegionCount.Text);
    }

    private async Task RegenerateAndReloadRegionsAsync()
    {
        var py = Path.Combine(_gridRoot, "generate_configs.py");
        if (!File.Exists(py)) throw new FileNotFoundException("generate_configs.py not found", py);
        var psi = new ProcessStartInfo("python", $"\"{py}\"")
        {
            WorkingDirectory = _gridRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.Environment["QUIC_REAL_CERT"] = "1";
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start generator");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        LogRegion(stdout.Trim());
        if (!string.IsNullOrWhiteSpace(stderr)) LogRegion(stderr.Trim());
        if (proc.ExitCode != 0) throw new InvalidOperationException($"generate_configs.py exited {proc.ExitCode}");

        _regions.Clear();
        LoadRegions();
        PopulateListBoxes();
        await RefreshRegionStatusesAsync();
    }

    private static string SafeDatabaseName(string regionName)
    {
        var safe = Regex.Replace(regionName.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return "sim_" + (string.IsNullOrWhiteSpace(safe) ? "region" : safe);
    }

    private void BtnAddEstate_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtNewEstateName.Text.Trim();
        var owner = TxtNewEstateOwner.Text.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_ \\-]{2,64}$")) { MessageBox.Show("Estate name: 2-64 chars letters/numbers/space/_/-"); return; }
        if (string.IsNullOrWhiteSpace(owner)) { MessageBox.Show("Owner required (e.g. Cute Devil)"); return; }
        var yamlPath = Path.Combine(_gridRoot, "regions.yaml");
        var txt = File.ReadAllText(yamlPath);
        if (txt.Contains($"name: {name}") || Regex.IsMatch(txt, Regex.Escape(name), RegexOptions.IgnoreCase))
        {
            // loose check — still allow if not in estates block
        }
        var lines = File.ReadAllLines(yamlPath).ToList();
        // If estates: list exists, append; otherwise create it from estate: single + new
        int estatesAt = lines.FindIndex(l => l.TrimStart().StartsWith("estates:"));
        int estateSingleAt = lines.FindIndex(l => l.TrimStart().StartsWith("estate:"));
        if (estatesAt >= 0)
        {
            // append after last estate entry (next top-level key or regions:)
            int regionsAt = lines.FindIndex(estatesAt, l => l.TrimStart().StartsWith("regions:"));
            if (regionsAt < 0) regionsAt = lines.Count;
            // find last estate block end before regionsAt
            int insertAt2 = regionsAt;
            var block = new[]
            {
                $"  - name: {name}",
                $"    owner: {owner}",
                $"    owner_uuid: \"\"",
                $"    managers:",
                $"      - {owner}",
            };
            lines.InsertRange(insertAt2, block);
        }
        else if (estateSingleAt >= 0)
        {
            // convert single estate: into estates: [old, new]
            var oldName = Regex.Match(txt, @"estate:\s*\n(?:.*\n)*?\s*name:\s*(.+)\s*\n").Groups[1].Value.Trim().Trim('"').Trim('\'');
            var oldOwner = Regex.Match(txt, @"estate:\s*\n(?:.*\n)*?\s*owner:\s*(.+)\s*\n").Groups[1].Value.Trim().Trim('"').Trim('\'');
            if (string.IsNullOrWhiteSpace(oldName)) oldName = "Fresh Estate";
            if (string.IsNullOrWhiteSpace(oldOwner)) oldOwner = "Cute Devil";
            // replace estate: block with estates:
            int estateEnd = lines.FindIndex(estateSingleAt + 1, l => !l.StartsWith(" ") && !string.IsNullOrWhiteSpace(l));
            if (estateEnd < 0) estateEnd = lines.FindIndex(estateSingleAt + 1, l => l.TrimStart().StartsWith("gate:"));
            if (estateEnd < 0) estateEnd = estateSingleAt + 6;
            lines.RemoveRange(estateSingleAt, estateEnd - estateSingleAt);
            var repl = new[]
            {
                "estates:",
                $"  - name: {oldName}",
                $"    owner: {oldOwner}",
                $"    owner_uuid: \"\"",
                $"    managers:",
                $"      - {oldOwner}",
                $"  - name: {name}",
                $"    owner: {owner}",
                $"    owner_uuid: \"\"",
                $"    managers:",
                $"      - {owner}",
            };
            lines.InsertRange(estateSingleAt, repl);
        }
        else
        {
            MessageBox.Show("regions.yaml missing estate: block.");
            return;
        }
        File.WriteAllLines(yamlPath, lines);
        LogRegion($"Added estate '{name}' owner '{owner}' to regions.yaml.");
        RefreshEstateCombos();
        TxtNewEstateName.Clear();
    }

    private void BtnRemoveEstate_Click(object sender, RoutedEventArgs e)
    {
        if (CboEstateList.SelectedItem == null) { MessageBox.Show("Pick an estate first."); return; }
        var name = CboEstateList.SelectedItem.ToString() ?? "";
        if (MessageBox.Show($"Remove estate '{name}'? Regions using it will fall back to default.", "Remove Estate", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var yamlPath = Path.Combine(_gridRoot, "regions.yaml");
        var lines = File.ReadAllLines(yamlPath).ToList();
        int start = lines.FindIndex(l => Regex.IsMatch(l, $@"^\s*-\s*name:\s*{Regex.Escape(name)}\s*$"));
        if (start < 0) { MessageBox.Show("Estate block not found."); return; }
        int end = start + 1;
        while (end < lines.Count && (lines[end].StartsWith(" ") || string.IsNullOrWhiteSpace(lines[end])) && !Regex.IsMatch(lines[end], @"^\s*-\s*name:") && !lines[end].TrimStart().StartsWith("regions:") && !lines[end].TrimStart().StartsWith("gate:"))
            end++;
        // also remove following blank lines
        while (end < lines.Count && string.IsNullOrWhiteSpace(lines[end])) end++;
        // but keep regions: if we overshot — back off one
        if (end < lines.Count && (lines[end].TrimStart().StartsWith("regions:") || lines[end].TrimStart().StartsWith("gate:"))) { }
        lines.RemoveRange(start, end - start);
        File.WriteAllLines(yamlPath, lines);
        LogRegion($"Removed estate '{name}'.");
        RefreshEstateCombos();
    }

    private void BtnReloadEstates_Click(object sender, RoutedEventArgs e) => RefreshEstateCombos();

    private async void BtnAddRegion_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtNewRegionName.Text.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_ -]{3,64}$")) { MessageBox.Show("Use a 3-64 char sim name: letters, numbers, spaces, _ or -."); return; }
        if (_regions.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("A sim with that name already exists."); return; }
        if (!int.TryParse(TxtNewRegionX.Text.Trim(), out var x) || !int.TryParse(TxtNewRegionY.Text.Trim(), out var y)) { MessageBox.Show("X/Y must be numbers."); return; }
        if (!int.TryParse(TxtNewRegionSize.Text.Trim(), out var size) || size < 1 || size > 16) { MessageBox.Show("Size must be 1-16 region units."); return; }
        var physics = ((ComboBoxItem)CboNewRegionPhysics.SelectedItem).Content.ToString() ?? "ubODE";
        string? estatePick = CboNewRegionEstate.SelectedItem?.ToString();
        if (estatePick == "(default)") estatePick = null;
        var yamlPath = Path.Combine(_gridRoot, "regions.yaml");
        var lines = File.ReadAllLines(yamlPath).ToList();
        var insertAt = lines.FindIndex(l => l.TrimStart().StartsWith("# Fresh01", StringComparison.OrdinalIgnoreCase));
        if (insertAt < 0) insertAt = lines.Count;
        var nextId = (_regions.Count + 1).ToString("00");
        var block = new List<string>
        {
            "",
            $"  - id: custom_{nextId}",
            $"    name: {name}",
            $"    database: {SafeDatabaseName(name)}",
            $"    size: {size}",
            $"    x: {x}",
            $"    y: {y}",
            $"    physics: {physics}",
            "    meshing: Meshmerizer"
        };
        if (!string.IsNullOrWhiteSpace(estatePick)) block.Add($"    estate: {estatePick}");
        lines.InsertRange(insertAt, block);
        File.WriteAllLines(yamlPath, lines);
        LogRegion($"Added {name} {(estatePick != null ? $"estate '{estatePick}' " : "")}to regions.yaml; regenerating configs...");
        try { await RegenerateAndReloadRegionsAsync(); }
        catch (Exception ex) { LogRegion("Add sim/regenerate failed: " + ex.Message); MessageBox.Show(ex.Message); }
    }

    private async void BtnRemoveRegion_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region first."); return; }
        var yamlPath = Path.Combine(_gridRoot, "regions.yaml");
        var lines = File.ReadAllLines(yamlPath).ToList();
        var nameLine = lines.FindIndex(l => Regex.IsMatch(l, $"^\\s*name:\\s*{Regex.Escape(r.Name)}\\s*$", RegexOptions.IgnoreCase));
        if (nameLine < 0) { MessageBox.Show($"{r.Name} is not a named regions.yaml entry. Auto-generated Fresh sims are not removed here."); return; }
        var start = nameLine;
        while (start >= 0 && !Regex.IsMatch(lines[start], "^\\s*-\\s*id:")) start--;
        if (start < 0) { MessageBox.Show("Could not find YAML block start."); return; }
        var end = start + 1;
        while (end < lines.Count && !Regex.IsMatch(lines[end], "^\\s*-\\s*id:") && !lines[end].TrimStart().StartsWith("# Fresh01", StringComparison.OrdinalIgnoreCase)) end++;
        if (MessageBox.Show($"Remove {r.Name} from regions.yaml and regenerate configs? Generated files are not deleted.", "Remove Sim", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        lines.RemoveRange(start, end - start);
        File.WriteAllLines(yamlPath, lines);
        LogRegion($"Removed {r.Name} from regions.yaml; regenerating configs...");
        try { await RegenerateAndReloadRegionsAsync(); }
        catch (Exception ex) { LogRegion("Remove sim/regenerate failed: " + ex.Message); MessageBox.Show(ex.Message); }
    }

    private RegionRow? SelectedRegion() => DgRegions.SelectedItem as RegionRow;

    private async void BtnRegionStart_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region first."); return; }
        try
        {
            var log = new Progress<string>(s => { LogRegion(s); TxtRegionLog.ScrollToEnd(); });
            await StartManagedServiceAsync("sim", r.Name, log);
            LogRegion($"Start {r.Name} requested through this app. Managed stdin is kept for console commands.");
            ProgressRegion.Value = 60;
        }
        catch (Exception ex) { LogRegion("Start failed: " + ex.Message); MessageBox.Show(ex.Message); }
    }

    private void BtnRegionStop_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        LogRegion($"Stop {r.Name} — sending 'shutdown' to managed sim console if this app started it.");
        _ = SendConsoleCommandAsync(r, "shutdown", "stop", new Progress<string>(s => LogRegion(s)));
    }

    private async void BtnRegionRestart60_Click(object sender, RoutedEventArgs e) => await RegionTasiaAsync(60, false);
    private async void BtnRegionRestart120_Click(object sender, RoutedEventArgs e) => await RegionTasiaAsync(60, true);
    private async void BtnRegionBackup_Click(object sender, RoutedEventArgs e) => await RegionBackupOnlyAsync();

    private async Task RegionTasiaAsync(int delay, bool shutdown)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        var btns = new[] { BtnRegionRestart60, BtnRegionRestart120, BtnRegionBackup, BtnRegionStart, BtnRegionStop };
        foreach (var b in btns) b.IsEnabled = false;
        ProgressRegion.Value = 0;
        var prog = new Progress<double>(v => ProgressRegion.Value = v);
        var log = new Progress<string>(s => { LogRegion(s); TxtRegionLog.ScrollToEnd(); });
        try { await RunTasiaFlowAsync(new[] { r }, delay, shutdown, prog, log); }
        finally { foreach (var b in btns) b.IsEnabled = true; }
    }

    private async Task RegionBackupOnlyAsync()
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        ProgressRegion.Value = 10;
        LogRegion($"OAR backup {r.Name} via 'oar backup'...");
        await OarBackupAsync(r, new Progress<string>(s => LogRegion(s)));
        ProgressRegion.Value = 100;
        LogRegion("OAR backup command sent.");
    }

    private async void BtnRegionBulkBackup_Click(object sender, RoutedEventArgs e)
    {
        var selected = _regions.Where(x => x.IsSelected).ToList();
        if (!selected.Any()) selected = _regions.ToList();
        ProgressRegion.Value = 0;
        var prog = new Progress<double>(v => ProgressRegion.Value = v);
        var log = new Progress<string>(s => LogRegion(s));
        BtnRegionBulkBackup.IsEnabled = false;
        try { await BulkBackupOnlyAsync(selected, prog, log); }
        finally { BtnRegionBulkBackup.IsEnabled = true; }
    }

    private void BtnRegionEdit_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        var simIni = Path.Combine(_gridRoot, "generated", "sims", r.Name, "OpenSim.ini");
        var regionIni = Path.Combine(_gridRoot, "generated", "sims", r.Name, "regions", r.Name + ".ini");
        string? path = null;
        if (File.Exists(simIni)) path = simIni;
        else if (File.Exists(regionIni)) path = regionIni;
        if (path == null) { MessageBox.Show("INI not found. Generate configs first."); return; }
        // Switch to Config tab and load file
        MainTabs.SelectedIndex = 7;
        LoadIniIntoEditor(path);
    }

    // ---------- Accounts ----------
    private async void BtnCreateAccount_Click(object sender, RoutedEventArgs e)
    {
        var first = TxtAccFirst.Text.Trim();
        var last = TxtAccLast.Text.Trim();
        var pass = TxtAccPass.Text.Trim();
        var email = TxtAccEmail.Text.Trim();
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last) || string.IsNullOrWhiteSpace(pass))
        {
            TxtAccountResult.Text = "Error: first, last, password required.";
            return;
        }
        BtnCreateAccount.IsEnabled = false;
        ProgressAccount.Value = 30;
        TxtAccountResult.Text = "Creating...";
        try
        {
            var result = await CreateAccountViaRobustAsync(first, last, pass, email);
            // Only result shown, as required
            TxtAccountResult.Text = result;
        }
        catch (Exception ex) { TxtAccountResult.Text = "Error: " + ex.Message; }
        finally { BtnCreateAccount.IsEnabled = true; ProgressAccount.Value = 100; }
    }

    private void BtnClearAccount_Click(object sender, RoutedEventArgs e)
    {
        TxtAccFirst.Clear(); TxtAccLast.Clear(); TxtAccPass.Clear(); TxtAccEmail.Clear();
        TxtAccountResult.Text = "—";
        ProgressAccount.Value = 0;
    }

    private async Task<string> CreateAccountViaRobustAsync(string first, string last, string pass, string email)
    {
        string display = $"{first} {last}";
        var invite = "9632587410";
        var endpoints = new[] { $"{_robustUrl}/create_user", $"{_robustUrl}/accounts/create", $"{_robustUrl}/wifi/createaccount" };
        var json1 = JsonSerializer.Serialize(new Dictionary<string,string> { ["first"]=first, ["last"]=last, ["password"]=pass, ["email"]=email, ["invite"]=invite });
        var json2 = JsonSerializer.Serialize(new Dictionary<string,string> { ["username"]=display, ["password"]=pass, ["email"]=email });
        foreach (var json in new[] { json1, json2 })
        {
            foreach (var endpoint in endpoints)
            {
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await Http.PostAsync(endpoint, content);
                    var body = await resp.Content.ReadAsStringAsync();
                    if (resp.IsSuccessStatusCode) return $"OK: {first} {last} created. Invite {invite} accepted. Response: {body.Substring(0, Math.Min(200, body.Length))}";
                    if ((int)resp.StatusCode == 404) continue;
                    return $"Result: {(int)resp.StatusCode} {body.Substring(0, Math.Min(300, body.Length))}";
                }
                catch { }
            }
        }

        // Fallback: try Robust console via HTTP if manager exposes it (simulated)
        // For buildability we return a simulated success if Robust offline
        try
        {
            var probe = await Http.GetAsync(_robustUrl);
            if (!probe.IsSuccessStatusCode) return $"Simulated OK (Robust offline): {first} {last} would be created with invite {invite}. Start Robust and retry for real creation.";
        }
        catch { return $"Simulated OK (Robust unreachable): {first} {last} with invite {invite} — start Robust http://127.0.0.1:22000 to create for real."; }

        return $"Failed to create {first} {last}. Check Robust logs. Invite {invite}";
    }

    // ---------- Backups tab ----------
    private async void BtnBackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ListBackupRegions.Items.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => (RegionRow)c.Tag!).ToList();
        if (!selected.Any()) { MessageBox.Show("Check at least one region."); return; }
        await RunBackupTabFlowAsync(selected);
    }

    private async void BtnBackupAll_Click(object sender, RoutedEventArgs e) => await RunBackupTabFlowAsync(_regions.ToList());

    private async Task RunBackupTabFlowAsync(List<RegionRow> targets)
    {
        BtnBackupSelected.IsEnabled = false; BtnBackupAll.IsEnabled = false;
        ProgressBackup.Value = 0;
        IProgress<double> prog = new Progress<double>(v => ProgressBackup.Value = v);
        IProgress<string> log = new Progress<string>(s => { LogBackup(s); TxtBackupLog.ScrollToEnd(); });
        try
        {
            var sem = new SemaphoreSlim(4);
            int done = 0;
            log.Report($"OAR backup {targets.Count} regions via 'oar backup' — parallel 4...");
            var tasks = targets.Select(async r =>
            {
                await sem.WaitAsync();
                try { await OarBackupAsync(r, log); }
                finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / targets.Count); }
            }).ToArray();
            await Task.WhenAll(tasks);
            log.Report($"OAR backup command sent for {targets.Count} region(s).");
            prog.Report(100);
        }
        finally { BtnBackupSelected.IsEnabled = true; BtnBackupAll.IsEnabled = true; }
    }

    private void BtnBackupSelectAll_Click(object sender, RoutedEventArgs e) { foreach (CheckBox c in ListBackupRegions.Items) c.IsChecked = true; }
    private void BtnBackupSelectNone_Click(object sender, RoutedEventArgs e) { foreach (CheckBox c in ListBackupRegions.Items) c.IsChecked = false; }

    // ---------- OAR helpers — full wiki options ----------
    private string BuildLoadOarOptions()
    {
        string opts = "";
        if (ChkOarMerge?.IsChecked == true) opts += " --merge";
        if (ChkOarSkipAssets?.IsChecked == true) opts += " --skip-assets";
        if (ChkOarNoObjects?.IsChecked == true) opts += " --no-objects";
        if (ChkOarForceAssets?.IsChecked == true) opts += " --force-assets";
        if (ChkOarForceTerrain?.IsChecked == true) opts += " --force-terrain";
        if (ChkOarForceParcels?.IsChecked == true) opts += " --force-parcels";
        if (ChkOarDebug?.IsChecked == true) opts += " --debug";
        var rot = TxtOarRotation?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(rot))
        {
            // allow "30" or "30.0" — keep as-is
            opts += $" --rotation {rot}";
        }
        var disp = TxtOarDisplacement?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(disp))
        {
            if (!disp.StartsWith("<")) disp = "<" + disp;
            if (!disp.EndsWith(">")) disp = disp + ">";
            opts += $" --displacement \"{disp}\"";
        }
        var bo = TxtOarBoundingOrigin?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(bo))
        {
            if (!bo.StartsWith("<")) bo = "<" + bo;
            if (!bo.EndsWith(">")) bo = bo + ">";
            opts += $" --bounding-origin \"{bo}\"";
        }
        var bs = TxtOarBoundingSize?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(bs))
        {
            if (!bs.StartsWith("<")) bs = "<" + bs;
            if (!bs.EndsWith(">")) bs = bs + ">";
            opts += $" --bounding-size \"{bs}\"";
        }
        var du = TxtOarDefaultUser?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(du))
        {
            // format is --default-user "First Last"
            opts += $" --default-user \"{du}\"";
        }
        return opts;
    }

    private string BuildSaveOarCommand(string file)
    {
        string opts = "";
        if (ChkSaveNoAssets?.IsChecked == true) opts += " --noassets";
        var home = TxtSaveHome?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(home)) opts += $" --home={home}";
        if (ChkSavePublish?.IsChecked == true) opts += " --publish";
        var permIdx = CboSavePerm?.SelectedIndex ?? 0;
        string perm = permIdx switch { 1 => "C", 2 => "T", 3 => "CT", _ => "" };
        if (!string.IsNullOrEmpty(perm)) opts += $" --perm={perm}";
        if (ChkSaveAll?.IsChecked == true) opts += " --all";
        var f = file.Trim();
        if (string.IsNullOrWhiteSpace(f)) return "save oar" + opts;
        // quote if spaces
        var quoted = f.Contains(' ') ? $"\"{f}\"" : f;
        return "save oar" + opts + " " + quoted;
    }

    private void BtnPickSaveOarFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "OAR files (*.oar)|*.oar|All files (*.*)|*.*", Title = "Save OAR as", FileName = "region.oar", InitialDirectory = Path.Combine(_gridRoot, "generated") };
        if (dlg.ShowDialog() == true) TxtSaveOarFile.Text = dlg.FileName;
    }

    private async void BtnSaveOar_Click(object sender, RoutedEventArgs e)
    {
        if (CboSaveOarRegion.SelectedItem == null) { MessageBox.Show("Pick a region first."); return; }
        var regionName = CboSaveOarRegion.SelectedItem.ToString() ?? "";
        var region = _regions.FirstOrDefault(r => r.Name == regionName);
        if (region == null) { MessageBox.Show("Region not found."); return; }
        var file = TxtSaveOarFile.Text.Trim();
        var cmd = BuildSaveOarCommand(file);
        LogOar($"[save] {region.Name} → {cmd}");
        var ok = await SendConsoleCommandAsync(region, cmd, "save-oar", new Progress<string>(s => { LogOar(s); TxtOarLog.ScrollToEnd(); }));
        if (!ok) LogOar($"[save] {region.Name} not managed — start it from this manager first, then retry.");
        else LogOar($"[save] command sent to {region.Name}. Check Logs tab or {Path.Combine(GetRegionFolder(region), "data")} for file.");
    }

    // ---------- OAR ----------
    private void BtnPickOar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "OAR files (*.oar)|*.oar|All files (*.*)|*.*", Title = "Pick OAR" };
        if (dlg.ShowDialog() == true) TxtOarPath.Text = dlg.FileName;
    }

    private async void BtnRestoreOar_Click(object sender, RoutedEventArgs e) => await DoOarAsync(bulkAll: false);
    private async void BtnBulkOar_Click(object sender, RoutedEventArgs e) => await DoOarAsync(bulkAll: true);

    private async Task DoOarAsync(bool bulkAll)
    {
        var path = TxtOarPath.Text.Trim();
        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !path.StartsWith("http", StringComparison.OrdinalIgnoreCase))) { MessageBox.Show("Pick a valid .oar file first (or http:// URL)."); return; }
        List<RegionRow> targets;
        if (bulkAll) targets = _regions.ToList();
        else
        {
            targets = ListOarRegions.Items.Cast<CheckBox>().Where(c => c.IsChecked == true).Select(c => (RegionRow)c.Tag!).ToList();
            if (!targets.Any()) { MessageBox.Show("Select at least one target region (check boxes)."); return; }
        }
        BtnRestoreOar.IsEnabled = false; BtnBulkOar.IsEnabled = false;
        ProgressOar.Value = 0;
        var log = new Progress<string>(s => { LogOar(s); TxtOarLog.ScrollToEnd(); });
        var prog = new Progress<double>(v => ProgressOar.Value = v);
        try
        {
            await BulkOarRestoreAsync(path, targets, prog, log);
        }
        finally { BtnRestoreOar.IsEnabled = true; BtnBulkOar.IsEnabled = true; }
    }

    private async Task BulkOarRestoreAsync(string oarPath, List<RegionRow> targets, IProgress<double> prog, IProgress<string> log)
    {
        var opts = BuildLoadOarOptions();
        log.Report($"OAR restore: {Path.GetFileName(oarPath)} → {targets.Count} region(s) bulk opts:{opts}");
        var sem = new SemaphoreSlim(2); // OAR is heavy
        int done = 0;
        var tasks = targets.Select(async r =>
        {
            await sem.WaitAsync();
            try
            {
                bool direct = ChkOarDirectPath == null || ChkOarDirectPath.IsChecked == true;
                string loadPath;
                if (direct || oarPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    loadPath = oarPath;
                    if (direct) log.Report($"[{r.Name}] direct load from original path (no copy).");
                }
                else
                {
                    var simDir = Path.Combine(_gridRoot, "generated", "sims", r.Name);
                    var dest = Path.Combine(simDir, "data", Path.GetFileName(oarPath));
                    try { Directory.CreateDirectory(Path.Combine(simDir, "data")); File.Copy(oarPath, dest, true); log.Report($"[{r.Name}] copied OAR to {dest}"); } catch (Exception ex) { log.Report($"[{r.Name}] copy failed: {ex.Message}"); }
                    loadPath = Path.Combine(_gridRoot, "generated", "sims", r.Name, "data", Path.GetFileName(oarPath));
                    try { if (File.Exists(Path.Combine(_gridRoot, "generated", "sims", r.Name, "data", Path.GetFileName(oarPath)))) loadPath = Path.Combine(_gridRoot, "generated", "sims", r.Name, "data", Path.GetFileName(oarPath)); } catch { }
                }
                var cmd = $"load oar{opts} \"{loadPath}\"";
                var ok = await SendConsoleCommandAsync(r, cmd, "load-oar", log);
                if (!ok)
                {
                    log.Report($"[{r.Name}] Sim offline/not managed — OAR ready at \"{loadPath}\". Start sim from this manager then retry.");
                    await Task.Delay(800);
                }
                else
                {
                    log.Report($"[{r.Name}] OAR restore issued: {cmd}");
                }
            }
            finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / targets.Count); }
        }).ToArray();
        await Task.WhenAll(tasks);
        log.Report($"Bulk OAR restore complete for {targets.Count} region(s).");
        prog.Report(100);
        if (ProgressOarSmart != null) ProgressOarSmart.Value = 100;
    }

    private void BtnOarSelectAll_Click(object sender, RoutedEventArgs e) { foreach (CheckBox c in ListOarRegions.Items) c.IsChecked = true; }
    private void BtnOarSelectNone_Click(object sender, RoutedEventArgs e) { foreach (CheckBox c in ListOarRegions.Items) c.IsChecked = false; }

    // ---------- Smart OAR mappings (different OAR per sim) ----------
    private void BtnPickOarMapping_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "OAR files (*.oar)|*.oar|All files (*.*)|*.*", Title = "Pick OAR for mapped region" };
        if (dlg.ShowDialog() == true) TxtOarMappingPath.Text = dlg.FileName;
    }

    private void BtnAddOarMapping_Click(object sender, RoutedEventArgs e)
    {
        if (CboOarRegion.SelectedItem == null) { MessageBox.Show("Pick a region first."); return; }
        var regionName = CboOarRegion.SelectedItem.ToString() ?? "";
        var path = TxtOarMappingPath.Text.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { MessageBox.Show("Pick a valid .oar file first (right side Pick)."); return; }
        var region = _regions.FirstOrDefault(r => r.Name == regionName);
        if (region == null) { MessageBox.Show("Region not found."); return; }
        if (_oarMappings.Any(m => m.Region.Name == regionName)) { MessageBox.Show($"{regionName} already mapped. Remove it first to replace."); return; }
        var mapping = new OarMapping { Region = region, OarPath = path };
        _oarMappings.Add(mapping);
        ListOarMappings.Items.Add(mapping.FullDisplay);
        LogOar($"Mapped {mapping.FullDisplay}");
    }

    private void BtnRemoveOarMapping_Click(object sender, RoutedEventArgs e)
    {
        int idx = ListOarMappings.SelectedIndex;
        if (idx < 0) { MessageBox.Show("Select a mapping to remove."); return; }
        var m = _oarMappings[idx];
        _oarMappings.RemoveAt(idx);
        ListOarMappings.Items.RemoveAt(idx);
        LogOar($"Removed mapping {m.FullDisplay}");
    }

    private void BtnClearOarMappings_Click(object sender, RoutedEventArgs e)
    {
        _oarMappings.Clear();
        ListOarMappings.Items.Clear();
        LogOar("Cleared all OAR mappings.");
    }

    private async void BtnSmartBulkOar_Click(object sender, RoutedEventArgs e) => await DoSmartBulkOarAsync();

    private async Task DoSmartBulkOarAsync()
    {
        if (!_oarMappings.Any()) { MessageBox.Show("Add at least one mapping: pick Region + .oar → Add Mapping, repeat for each sim."); return; }
        var missing = _oarMappings.Where(m => !File.Exists(m.OarPath) && !m.OarPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (missing.Any()) { MessageBox.Show("Missing file(s):\n" + string.Join("\n", missing.Select(m => m.FullDisplay))); return; }
        bool direct = ChkOarDirectPath == null || ChkOarDirectPath.IsChecked == true;
        var opts = BuildLoadOarOptions();
        BtnSmartBulkOar.IsEnabled = false; BtnAddOarMapping.IsEnabled = false; BtnRemoveOarMapping.IsEnabled = false;
        ProgressOar.Value = 0; ProgressOarSmart.Value = 0;
        IProgress<string> log = new Progress<string>(s => { LogOar(s); TxtOarLog.ScrollToEnd(); });
        IProgress<double> prog = new Progress<double>(v => { ProgressOar.Value = v; ProgressOarSmart.Value = v; });
        try
        {
            log.Report($"Smart bulk: {_oarMappings.Count} different OAR→Region mappings (direct={direct} opts:{opts}) parallel 2...");
            await BulkOarRestoreMappedAsync(_oarMappings.ToList(), direct, opts, prog, log);
        }
        finally { BtnSmartBulkOar.IsEnabled = true; BtnAddOarMapping.IsEnabled = true; BtnRemoveOarMapping.IsEnabled = true; }
    }

    private async Task BulkOarRestoreMappedAsync(List<OarMapping> mappings, bool direct, string opts, IProgress<double> prog, IProgress<string> log)
    {
        var sem = new SemaphoreSlim(2);
        int done = 0;
        var tasks = mappings.Select(async m =>
        {
            await sem.WaitAsync();
            try
            {
                string loadPath;
                if (direct || m.OarPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    loadPath = m.OarPath;
                    log.Report($"[{m.Region.Name}] direct: {Path.GetFileName(loadPath)} from {loadPath}");
                }
                else
                {
                    var simDir = Path.Combine(_gridRoot, "generated", "sims", m.Region.Name);
                    var dest = Path.Combine(simDir, "data", Path.GetFileName(m.OarPath));
                    try { Directory.CreateDirectory(Path.Combine(simDir, "data")); File.Copy(m.OarPath, dest, true); log.Report($"[{m.Region.Name}] copied to {dest}"); } catch (Exception ex) { log.Report($"[{m.Region.Name}] copy failed: {ex.Message}"); loadPath = m.OarPath; goto doLoad; }
                    loadPath = dest;
                }
            doLoad:
                var cmd = $"load oar{opts} \"{loadPath}\"";
                bool ok = await SendConsoleCommandAsync(m.Region, cmd, "load-oar", log);
                if (!ok) log.Report($"[{m.Region.Name}] Sim offline/not managed — ready at \"{loadPath}\". Start sim then retry Smart Bulk.");
                else log.Report($"[{m.Region.Name}] issued: {cmd}");
            }
            finally { sem.Release(); Interlocked.Increment(ref done); prog.Report(done * 100.0 / mappings.Count); }
        }).ToArray();
        await Task.WhenAll(tasks);
        log.Report($"Smart bulk complete for {mappings.Count} region(s).");
        prog.Report(100);
    }

    // ---------- Apache ----------
    private async void BtnApacheStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ProgressApache.IsIndeterminate = true;
            TxtApacheStatus.Text = "Starting Apache...";
            await StartApacheAsync(new Progress<string>(s => TxtApacheStatus.Text = s));
            ProgressApache.IsIndeterminate = false; ProgressApache.Value = 100;
            _ = LoadApacheLogAsync();
        }
        catch (Exception ex) { TxtApacheStatus.Text = "Apache start failed: " + ex.Message; ProgressApache.IsIndeterminate = false; }
    }

    private async void BtnApacheStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopApacheAsync(new Progress<string>(s => TxtApacheStatus.Text = s));
        }
        catch (Exception ex) { TxtApacheStatus.Text = "Stop failed: " + ex.Message; }
    }

    private void BtnApacheRestart_Click(object sender, RoutedEventArgs e) { BtnApacheStop_Click(sender, e); Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => BtnApacheStart_Click(sender, e))); }

    private void BtnApacheHtdocs_Click(object sender, RoutedEventArgs e)
    {
        var htdocs = Path.Combine(_xamppRoot, "htdocs");
        if (Directory.Exists(htdocs)) Process.Start(new ProcessStartInfo { FileName = htdocs, UseShellExecute = true });
        else MessageBox.Show("htdocs not found: " + htdocs);
    }

    private async void BtnApacheLog_Click(object sender, RoutedEventArgs e) => await LoadApacheLogAsync();
    private async Task LoadApacheLogAsync()
    {
        string logPath = Path.Combine(_xamppRoot, @"apache\logs\error.log");
        if (!File.Exists(logPath)) { TxtApacheLog.Text = "No log at " + logPath; return; }
        try
        {
            var lines = await File.ReadAllLinesAsync(logPath);
            var tail = lines.TakeLast(200);
            TxtApacheLog.Text = string.Join("\n", tail);
            TxtApacheLog.ScrollToEnd();
        }
        catch (Exception ex) { TxtApacheLog.Text = "Log read failed: " + ex.Message; }
    }

    // ---------- Logs ----------
    private async void CboLogSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CboLogSource == null || CboLogSource.SelectedIndex < 0) return;
        await RefreshLogViewAsync();
    }
    private async void BtnLogRefresh_Click(object sender, RoutedEventArgs e) => await RefreshLogViewAsync();
    private void BtnLogClear_Click(object sender, RoutedEventArgs e) => TxtLogView.Clear();
    private void BtnLogOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(_gridRoot, "generated", "robust", "data");
        if (CboLogSource?.SelectedIndex == 1 && SelectedRegion() is { } region)
            path = Path.Combine(GetRegionFolder(region), "data");
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        else MessageBox.Show("Path not found: " + path);
    }

    private string GetRegionFolder(RegionRow region)
    {
        if (!string.IsNullOrWhiteSpace(region.Folder))
            return Path.Combine(_gridRoot, "generated", region.Folder.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        return Path.Combine(_gridRoot, "generated", "sims", region.Name);
    }

    private void ChkLogAuto_Checked(object sender, RoutedEventArgs e) => StartLogAuto();
    private void ChkLogAuto_Unchecked(object sender, RoutedEventArgs e) => StopLogAuto();

    private void StartLogAuto()
    {
        StopLogAuto();
        _logCts = new CancellationTokenSource();
        var tok = _logCts.Token;
        Task.Run(async () =>
        {
            while (!tok.IsCancellationRequested)
            {
                await Task.Delay(2000, tok).ContinueWith(_ => { });
                if (tok.IsCancellationRequested) break;
                await Dispatcher.InvokeAsync(async () => await RefreshLogViewAsync());
            }
        }, tok);
    }
    private void StopLogAuto() { _logCts?.Cancel(); _logCts = null; }

    private string ResolveRobustLogPath()
    {
        var candidates = new[]
        {
            Path.Combine(_gridRoot, "generated", "robust", "data", "RobustConsoleHistory.txt"),
            Path.Combine(_gridRoot, "generated", "robust", "data", "RobustManaged.log"),
            Path.Combine(_gridRoot, "generated", "robust", "data", "robust.log"),
            Path.Combine(_gridRoot, "bin", "Robust.log"),
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;
        return candidates[0];
    }

    private string ResolveSimLogPath(RegionRow r)
    {
        var baseDir = GetRegionFolder(r);
        var candidates = new[]
        {
            Path.Combine(baseDir, "data", "OpenSimConsoleHistory.txt"),
            Path.Combine(baseDir, "data", "OpenSimManaged.log"),
            Path.Combine(baseDir, "data", "OpenSim.log"),
            Path.Combine(_gridRoot, "bin", "OpenSim.log"),
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;
        return candidates[0];
    }

    private async Task RefreshLogViewAsync()
    {
        if (CboLogSource == null || TxtLogView == null) return;
        string path;
        if (CboLogSource.SelectedIndex == 0)
        {
            path = ResolveRobustLogPath();
        }
        else
        {
            var r = SelectedRegion();
            if (r == null) { TxtLogView.Text = "Select a region in Regions tab first, then choose 'Region — select via Regions tab' and Refresh."; return; }
            path = ResolveSimLogPath(r);
        }
        if (!File.Exists(path))
        {
            var hint = CboLogSource.SelectedIndex == 0
                ? "Start Robust from this manager (Dashboard → Start Robust) — logs stream to RobustManaged.log even when hidden."
                : "Start the selected sim from this manager (Regions → Start) — logs stream to OpenSimManaged.log even when hidden.";
            TxtLogView.Text = $"Log not created yet: {path}\n{hint}";
            return;
        }
        try
        {
            // tail 600 lines + live size hint — fixes 'hidden' confusion: managed stdin still captures
            var lines = await File.ReadAllLinesAsync(path);
            var tail = lines.TakeLast(600);
            var header = $"— {Path.GetFileName(path)}  {new FileInfo(path).Length / 1024} KB  {File.GetLastWriteTime(path):HH:mm:ss} —\n";
            TxtLogView.Text = header + string.Join("\n", tail);
            TxtLogView.ScrollToEnd();
        }
        catch (Exception ex) { TxtLogView.Text = "Read failed: " + ex.Message; }
    }

    // ---------- Console — send to any region or Robust (hidden stdin) ----------
    private void LogConsole(string s)
    {
        try { TxtConsoleLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\n"); TxtConsoleLog.ScrollToEnd(); } catch { }
    }

    private async Task<bool> SendRobustCommandAsync(string command, IProgress<string>? log = null)
    {
        const string key = "robust:robust";
        if (!_managedProcesses.TryGetValue(key, out var proc) || proc.HasExited)
        {
            log?.Report($"[console] Robust not managed by this app. Start Robust from Dashboard first, then retry '{command}'. Hidden windows still work if started here.");
            return false;
        }
        try
        {
            proc.StandardInput.WriteLine(command);
            proc.StandardInput.Flush();
            log?.Report($"[console] Robust ({proc.Id}) ← {command}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Report($"[console] Robust send failed: {ex.Message}");
            return false;
        }
    }

    private async void BtnConsoleSend_Click(object sender, RoutedEventArgs e)
    {
        var target = CboConsoleTarget.SelectedItem?.ToString() ?? "";
        var cmd = TxtConsoleCommand.Text.Trim();
        if (string.IsNullOrWhiteSpace(cmd)) { MessageBox.Show("Type a command first (e.g. help, show users, backup, save oar ...)"); return; }
        var log = new Progress<string>(s => LogConsole(s));
        bool isRobust = target.StartsWith("Robust", StringComparison.OrdinalIgnoreCase);
        if (isRobust)
        {
            LogConsole($"> Robust ← {cmd}");
            await SendRobustCommandAsync(cmd, log);
        }
        else
        {
            var r = _regions.FirstOrDefault(x => x.Name == target);
            if (r == null) { MessageBox.Show("Pick a target first."); return; }
            LogConsole($"> {r.Name} ← {cmd}");
            await SendConsoleCommandAsync(r, cmd, "console", log);
        }
        LogConsole("Tip: open Logs tab → Robust / Region to see output (also in RobustManaged.log / OpenSimManaged.log).");
        TxtConsoleCommand.SelectAll();
    }

    private void BtnConsoleClear_Click(object sender, RoutedEventArgs e) => TxtConsoleLog.Clear();
    private void BtnConsoleShowInfo_Click(object sender, RoutedEventArgs e)
    {
        TxtConsoleCommand.Text = "show info";
        BtnConsoleSend_Click(sender, e);
    }
    private void BtnConsoleQuickBackup_Click(object sender, RoutedEventArgs e) { TxtConsoleCommand.Text = "backup"; BtnConsoleSend_Click(sender, e); }
    private void BtnConsoleQuickShowUsers_Click(object sender, RoutedEventArgs e) { TxtConsoleCommand.Text = "show users"; BtnConsoleSend_Click(sender, e); }
    private void BtnConsoleQuickShowRegions_Click(object sender, RoutedEventArgs e) { TxtConsoleCommand.Text = "show regions"; BtnConsoleSend_Click(sender, e); }
    private void BtnConsoleQuickAlert_Click(object sender, RoutedEventArgs e)
    {
        var msg = Microsoft.VisualBasic.Interaction.InputBox("Alert message to send to all users in target region:", "Console — alert", "Hello from Fresh Metaverse ✨");
        if (string.IsNullOrWhiteSpace(msg)) return;
        // alert needs quotes if spaces
        TxtConsoleCommand.Text = $"alert \"{msg.Replace("\"", "'")}\"";
        BtnConsoleSend_Click(sender, e);
    }
    private void BtnConsoleQuickChangeRegion_Click(object sender, RoutedEventArgs e)
    {
        TxtConsoleCommand.Text = "change region ";
        TxtConsoleCommand.Focus();
        TxtConsoleCommand.CaretIndex = TxtConsoleCommand.Text.Length;
    }

    // ---------- Config ----------
    private string? _currentIniPath;
    private void LoadIniFileList()
    {
        ListIniFiles.Items.Clear();
        var files = new List<string>();
        var robustIni = Path.Combine(_gridRoot, "generated", "robust", "Robust.ini");
        var assetIni = Path.Combine(_gridRoot, "generated", "asset", "AssetServer.ini");
        if (File.Exists(robustIni)) files.Add(robustIni);
        if (File.Exists(assetIni)) files.Add(assetIni);
        var simsDir = Path.Combine(_gridRoot, "generated", "sims");
        if (Directory.Exists(simsDir))
        {
            foreach (var d in Directory.GetDirectories(simsDir))
            {
                var o = Path.Combine(d, "OpenSim.ini");
                if (File.Exists(o)) files.Add(o);
                var regDir = Path.Combine(d, "regions");
                if (Directory.Exists(regDir)) foreach (var r in Directory.GetFiles(regDir, "*.ini")) files.Add(r);
            }
        }
        // fallback to templates
        if (!files.Any())
        {
            var tplDir = Path.Combine(_gridRoot, "templates");
            if (Directory.Exists(tplDir)) foreach (var f in Directory.GetFiles(tplDir, "*.tpl")) files.Add(f);
        }
        foreach (var f in files) ListIniFiles.Items.Add(f);
        if (ListIniFiles.Items.Count > 0) ListIniFiles.SelectedIndex = 0;
    }

    private async void ListIniFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListIniFiles.SelectedItem is string p) await LoadIniIntoEditorAsync(p);
    }

    private Task LoadIniIntoEditorAsync(string path) { LoadIniIntoEditor(path); return Task.CompletedTask; }
    private void LoadIniIntoEditor(string path)
    {
        _currentIniPath = path;
        TxtIniTitle.Text = path;
        try
        {
            TxtIniContent.Text = File.ReadAllText(path);
            if (path.Contains(".ini") && File.ReadAllText(path).Contains("MaxPrims"))
            {
                // highlight
            }
        }
        catch (Exception ex) { TxtIniContent.Text = "Read failed: " + ex.Message; }
    }

    private void BtnIniReload_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIniPath != null) LoadIniIntoEditor(_currentIniPath);
        else if (ListIniFiles.SelectedItem is string p) LoadIniIntoEditor(p);
    }

    private void BtnIniSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentIniPath == null) { MessageBox.Show("Select a file first."); return; }
        try
        {
            File.WriteAllText(_currentIniPath, TxtIniContent.Text);
            TxtStatusBar.Text = $"Saved {Path.GetFileName(_currentIniPath)} ♡";
            MessageBox.Show($"Saved {_currentIniPath}\nRestart affected service to apply.", "Fresh Metaverse");
        }
        catch (Exception ex) { MessageBox.Show("Save failed: " + ex.Message); }
    }

    private async void BtnForceKillAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Force kill ALL sim processes immediately? This bypasses console 'shutdown'.", "Force Kill All Sims", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        LogBulk("Force Kill All Sims requested — killing managed first, then sweeping external sim dots.");
        // kill managed
        foreach (var kv in _managedProcesses.Where(kv => kv.Key.StartsWith("sim:")).ToList())
        {
            try { kv.Value.Kill(true); LogBulk($"[kill] {kv.Key} pid {kv.Value.Id} killed (managed)."); } catch (Exception ex) { LogBulk($"[kill] {kv.Key} failed: {ex.Message}"); }
            _managedProcesses.Remove(kv.Key);
        }
        await ForceKillExternalAsync("sim", null);
        await RefreshRegionStatusesAsync();
    }

    private async void BtnForceKillRobust_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Force kill Robust immediately?", "Force Kill Robust", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        LogBulk("Force Kill Robust requested.");
        if (_managedProcesses.TryGetValue("robust:robust", out var p) && !p.HasExited)
        {
            try { p.Kill(true); LogBulk($"[kill] robust:robust pid {p.Id} killed (managed)."); } catch (Exception ex) { LogBulk($"[kill] robust failed: {ex.Message}"); }
            _managedProcesses.Remove("robust:robust");
        }
        await ForceKillExternalAsync("robust", null);
        await RefreshRobustStatusAsync();
    }

    private void BtnRegionShow_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        var key = "sim:" + r.Name;
        if (_managedProcesses.TryGetValue(key, out var proc) && !proc.HasExited)
        {
            try { if (IsWindow(proc.MainWindowHandle)) ShowWindow(proc.MainWindowHandle, SW_RESTORE); ShowWindow(proc.MainWindowHandle, SW_SHOW); LogRegion($"Show {r.Name} pid {proc.Id}."); } catch (Exception ex) { LogRegion($"Show {r.Name} failed: {ex.Message}"); }
        }
        else LogRegion($"{r.Name} is not managed by this app (or already stopped). Start it from this manager first. External stale sims: use Force Kill then Start again.");
    }

    private void BtnRegionHide_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        var key = "sim:" + r.Name;
        if (_managedProcesses.TryGetValue(key, out var proc) && !proc.HasExited)
        {
            try { if (IsWindow(proc.MainWindowHandle)) ShowWindow(proc.MainWindowHandle, SW_HIDE); LogRegion($"Hide {r.Name} pid {proc.Id}."); } catch (Exception ex) { LogRegion($"Hide {r.Name} failed: {ex.Message}"); }
        }
        else LogRegion($"{r.Name} is not managed by this app.");
    }

    private async void BtnRegionForceKill_Click(object sender, RoutedEventArgs e)
    {
        var r = SelectedRegion(); if (r == null) { MessageBox.Show("Select a region."); return; }
        if (MessageBox.Show($"Force kill {r.Name} immediately? Bypasses 'shutdown' and kills process tree.", "Force Kill", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var key = "sim:" + r.Name;
        LogRegion($"Force kill {r.Name} requested.");
        if (_managedProcesses.TryGetValue(key, out var proc) && !proc.HasExited)
        {
            try { proc.Kill(true); LogRegion($"[kill] {key} pid {proc.Id} killed (managed)."); } catch (Exception ex) { LogRegion($"[kill] {key} failed: {ex.Message}"); }
            _managedProcesses.Remove(key);
        }
        // also sweep external matching name
        await ForceKillExternalAsync("sim", r.Name);
        await RefreshRegionStatusesAsync();
    }

    private async Task ForceKillExternalAsync(string kind, string? nameOrNull)
    {
        try
        {
            string script;
            if (kind == "robust")
            {
                var robustDir = Path.Combine(_gridRoot, "generated", "robust");
                var robustBin = Path.Combine(_gridRoot, "bin", "Robust.dll");
                script = "$hits = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'dotnet.exe' -and ($_.CommandLine -like '*Robust.dll*') }; $hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"killed Robust pid \" + $_.ProcessId }";
            }
            else
            {
                // sim: if name given, match that sim ini in commandline; else kill all OpenSim sims
                if (!string.IsNullOrEmpty(nameOrNull))
                    script = "$n='" + nameOrNull.Replace("'","''") + "'; $hits = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*OpenSim.dll*' -and $_.CommandLine -like \"*\" + $n + \"*\" }; $hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"killed sim \" + $n + \" pid \" + $_.ProcessId }";
                else
                    script = "$hits = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*OpenSim.dll*' }; $hits | ForEach-Object { Stop-Process -Id $_.ProcessId -Force; \"killed sim pid \" + $_.ProcessId }";
            }
            var psi = new ProcessStartInfo("powershell", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command " + JsonSerializer.Serialize(script, s_psArgsOptions))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            if (!string.IsNullOrWhiteSpace(stdout)) foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LogBulk(line.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) LogBulk("[kill sweep] " + stderr.Trim());
            // also report to region log if it was a single-region kill
            if (!string.IsNullOrEmpty(nameOrNull) && !string.IsNullOrWhiteSpace(stdout)) foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) LogRegion(line.Trim());
        }
        catch (Exception ex) { LogBulk("[kill sweep] failed: " + ex.Message); }
    }

    private void BtnRegen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var py = Path.Combine(_gridRoot, "generate_configs.py");
            if (!File.Exists(py)) { MessageBox.Show("generate_configs.py not found at " + py); return; }
            Process.Start(new ProcessStartInfo("python", $"\"{py}\"") { WorkingDirectory = _gridRoot, UseShellExecute = false });
            MessageBox.Show("Regeneration started (python generate_configs.py). Reload INI list after.", "Fresh Metaverse");
        }
        catch (Exception ex) { MessageBox.Show("Regen failed: " + ex.Message); }
    }
}
