using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FLEETTRIDI_URLS") ?? "http://0.0.0.0:8787");
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

var adminUser = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_USER") ?? "nanamicode";
var adminPassword = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_PASSWORD") ?? "veralucia12";
var adminToken = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_TOKEN")
    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

var dataDir = Environment.GetEnvironmentVariable("FLEETTRIDI_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
var uploadDir = Path.Combine(dataDir, "uploads");
var dbPath = Path.Combine(dataDir, "devices.json");
Directory.CreateDirectory(uploadDir);

var devices = LoadDevices(dbPath, json);
var sessions = new ConcurrentDictionary<string, Channel<string>>();
var saveGate = new SemaphoreSlim(1, 1);

bool IsAdmin(HttpContext c) =>
    c.Request.Headers.TryGetValue("X-Fleet-Token", out var t) && t == adminToken;

bool IsOnline(Device d) => (DateTimeOffset.UtcNow - d.LastSeen).TotalSeconds < 35;

string PublicBaseUrl(HttpContext c) =>
    (Environment.GetEnvironmentVariable("FLEETTRIDI_PUBLIC_URL") ?? $"{c.Request.Scheme}://{c.Request.Host}").TrimEnd('/');

async Task SaveAsync()
{
    await saveGate.WaitAsync();
    try
    {
        var temp = dbPath + ".tmp";
        await using (var fs = File.Create(temp))
            await JsonSerializer.SerializeAsync(fs, devices.Values.OrderBy(x => x.Id).ToList(), json);
        File.Move(temp, dbPath, true);
    }
    finally { saveGate.Release(); }
}

async Task<JobState> QueueJob(Device d, string type, Dictionary<string, string>? args)
{
    var job = new JobEnvelope(Guid.NewGuid().ToString("N"), type, args ?? new());
    var state = new JobState(job.Id, job.Type, "queued", null, DateTimeOffset.UtcNow, job.Args);
    d.Jobs[job.Id] = state;

    if (sessions.TryGetValue(d.Id, out var ch))
    {
        await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job }, json));
        state = state with { Status = "sent", UpdatedAt = DateTimeOffset.UtcNow };
        d.Jobs[job.Id] = state;
    }

    await SaveAsync();
    return state;
}

IEnumerable<Device> ResolveTargets(string? csvIds, bool allOnline)
{
    if (allOnline) return devices.Values.Where(IsOnline).ToArray();
    var ids = (csvIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return ids.Select(id => devices.TryGetValue(id, out var d) ? d : null).Where(d => d is not null).Cast<Device>().ToArray();
}

app.MapGet("/", () => Results.Ok(new { name = "FleetTridi Server", version = "0.2.0", devices = devices.Count }));

app.MapPost("/api/login", async (HttpContext c) =>
{
    var login = await JsonSerializer.DeserializeAsync<LoginRequest>(c.Request.Body, json);
    return login?.Username == adminUser && login.Password == adminPassword
        ? Results.Ok(new { token = adminToken })
        : Results.Unauthorized();
});

app.MapGet("/api/devices", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    return Results.Ok(devices.Values.Select(d => new
    {
        d.Id, d.Name, d.City, d.Site, d.InitialIp, d.LastIp, d.Model, d.AndroidVersion,
        d.AgentVersion, d.RootAvailable, d.LastSeen, online = IsOnline(d), d.Telemetry
    }).OrderBy(x => x.City).ThenBy(x => x.Name));
});

app.MapPost("/api/devices", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var req = await JsonSerializer.DeserializeAsync<CreateDeviceRequest>(c.Request.Body, json);
    if (req is null) return Results.BadRequest();

    var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id.Trim();
    if (devices.ContainsKey(id)) return Results.Conflict("device id already exists");

    var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    var d = new Device
    {
        Id = id,
        Name = string.IsNullOrWhiteSpace(req.Name) ? id : req.Name.Trim(),
        City = req.City?.Trim() ?? "",
        Site = req.Site?.Trim() ?? "",
        InitialIp = req.InitialIp?.Trim() ?? "",
        EnrollmentToken = token
    };
    devices[id] = d;
    await SaveAsync();
    return Results.Ok(new { deviceId = id, enrollmentToken = token, d.Name, d.City, d.Site, d.InitialIp });
});

app.MapGet("/api/devices/{id}/enrollment", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    return devices.TryGetValue(id, out var d)
        ? Results.Ok(new { deviceId = d.Id, enrollmentToken = d.EnrollmentToken, d.Name, d.City, d.Site, d.InitialIp })
        : Results.NotFound();
});

app.MapDelete("/api/devices/{id}", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryRemove(id, out _)) return Results.NotFound();
    if (sessions.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    await SaveAsync();
    return Results.Ok();
});

