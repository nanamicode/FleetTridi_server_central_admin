using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FLEETTRIDI_URLS") ?? "http://0.0.0.0:8787");
var app = builder.Build();
app.UseWebSockets();

const string AdminUser = "nanamicode";
const string AdminPassword = "veralucia12";
const string AdminToken = "fleettridi-dev-admin-token";

var devices = new ConcurrentDictionary<string, Device>();
var sessions = new ConcurrentDictionary<string, Channel<string>>();
Directory.CreateDirectory("data/uploads");

bool IsAdmin(HttpContext c) =>
    c.Request.Headers.TryGetValue("X-Fleet-Token", out var t) && t == AdminToken;

app.MapGet("/", () => Results.Ok(new { name = "FleetTridi Server", version = "0.1.0" }));

app.MapPost("/api/login", async (HttpContext c) =>
{
    var login = await JsonSerializer.DeserializeAsync<LoginRequest>(c.Request.Body);
    return login?.Username == AdminUser && login.Password == AdminPassword
        ? Results.Ok(new { token = AdminToken })
        : Results.Unauthorized();
});

app.MapGet("/api/devices", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var now = DateTimeOffset.UtcNow;
    return Results.Ok(devices.Values.Select(d => new {
        d.Id, d.Name, d.City, d.Site, d.LastIp, d.Model, d.AndroidVersion,
        d.AgentVersion, d.LastSeen, online = (now - d.LastSeen).TotalSeconds < 30,
        d.Telemetry
    }).OrderBy(x => x.City).ThenBy(x => x.Name));
});

app.MapPost("/api/devices", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var req = await JsonSerializer.DeserializeAsync<CreateDeviceRequest>(c.Request.Body);
    if (req is null) return Results.BadRequest();
    var id = string.IsNullOrWhiteSpace(req.Id) ? Guid.NewGuid().ToString("N") : req.Id.Trim();
    var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    var d = new Device { Id = id, Name = req.Name ?? id, City = req.City ?? "", Site = req.Site ?? "", EnrollmentToken = token };
    devices[id] = d;
    return Results.Ok(new { deviceId = id, enrollmentToken = token });
});

app.MapPost("/api/devices/{id}/jobs", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    var req = await JsonSerializer.DeserializeAsync<JobRequest>(c.Request.Body);
    if (req is null || string.IsNullOrWhiteSpace(req.Type)) return Results.BadRequest();
    var job = new JobEnvelope(Guid.NewGuid().ToString("N"), req.Type, req.Args ?? new());
    d.Jobs[job.Id] = new JobState(job.Id, job.Type, "queued", null, DateTimeOffset.UtcNow);
    if (sessions.TryGetValue(id, out var ch)) await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job }));
    return Results.Ok(d.Jobs[job.Id]);
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
    var key = Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(file.FileName);
    var path = Path.Combine("data/uploads", key);
    await using (var fs = File.Create(path)) await file.CopyToAsync(fs);
    var baseUrl = $"{c.Request.Scheme}://{c.Request.Host}";
    var job = new JobEnvelope(Guid.NewGuid().ToString("N"), "pushFile", new() {
        ["url"] = $"{baseUrl}/agent/files/{key}",
        ["target"] = target
    });
    d.Jobs[job.Id] = new JobState(job.Id, job.Type, "queued", null, DateTimeOffset.UtcNow);
    if (sessions.TryGetValue(id, out var ch)) await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job }));
    return Results.Ok(d.Jobs[job.Id]);
});

app.MapPost("/api/devices/{id}/install-apk", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("file required");
    var key = Guid.NewGuid().ToString("N") + "_" + Path.GetFileName(file.FileName);
    var path = Path.Combine("data/uploads", key);
    await using (var fs = File.Create(path)) await file.CopyToAsync(fs);
    var baseUrl = $"{c.Request.Scheme}://{c.Request.Host}";
    var job = new JobEnvelope(Guid.NewGuid().ToString("N"), "installApk", new() { ["url"] = $"{baseUrl}/agent/files/{key}" });
    d.Jobs[job.Id] = new JobState(job.Id, job.Type, "queued", null, DateTimeOffset.UtcNow);
    if (sessions.TryGetValue(id, out var ch)) await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job }));
    return Results.Ok(d.Jobs[job.Id]);
});

app.MapGet("/agent/files/{key}", (string key) =>
{
    var safe = Path.GetFileName(key);
    var path = Path.Combine("data/uploads", safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
});

app.Map("/agent", async c =>
{
    if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }
    var id = c.Request.Query["deviceId"].ToString();
    var token = c.Request.Query["token"].ToString();
    if (string.IsNullOrWhiteSpace(id) || !devices.TryGetValue(id, out var device) || device.EnrollmentToken != token) {
        c.Response.StatusCode = 401; return;
    }

    device.LastIp = c.Connection.RemoteIpAddress?.ToString() ?? "";
    device.LastSeen = DateTimeOffset.UtcNow;
    using var ws = await c.WebSockets.AcceptWebSocketAsync();
    var channel = Channel.CreateUnbounded<string>();
    sessions[id] = channel;

    var sender = Task.Run(async () => {
        await foreach (var msg in channel.Reader.ReadAllAsync()) {
            if (ws.State != WebSocketState.Open) break;
            var bytes = Encoding.UTF8.GetBytes(msg);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
    });

    var buf = new byte[2 * 1024 * 1024];
    try {
        while (ws.State == WebSocketState.Open) {
            using var ms = new MemoryStream();
            WebSocketReceiveResult? r;
            do {
                r = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Close) break;
            device.LastSeen = DateTimeOffset.UtcNow;
            using var doc = JsonDocument.Parse(ms.ToArray());
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "hello") {
                device.Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? device.Name : device.Name;
                device.City = root.TryGetProperty("city", out var city) ? city.GetString() ?? device.City : device.City;
                device.Site = root.TryGetProperty("site", out var site) ? site.GetString() ?? device.Site : device.Site;
                device.Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "";
                device.AndroidVersion = root.TryGetProperty("androidVersion", out var av) ? av.GetString() ?? "" : "";
                device.AgentVersion = root.TryGetProperty("agentVersion", out var ag) ? ag.GetString() ?? "" : "";
            } else if (type == "telemetry" && root.TryGetProperty("data", out var tel)) {
                device.Telemetry = JsonSerializer.Deserialize<Dictionary<string, object>>(tel.GetRawText()) ?? new();
            } else if (type == "jobResult") {
                var jid = root.GetProperty("jobId").GetString() ?? "";
                var ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();
                var output = root.TryGetProperty("output", out var outp) ? outp.GetString() : null;
                device.Jobs[jid] = new JobState(jid, device.Jobs.TryGetValue(jid, out var old) ? old.Type : "unknown", ok ? "done" : "failed", output, DateTimeOffset.UtcNow);
            }
        }
    } finally {
        sessions.TryRemove(id, out _);
        channel.Writer.TryComplete();
        try { await sender; } catch { }
    }
});

app.Run();

record LoginRequest(string Username, string Password);
record CreateDeviceRequest(string? Id, string? Name, string? City, string? Site);
record JobRequest(string Type, Dictionary<string,string>? Args);
record JobEnvelope(string Id, string Type, Dictionary<string,string> Args);
record JobState(string Id, string Type, string Status, string? Output, DateTimeOffset UpdatedAt);
sealed class Device {
    public string Id { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Site { get; set; } = "";
    public string LastIp { get; set; } = "";
    public string Model { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string,object> Telemetry { get; set; } = new();
    public ConcurrentDictionary<string,JobState> Jobs { get; } = new();
}