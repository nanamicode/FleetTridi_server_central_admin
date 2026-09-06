using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FleetTridi.Admin;

public partial class MainWindow : Window
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    string token = "";
    string? selectedId;
    DeviceRow? selected;
    bool screenBusy;

    public MainWindow()
    {
        InitializeComponent();
        liveTimer.Tick += async (_, _) => await LiveTick();
    }

    async Task LoginAsync()
    {
        var baseUrl = ServerBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("URL do servidor Admin inválida.");

        http.BaseAddress = new Uri(baseUrl + "/");
        var r = await http.PostAsJsonAsync("api/login", new { username = UserBox.Text, password = PassBox.Password });
        var body = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException("Login recusado: " + body);

        token = JsonDocument.Parse(body).RootElement.GetProperty("token").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("O servidor retornou um token administrativo vazio.");

        http.DefaultRequestHeaders.Remove("X-Fleet-Token");
        http.DefaultRequestHeaders.Add("X-Fleet-Token", token);

        if (string.IsNullOrWhiteSpace(AgentUrlBox.Text))
            AgentUrlBox.Text = SuggestAgentUrl(uri);

        await Refresh();
        await RefreshReleases();
        StatusText.Text = "Conectado ao servidor.";
    }

    async void Login_Click(object sender, RoutedEventArgs e)
    {
        try { await LoginAsync(); }
        catch (Exception ex)
        {
            token = "";
            StatusText.Text = "Falha: " + ex.Message;
        }
    }

    async Task EnsureLogin()
    {
        if (!string.IsNullOrWhiteSpace(token)) return;
        await LoginAsync();
    }

    void DetectAgentUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out var uri))
        {
            StatusText.Text = "URL do servidor Admin inválida.";
            return;
        }

        AgentUrlBox.Text = SuggestAgentUrl(uri);
        StatusText.Text = string.IsNullOrWhiteSpace(AgentUrlBox.Text)
            ? "Não encontrei um IPv4 LAN automaticamente. Informe o IP do PC manualmente."
            : "URL para os totens: " + AgentUrlBox.Text;
    }

    static string SuggestAgentUrl(Uri adminUri)
    {
        if (!adminUri.IsLoopback) return adminUri.ToString().TrimEnd('/');

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n =>
            {
                var props = n.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                return props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => new { a.Address, hasGateway });
            })
            .Where(x => !IPAddress.IsLoopback(x.Address) &&
                        !x.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .OrderByDescending(x => x.hasGateway)
            .ThenByDescending(x => IsPrivateIPv4(x.Address))
            .ToArray();

        var ip = candidates.FirstOrDefault()?.Address;
        if (ip is null) return "";

        var port = adminUri.IsDefaultPort ? "" : ":" + adminUri.Port;
        return $"{adminUri.Scheme}://{ip}{port}";
    }

    static bool IsPrivateIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    async Task Refresh()
    {
        var data = await http.GetFromJsonAsync<List<DeviceRow>>("api/devices") ?? [];
        DevicesGrid.ItemsSource = data;
        OnlineCount.Text = $"{data.Count(x => x.online)} online / {data.Count} cadastrados";

        if (selectedId is not null)
        {
            selected = data.FirstOrDefault(x => x.id == selectedId);
            if (selected is not null) ShowSelected(selected);
        }
    }

    void DevicesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        selected = DevicesGrid.SelectedItem as DeviceRow;
        selectedId = selected?.id;
        if (selected is not null) ShowSelected(selected);
    }

    void ShowSelected(DeviceRow d)
    {
        SelectedLabel.Text = $"{d.name} — {d.city} / {d.site}\nID: {d.id}\nIP: {d.lastIp}   Android: {d.androidVersion}   Agente: {d.agentVersion}\nTridiAudience: {d.audienceVersion} ({d.audiencePackage})   Privilégio: {d.privilegeMode}";
        if (!string.IsNullOrWhiteSpace(d.audiencePackage))
            ReleasePackageBox.Text = d.audiencePackage;
        TelemetryBox.Text = d.telemetry.ValueKind == JsonValueKind.Undefined ? "" : JsonSerializer.Serialize(d.telemetry, new JsonSerializerOptions { WriteIndented = true });
    }

    async Task<string> Job(string type, Dictionary<string, string>? args = null)
    {
        if (selectedId is null) throw new InvalidOperationException("Selecione um totem.");
        var r = await http.PostAsJsonAsync($"api/devices/{selectedId}/jobs", new { type, args = args ?? new Dictionary<string, string>() });
        var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException(text);
        OutputBox.Text = text;
        return text;
    }

    async void AddTotem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureLogin();
            var dlg = new AddTotemWindow { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var r = await http.PostAsJsonAsync("api/devices", new
            {
                name = dlg.TotemName,
                city = dlg.City,
                site = dlg.Site,
                initialIp = dlg.InitialIp
            });
            var body = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) throw new InvalidOperationException(body);
            OutputBox.Text = "Totem cadastrado.\n" + body + "\n\nAgora selecione-o e use “Bootstrap via ADB”.";
            await Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FleetTridi"); }
    }

    async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (selectedId is null) return;
        if (MessageBox.Show(this, $"Excluir {selected?.name} do FleetTridi?", "Confirmar", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var r = await http.DeleteAsync($"api/devices/{selectedId}");
        OutputBox.Text = await r.Content.ReadAsStringAsync();
        selectedId = null;
        selected = null;
        await Refresh();
    }

    async void Bootstrap_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (selectedId is null) throw new InvalidOperationException("Selecione o totem cadastrado.");
            var enrollment = await http.GetFromJsonAsync<Enrollment>($"api/devices/{selectedId}/enrollment")
                ?? throw new InvalidOperationException("Não foi possível ler o cadastro.");
            if (string.IsNullOrWhiteSpace(enrollment.initialIp)) throw new InvalidOperationException("Esse totem não tem IP inicial cadastrado.");

            var dlg = new OpenFileDialog { Filter = "FleetTridi Agent APK|*.apk", Title = "Selecione o FleetTridiAgent.apk" };
            if (dlg.ShowDialog() != true) return;

            var adb = FindAdb();
            var target = await ResolveAdbTarget(adb, enrollment.initialIp);
            var serial = target.Serial;
            var log = new StringBuilder();

            if (target.IsNetwork)
                log.AppendLine(await Run(adb, "connect", serial));

            log.AppendLine(await Run(adb, "-s", serial, "wait-for-device"));

            // adb root é apenas uma tentativa de elevar o adbd. A validação real do app acontece depois.
            log.AppendLine(await Run(adb, "-s", serial, "root"));
            await Task.Delay(900);
            if (target.IsNetwork)
                log.AppendLine(await Run(adb, "connect", serial));
            log.AppendLine(await Run(adb, "-s", serial, "wait-for-device"));

            // Provisionamento físico: reinstala o agente limpo. Isso também evita conflito de certificado entre builds DEBUG.
            log.AppendLine(await Run(adb, "-s", serial, "uninstall", "com.tridi.fleet.agent"));
            log.AppendLine(await Run(adb, "-s", serial, "install", dlg.FileName));

            var shellRootCheck = await Run(adb, "-s", serial, "shell", "su", "-c", "id");
            log.AppendLine(shellRootCheck);
            if (!shellRootCheck.Contains("uid=0", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "O shell ADB não conseguiu executar su -c id. O agente pode ser instalado, mas atualização silenciosa/reboot remoto não devem ser testados ainda.");

            var agentServer = AgentUrlBox.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(agentServer, UriKind.Absolute, out var agentUri) ||
                (agentUri.Scheme != Uri.UriSchemeHttp && agentUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("Informe uma URL válida em “URL vista pelos totens”.");

            if (agentUri.IsLoopback)
                throw new InvalidOperationException(
                    "A URL dos totens não pode ser localhost/127.0.0.1. Use o IP LAN do PC ou a URL pública da central.");

            log.AppendLine(await Run(adb, "-s", serial, "shell", "am", "broadcast",
                "-n", "com.tridi.fleet.agent/.ConfigReceiver",
                "-a", "com.tridi.fleet.agent.CONFIG",
                "--es", "server", agentServer,
                "--es", "deviceId", enrollment.deviceId,
                "--es", "token", enrollment.enrollmentToken,
                "--es", "name", enrollment.name ?? "",
                "--es", "city", enrollment.city ?? "",
                "--es", "site", enrollment.site ?? "",
                "--es", "audiencePackage", "com.tridi.audience"));

            OutputBox.Text = log.ToString();
            StatusText.Text = "Enrollment enviado. Validando conexão e root pelo próprio agente...";

            DeviceRow? confirmed = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(1000);
                var data = await http.GetFromJsonAsync<List<DeviceRow>>("api/devices") ?? [];
                confirmed = data.FirstOrDefault(x => x.id == enrollment.deviceId);
                if (confirmed?.online == true && !string.IsNullOrWhiteSpace(confirmed.privilegeMode))
                    break;
            }

            await Refresh();

            if (confirmed?.online != true)
                throw new InvalidOperationException(
                    "O APK foi provisionado, mas o agente não ficou online em 20s. " +
                    "Confira se a URL dos totens aponta para o IP LAN correto e se o servidor está escutando na rede. " +
                    "No pacote Windows use INICIAR-TESTE-LOCAL.bat.");

            if (!string.Equals(confirmed.privilegeMode, "su-root", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"O totem conectou, mas o próprio FleetTridi Agent reportou privilégio “{confirmed.privilegeMode}”. " +
                    "Não faça teste de atualização remota até o app ter su-root.");

            StatusText.Text = $"Bootstrap validado: {confirmed.name} online, agente {confirmed.agentVersion}, su-root confirmado pelo app.";
            OutputBox.Text += "\n\nVALIDAÇÃO FINAL: agente online + su-root confirmado pelo próprio APK.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bootstrap ADB"); }
    }

    async Task<(string Serial, bool IsNetwork)> ResolveAdbTarget(string adb, string configured)
    {
        var value = configured.Trim();

        if (string.IsNullOrWhiteSpace(value))
        {
            var listing = await Run(adb, "devices");
            var found = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.EndsWith("\tdevice", StringComparison.Ordinal))
                .Select(line => line.Split('\t')[0].Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (found.Length == 0)
                throw new InvalidOperationException("Nenhum dispositivo ADB conectado. Conecte a BTV por USB ou informe o IP/serial no cadastro.");
            if (found.Length > 1)
                throw new InvalidOperationException("Há mais de um dispositivo ADB conectado. Informe o serial USB ou IP do totem no cadastro.");

            return (found[0], false);
        }

        if (IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            return (value.Contains(':') ? value : value + ":5555", true);

        if (value.Contains(':'))
            return (value, true);

        if (value.Contains('.'))
            return (value + ":5555", true);

        return (value, false);
    }

    string FindAdb()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;
        return "adb.exe";
    }

    static async Task<string> Run(string exe, params string[] args)
    {
        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
        p.Start();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return $"> {Path.GetFileName(exe)} {string.Join(' ', args)}\n{await stdout}{await stderr}";
    }

    async void Home_Click(object s, RoutedEventArgs e) => await SafeJob("keyevent", new() { ["key"] = "3" });
    async void Back_Click(object s, RoutedEventArgs e) => await SafeJob("keyevent", new() { ["key"] = "4" });
    async void Reboot_Click(object s, RoutedEventArgs e) => await SafeJob("rebootDevice");
    async void RestartAudience_Click(object s, RoutedEventArgs e) => await SafeJob("restartAudience");

    async Task SafeJob(string type, Dictionary<string, string>? args = null)
    {
        try { await Job(type, args); }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
    }

    async void InstallApk_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (selectedId is null) throw new InvalidOperationException("Selecione um totem.");
            var dlg = new OpenFileDialog { Filter = "APK Android|*.apk" };
            if (dlg.ShowDialog() != true) return;
            using var form = new MultipartFormDataContent();
            await using var fs = File.OpenRead(dlg.FileName);
            form.Add(new StreamContent(fs), "file", Path.GetFileName(dlg.FileName));
            var r = await http.PostAsync($"api/devices/{selectedId}/install-apk", form);
            OutputBox.Text = await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
    }

    async void PushFile_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (selectedId is null) throw new InvalidOperationException("Selecione um totem.");
            var dlg = new OpenFileDialog();
            if (dlg.ShowDialog() != true) return;
            using var form = new MultipartFormDataContent();
            await using var fs = File.OpenRead(dlg.FileName);
            form.Add(new StreamContent(fs), "file", Path.GetFileName(dlg.FileName));
            form.Add(new StringContent(TargetPathBox.Text), "target");
            var r = await http.PostAsync($"api/devices/{selectedId}/upload", form);
            OutputBox.Text = await r.Content.ReadAsStringAsync();
        }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
    }

    async void Capture_Click(object sender, RoutedEventArgs e) => await CaptureAndRefresh();

    async Task CaptureAndRefresh()
    {
        if (selectedId is null || screenBusy) return;
        screenBusy = true;
        try
        {
            await Job("captureScreen");
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(300);
                var r = await http.GetAsync($"api/devices/{selectedId}/screenshot?ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
                if (!r.IsSuccessStatusCode || r.StatusCode == System.Net.HttpStatusCode.NoContent) continue;
                var bytes = await r.Content.ReadAsByteArrayAsync();
                if (bytes.Length == 0) continue;
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ScreenImage.Source = bmp;
                break;
            }
        }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
        finally { screenBusy = false; }
    }

    async Task LiveTick()
    {
        if (LiveCheck.IsChecked == true) await CaptureAndRefresh();
    }

    void LiveCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LiveCheck.IsChecked == true) liveTimer.Start(); else liveTimer.Stop();
    }

    async void ScreenImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ScreenImage.Source is not BitmapSource bmp || selectedId is null) return;
        ScreenImage.Focus();
        var p = e.GetPosition(ScreenImage);
        var scale = Math.Min(ScreenImage.ActualWidth / bmp.PixelWidth, ScreenImage.ActualHeight / bmp.PixelHeight);
        var shownW = bmp.PixelWidth * scale;
        var shownH = bmp.PixelHeight * scale;
        var offX = (ScreenImage.ActualWidth - shownW) / 2;
        var offY = (ScreenImage.ActualHeight - shownH) / 2;
        if (p.X < offX || p.Y < offY || p.X > offX + shownW || p.Y > offY + shownH) return;
        var x = (int)((p.X - offX) / scale);
        var y = (int)((p.Y - offY) / scale);
        await SafeJob("tap", new() { ["x"] = x.ToString(), ["y"] = y.ToString() });
    }

    async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(Keyboard.FocusedElement, ScreenImage)) return;
        var code = e.Key switch
        {
            Key.Up => "19", Key.Down => "20", Key.Left => "21", Key.Right => "22",
            Key.Enter => "23", Key.Escape => "4", Key.Home => "3",
            _ => null
        };
        if (code is null) return;
        e.Handled = true;
        await SafeJob("keyevent", new() { ["key"] = code });
    }

    IEnumerable<DeviceRow> SelectedRows() => DevicesGrid.SelectedItems.Cast<DeviceRow>();

    async Task BulkApk(bool allOnline)
    {
        var dlg = new OpenFileDialog { Filter = "APK Android|*.apk" };
        if (dlg.ShowDialog() != true) return;
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(dlg.FileName);
        form.Add(new StreamContent(fs), "file", Path.GetFileName(dlg.FileName));
        form.Add(new StringContent(string.Join(',', SelectedRows().Select(x => x.id))), "ids");
        form.Add(new StringContent(allOnline.ToString()), "allOnline");
        var r = await http.PostAsync("api/bulk/install-apk", form);
        OutputBox.Text = await r.Content.ReadAsStringAsync();
    }

    async Task BulkFile(bool allOnline)
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog() != true) return;
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(dlg.FileName);
        form.Add(new StreamContent(fs), "file", Path.GetFileName(dlg.FileName));
        form.Add(new StringContent(TargetPathBox.Text), "target");
        form.Add(new StringContent(string.Join(',', SelectedRows().Select(x => x.id))), "ids");
        form.Add(new StringContent(allOnline.ToString()), "allOnline");
        var r = await http.PostAsync("api/bulk/upload", form);
        OutputBox.Text = await r.Content.ReadAsStringAsync();
    }

    async void BulkApkSelected_Click(object s, RoutedEventArgs e) => await BulkApk(false);
    async void BulkApkOnline_Click(object s, RoutedEventArgs e) => await BulkApk(true);
    async void BulkFileSelected_Click(object s, RoutedEventArgs e) => await BulkFile(false);
    async void BulkFileOnline_Click(object s, RoutedEventArgs e) => await BulkFile(true);

    async void BulkReboot_Click(object s, RoutedEventArgs e)
    {
        var ids = SelectedRows().Select(x => x.id).ToArray();
        var r = await http.PostAsJsonAsync("api/bulk/jobs", new { ids, allOnline = false, type = "rebootDevice", args = new Dictionary<string, string>() });
        OutputBox.Text = await r.Content.ReadAsStringAsync();
    }

    async Task RefreshReleases()
    {
        try
        {
            var list = await http.GetFromJsonAsync<List<ReleaseRow>>("api/releases") ?? [];
            foreach (var item in list)
                item.label = $"{item.version} [{item.channel}] — {item.sha256[..Math.Min(12, item.sha256.Length)]}";
            ReleaseBox.ItemsSource = list;
            if (list.Count > 0 && ReleaseBox.SelectedIndex < 0) ReleaseBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            OutputBox.Text = "Falha ao ler releases: " + ex.Message;
        }
    }

    async void RefreshReleases_Click(object s, RoutedEventArgs e) => await RefreshReleases();

    async void AddRelease_Click(object s, RoutedEventArgs e)
    {
        try
        {
            await EnsureLogin();
            var version = ReleaseVersionBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(version)) throw new InvalidOperationException("Informe a versão da release.");

            var dlg = new OpenFileDialog { Filter = "APK Android|*.apk", Title = "Selecione o APK do TridiAudience" };
            if (dlg.ShowDialog() != true) return;

            using var form = new MultipartFormDataContent();
            await using var fs = File.OpenRead(dlg.FileName);
            form.Add(new StreamContent(fs), "file", Path.GetFileName(dlg.FileName));
            form.Add(new StringContent(version), "version");
            var packageName = ReleasePackageBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(packageName)) packageName = "com.tridi.audience";
            form.Add(new StringContent(packageName), "packageName");
            form.Add(new StringContent(string.IsNullOrWhiteSpace(ReleaseChannelBox.Text) ? "stable" : ReleaseChannelBox.Text.Trim()), "channel");
            form.Add(new StringContent("Cadastrado pelo FleetTridi Admin"), "notes");

            var response = await http.PostAsync("api/releases", form);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);

            OutputBox.Text = body;
            await RefreshReleases();
        }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
    }

    async Task DeployRelease(bool dryRun)
    {
        try
        {
            await EnsureLogin();
            if (ReleaseBox.SelectedItem is not ReleaseRow release)
                throw new InvalidOperationException("Selecione uma release.");

            var percent = int.TryParse(RolloutPercentBox.Text, out var parsed) ? Math.Clamp(parsed, 1, 100) : 10;
            var city = RolloutCityBox.Text.Trim();
            var ids = string.IsNullOrWhiteSpace(city) ? SelectedRows().Select(x => x.id).ToArray() : Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(city) && ids.Length == 0)
                throw new InvalidOperationException("Informe uma cidade ou selecione ao menos um totem.");

            if (!dryRun)
            {
                var targetText = string.IsNullOrWhiteSpace(city) ? $"{ids.Length} totem(ns) selecionado(s)" : $"cidade {city}";
                var confirm = MessageBox.Show(this,
                    $"Publicar TridiAudience {release.version} para {percent}% de {targetText}?\n\nUse primeiro Simular rollout para conferir os alvos.",
                    "Confirmar rollout", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            var response = await http.PostAsJsonAsync($"api/releases/{release.id}/deploy", new
            {
                ids,
                city = string.IsNullOrWhiteSpace(city) ? null : city,
                allOnline = false,
                onlyOnline = true,
                percent,
                dryRun
            });
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(body);
            OutputBox.Text = body;
        }
        catch (Exception ex) { OutputBox.Text = ex.Message; }
    }

    async void DryRunRelease_Click(object s, RoutedEventArgs e) => await DeployRelease(true);
    async void DeployRelease_Click(object s, RoutedEventArgs e) => await DeployRelease(false);

    public sealed class DeviceRow
    {
        public string id { get; set; } = "";
        public string name { get; set; } = "";
        public string city { get; set; } = "";
        public string site { get; set; } = "";
        public string initialIp { get; set; } = "";
        public string lastIp { get; set; } = "";
        public string model { get; set; } = "";
        public string androidVersion { get; set; } = "";
        public string agentVersion { get; set; } = "";
        public string audienceVersion { get; set; } = "";
        public string audiencePackage { get; set; } = "";
        public string privilegeMode { get; set; } = "";
        public string updateChannel { get; set; } = "";
        public string agentServerUrl { get; set; } = "";
        public bool online { get; set; }
        public bool rootAvailable { get; set; }
        public JsonElement telemetry { get; set; }
    }

    public sealed class ReleaseRow
    {
        public string id { get; set; } = "";
        public string version { get; set; } = "";
        public string packageName { get; set; } = "";
        public string channel { get; set; } = "";
        public string notes { get; set; } = "";
        public string sha256 { get; set; } = "";
        public long sizeBytes { get; set; }
        public DateTimeOffset createdAt { get; set; }
        public string label { get; set; } = "";
    }

    public sealed class Enrollment
    {
        public string deviceId { get; set; } = "";
        public string enrollmentToken { get; set; } = "";
        public string? name { get; set; }
        public string? city { get; set; }
        public string? site { get; set; }
        public string initialIp { get; set; } = "";
    }
}