app.MapGet("/api/devices/{id}/jobs", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    return Results.Ok(d.Jobs.Values.OrderByDescending(j => j.UpdatedAt).Take(100));
});

app.MapPost("/api/devices/{id}/jobs", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    var req = await JsonSerializer.DeserializeAsync<JobRequest>(c.Request.Body, json);
    if (req is null || string.IsNullOrWhiteSpace(req.Type)) return Results.BadRequest();
    return Results.Ok(await QueueJob(d, req.Type, req.Args));
});

app.MapPost("/api/bulk/jobs", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var req = await JsonSerializer.DeserializeAsync<BulkJobRequest>(c.Request.Body, json);
    if (req is null || string.IsNullOrWhiteSpace(req.Type)) return Results.BadRequest();

    var targets = req.AllOnline
        ? devices.Values.Where(IsOnline).ToArray()
        : (req.Ids ?? []).Select(id => devices.TryGetValue(id, out var d) ? d : null).Where(d => d is not null).Cast<Device>().ToArray();

    var results = new List<JobState>();
    foreach (var d in targets) results.Add(await QueueJob(d, req.Type, req.Args));
    return Results.Ok(new { count = results.Count, jobs = results });
});

app.MapPost("/api/devices/{id}/upload", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var target = form["target"].ToString();
    if (file is null || string.IsNullOrWhiteSpace(target)) return Results.BadRequest("file + target required");

    var key = await SaveUpload(file);
    return Results.Ok(await QueueJob(d, "pushFile", new()
    {
        ["url"] = $"{PublicBaseUrl(c)}/agent/files/{key}",
        ["target"] = target
    }));
});

app.MapPost("/api/devices/{id}/install-apk", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("file required");

    var key = await SaveUpload(file);
    return Results.Ok(await QueueJob(d, "installApk", new() { ["url"] = $"{PublicBaseUrl(c)}/agent/files/{key}" }));
});

app.MapPost("/api/bulk/upload", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var target = form["target"].ToString();
    var allOnline = bool.TryParse(form["allOnline"], out var ao) && ao;
    if (file is null || string.IsNullOrWhiteSpace(target)) return Results.BadRequest("file + target required");

    var key = await SaveUpload(file);
    var targets = ResolveTargets(form["ids"], allOnline);
    var jobs = new List<JobState>();
    foreach (var d in targets)
        jobs.Add(await QueueJob(d, "pushFile", new() { ["url"] = $"{PublicBaseUrl(c)}/agent/files/{key}", ["target"] = target }));
    return Results.Ok(new { count = jobs.Count, jobs });
});

app.MapPost("/api/bulk/install-apk", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var allOnline = bool.TryParse(form["allOnline"], out var ao) && ao;
    if (file is null) return Results.BadRequest("file required");

    var key = await SaveUpload(file);
    var targets = ResolveTargets(form["ids"], allOnline);
    var jobs = new List<JobState>();
    foreach (var d in targets)
        jobs.Add(await QueueJob(d, "installApk", new() { ["url"] = $"{PublicBaseUrl(c)}/agent/files/{key}" }));
    return Results.Ok(new { count = jobs.Count, jobs });
});

app.MapGet("/api/devices/{id}/screenshot", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(d.LastScreenshotBase64)) return Results.NoContent();
    try { return Results.File(Convert.FromBase64String(d.LastScreenshotBase64), "image/png"); }
    catch { return Results.Problem("invalid screenshot payload"); }
});

app.MapGet("/agent/files/{key}", (string key) =>
{
    var safe = Path.GetFileName(key);
    var path = Path.Combine(uploadDir, safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
});

app.Map("/agent", async c =>
{
    if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }

    var id = c.Request.Query["deviceId"].ToString();
    var token = c.Request.Query["token"].ToString();
    if (string.IsNullOrWhiteSpace(id) || !devices.TryGetValue(id, out var device) || !CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(device.EnrollmentToken), Encoding.UTF8.GetBytes(token)))
    {
        c.Response.StatusCode = 401;
        return;
    }

    device.LastIp = c.Connection.RemoteIpAddress?.ToString() ?? "";
    device.LastSeen = DateTimeOffset.UtcNow;

    using var ws = await c.WebSockets.AcceptWebSocketAsync();
    var channel = Channel.CreateUnbounded<string>();
    if (sessions.TryRemove(id, out var old)) old.Writer.TryComplete();
    sessions[id] = channel;

    foreach (var state in device.Jobs.Values.Where(j =>
                 j.Status == "queued" || (j.Status == "sent" && DateTimeOffset.UtcNow - j.UpdatedAt > TimeSpan.FromMinutes(2))))
    {
        var args = state.Args ?? new Dictionary<string, string>();
        var envelope = new JobEnvelope(state.Id, state.Type, args);
        await channel.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job = envelope }, json));
        device.Jobs[state.Id] = state with { Status = "sent", UpdatedAt = DateTimeOffset.UtcNow };
    }
    await SaveAsync();

    var sender = Task.Run(async () =>
    {
        await foreach (var msg in channel.Reader.ReadAllAsync())
        {
            if (ws.State != WebSocketState.Open) break;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    });

    var buf = new byte[256 * 1024];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult? r;
            do
            {
                r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 16 * 1024 * 1024) throw new InvalidDataException("agent message too large");
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close) break;

            device.LastSeen = DateTimeOffset.UtcNow;
            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            if (type == "hello")
            {
                device.Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? device.Name : device.Name;
                device.City = root.TryGetProperty("city", out var city) ? city.GetString() ?? device.City : device.City;
                device.Site = root.TryGetProperty("site", out var site) ? site.GetString() ?? device.Site : device.Site;
                device.Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "";
                device.AndroidVersion = root.TryGetProperty("androidVersion", out var av) ? av.GetString() ?? "" : "";
                device.AgentVersion = root.TryGetProperty("agentVersion", out var ag) ? ag.GetString() ?? "" : "";
                device.RootAvailable = root.TryGetProperty("rootAvailable", out var ra) && ra.GetBoolean();
                await SaveAsync();
            }
            else if (type == "telemetry" && root.TryGetProperty("data", out var tel))
            {
                device.Telemetry = JsonSerializer.Deserialize<Dictionary<string, object>>(tel.GetRawText(), json) ?? new();
            }
            else if (type == "jobAck")
            {
                var jid = root.TryGetProperty("jobId", out var jidEl) ? jidEl.GetString() ?? "" : "";
                if (device.Jobs.TryGetValue(jid, out var oldState))
                    device.Jobs[jid] = oldState with { Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
            }
            else if (type == "jobResult")
            {
                var jid = root.TryGetProperty("jobId", out var jidEl) ? jidEl.GetString() ?? "" : "";
                var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
                var output = root.TryGetProperty("output", out var outp) ? outp.GetString() : null;

                if (root.TryGetProperty("binaryBase64", out var bin) && bin.ValueKind == JsonValueKind.String)
                {
                    device.LastScreenshotBase64 = bin.GetString();
                    device.LastScreenshotAt = DateTimeOffset.UtcNow;
                    output = $"screenshot received at {device.LastScreenshotAt:O}";
                }

                var old = device.Jobs.TryGetValue(jid, out var oldState) ? oldState : null;
                device.Jobs[jid] = new JobState(jid, old?.Type ?? "unknown", ok ? "done" : "failed", output,
                    DateTimeOffset.UtcNow, old?.Args);
                await SaveAsync();
            }
        }
    }
    catch { }
    finally
    {
        if (sessions.TryGetValue(id, out var active) && ReferenceEquals(active, channel)) sessions.TryRemove(id, out _);
        channel.Writer.TryComplete();
        try { await sender; } catch { }
    }
});

app.Run();

async Task<string> SaveUpload(IFormFile file)
{
    var key = Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(file.FileName);
    var path = Path.Combine(uploadDir, key);
    await using var fs = File.Create(path);
    await file.CopyToAsync(fs);
    return key;
}

static ConcurrentDictionary<string, Device> LoadDevices(string path, JsonSerializerOptions json)
{
    try
    {
        if (!File.Exists(path)) return new();
        var list = JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(path), json) ?? [];
        return new ConcurrentDictionary<string, Device>(list.ToDictionary(x => x.Id, x => x));
    }
    catch { return new(); }
}

record LoginRequest(string Username, string Password);
record CreateDeviceRequest(string? Id, string? Name, string? City, string? Site, string? InitialIp);
record JobRequest(string Type, Dictionary<string, string>? Args);
record BulkJobRequest(string[]? Ids, bool AllOnline, string Type, Dictionary<string, string>? Args);
record JobEnvelope(string Id, string Type, Dictionary<string, string> Args);
record JobState(string Id, string Type, string Status, string? Output, DateTimeOffset UpdatedAt, Dictionary<string, string>? Args = null);

sealed class Device
{
    public string Id { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Site { get; set; } = "";
    public string InitialIp { get; set; } = "";
    public string LastIp { get; set; } = "";
    public string Model { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public bool RootAvailable { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, object> Telemetry { get; set; } = new();
    public ConcurrentDictionary<string, JobState> Jobs { get; set; } = new();
    [JsonIgnore] public string? LastScreenshotBase64 { get; set; }
    [JsonIgnore] public DateTimeOffset? LastScreenshotAt { get; set; }
}