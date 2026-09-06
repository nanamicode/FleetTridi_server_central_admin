using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var devMode = Environment.GetEnvironmentVariable("FLEETTRIDI_DEV_MODE") == "1";
var defaultUrls = devMode ? "http://127.0.0.1:8787" : "http://0.0.0.0:8787";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("FLEETTRIDI_URLS") ?? defaultUrls);
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

var adminUser = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_USER") ?? "nanamicode";
var adminPassword = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_PASSWORD");
if (string.IsNullOrWhiteSpace(adminPassword))
{
    if (!devMode)
        throw new InvalidOperationException("FLEETTRIDI_ADMIN_PASSWORD precisa ser definida fora do modo de desenvolvimento.");
    adminPassword = "fleettridi-local";
}

var adminToken = Environment.GetEnvironmentVariable("FLEETTRIDI_ADMIN_TOKEN")
    ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

var allowLegacyAgentAuth = devMode || Environment.GetEnvironmentVariable("FLEETTRIDI_ALLOW_LEGACY_AGENT_AUTH") == "1";
var allowLegacyFileDownloads = devMode || Environment.GetEnvironmentVariable("FLEETTRIDI_ALLOW_LEGACY_FILE_DOWNLOADS") == "1";

var dataDir = Environment.GetEnvironmentVariable("FLEETTRIDI_DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
var uploadDir = Path.Combine(dataDir, "uploads");
var devicesPath = Path.Combine(dataDir, "devices.json");
var releasesPath = Path.Combine(dataDir, "releases.json");
var auditPath = Path.Combine(dataDir, "audit.jsonl");
Directory.CreateDirectory(uploadDir);

var devices = LoadDevices(devicesPath, json);
var releases = LoadReleases(releasesPath, json);
var sessions = new ConcurrentDictionary<string, Channel<string>>();
var saveGate = new SemaphoreSlim(1, 1);
var auditGate = new SemaphoreSlim(1, 1);

var allowedJobs = new HashSet<string>(StringComparer.Ordinal)
{
    "installApk",
    "syncCreative",
    "pushFile",
    "restartAudience",
    "rebootDevice",
    "keyevent",
    "tap",
    "swipe",
    "captureScreen"
};

bool SafeEquals(string a, string b)
{
    var aa = Encoding.UTF8.GetBytes(a ?? "");
    var bb = Encoding.UTF8.GetBytes(b ?? "");
    return aa.Length == bb.Length && CryptographicOperations.FixedTimeEquals(aa, bb);
}

bool IsAdmin(HttpContext c) =>
    c.Request.Headers.TryGetValue("X-Fleet-Token", out var t) && SafeEquals(t.ToString(), adminToken);

bool DeviceTokenMatches(Device d, string token) => SafeEquals(d.EnrollmentToken, token);

bool TryAuthenticateDevice(HttpContext c, out Device? device)
{
    device = null;
    var id = c.Request.Headers["X-Fleet-Device-Id"].ToString();
    var token = c.Request.Headers["X-Fleet-Device-Token"].ToString();
    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(token)) return false;
    if (!devices.TryGetValue(id, out var d) || !DeviceTokenMatches(d, token)) return false;
    device = d;
    return true;
}

bool IsOnline(Device d) => (DateTimeOffset.UtcNow - d.LastSeen).TotalSeconds < 35;

string PublicBaseUrl(HttpContext c) =>
    (Environment.GetEnvironmentVariable("FLEETTRIDI_PUBLIC_URL") ?? $"{c.Request.Scheme}://{c.Request.Host}").TrimEnd('/');

string FileUrl(HttpContext c, string key) => $"{PublicBaseUrl(c)}/agent/files/{Uri.EscapeDataString(key)}";

async Task SaveDevicesAsync()
{
    await saveGate.WaitAsync();
    try
    {
        var temp = devicesPath + ".tmp";
        await using (var fs = File.Create(temp))
            await JsonSerializer.SerializeAsync(fs, devices.Values.OrderBy(x => x.Id).ToList(), json);
        File.Move(temp, devicesPath, true);
    }
    finally { saveGate.Release(); }
}

async Task SaveReleasesAsync()
{
    await saveGate.WaitAsync();
    try
    {
        var temp = releasesPath + ".tmp";
        await using (var fs = File.Create(temp))
            await JsonSerializer.SerializeAsync(fs, releases.Values.OrderByDescending(x => x.CreatedAt).ToList(), json);
        File.Move(temp, releasesPath, true);
    }
    finally { saveGate.Release(); }
}

async Task AuditAsync(string action, string target, string? details = null)
{
    var entry = new AuditEntry(DateTimeOffset.UtcNow, action, target, details ?? "");
    var line = JsonSerializer.Serialize(entry, json).Replace(Environment.NewLine, "");
    await auditGate.WaitAsync();
    try { await File.AppendAllTextAsync(auditPath, line + Environment.NewLine); }
    finally { auditGate.Release(); }
}

async Task<JobState> QueueJob(Device d, string type, Dictionary<string, string>? args)
{
    if (!allowedJobs.Contains(type)) throw new InvalidOperationException("Tipo de job não permitido.");

    var job = new JobEnvelope(Guid.NewGuid().ToString("N"), type, args ?? new());
    var state = new JobState(job.Id, job.Type, "queued", null, DateTimeOffset.UtcNow, job.Args);
    d.Jobs[job.Id] = state;

    if (sessions.TryGetValue(d.Id, out var ch))
    {
        await ch.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job }, json));
        state = state with { Status = "sent", UpdatedAt = DateTimeOffset.UtcNow };
        d.Jobs[job.Id] = state;
    }

    await SaveDevicesAsync();
    await AuditAsync("job.queued", d.Id, type);
    return state;
}

IEnumerable<Device> ResolveTargets(TargetSelector selector)
{
    var q = devices.Values.AsEnumerable();
    var ids = selector.Ids?.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
    var hasFilters = ids.Count > 0 ||
                     !string.IsNullOrWhiteSpace(selector.City) ||
                     !string.IsNullOrWhiteSpace(selector.Site) ||
                     !string.IsNullOrWhiteSpace(selector.Channel) ||
                     !string.IsNullOrWhiteSpace(selector.Tag);

    if (ids.Count > 0) q = q.Where(d => ids.Contains(d.Id));
    if (!string.IsNullOrWhiteSpace(selector.City))
        q = q.Where(d => string.Equals(d.City, selector.City.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(selector.Site))
        q = q.Where(d => string.Equals(d.Site, selector.Site.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(selector.Channel))
        q = q.Where(d => string.Equals(d.UpdateChannel, selector.Channel.Trim(), StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(selector.Tag))
        q = q.Where(d => d.Tags.Any(t => string.Equals(t, selector.Tag.Trim(), StringComparison.OrdinalIgnoreCase)));

    if (selector.AllOnline || selector.OnlyOnline) q = q.Where(IsOnline);
    if (!hasFilters && !selector.AllOnline) return Array.Empty<Device>();

    return q.OrderBy(d => d.City).ThenBy(d => d.Site).ThenBy(d => d.Name).ToArray();
}

TargetSelector SelectorFromForm(IFormCollection form)
{
    var ids = form["ids"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return new TargetSelector
    {
        Ids = ids,
        AllOnline = bool.TryParse(form["allOnline"], out var ao) && ao,
        OnlyOnline = bool.TryParse(form["onlyOnline"], out var oo) && oo,
        City = form["city"].ToString(),
        Site = form["site"].ToString(),
        Channel = form["channel"].ToString(),
        Tag = form["tag"].ToString()
    };
}

app.MapGet("/", () => Results.Ok(new
{
    name = "FleetTridi Server",
    version = "0.4.0",
    devices = devices.Count,
    releases = releases.Count,
    devMode,
    legacyAgentAuth = allowLegacyAgentAuth,
    legacyFileDownloads = allowLegacyFileDownloads
}));

app.MapPost("/api/login", async (HttpContext c) =>
{
    var login = await JsonSerializer.DeserializeAsync<LoginRequest>(c.Request.Body, json);
    var ok = login is not null && SafeEquals(login.Username ?? "", adminUser) && SafeEquals(login.Password ?? "", adminPassword);
    if (!ok)
    {
        await AuditAsync("auth.failed", c.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return Results.Unauthorized();
    }

    await AuditAsync("auth.login", c.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    return Results.Ok(new { token = adminToken });
});

app.MapGet("/api/devices", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    return Results.Ok(devices.Values.Select(d => new
    {
        d.Id, d.Name, d.City, d.Site, d.Tags, d.UpdateChannel, d.InitialIp, d.LastIp,
        d.Model, d.AndroidVersion, d.AgentVersion, d.PrivilegeMode, d.RootAvailable,
        d.AudiencePackage, d.AudienceVersion, d.LastSeen, online = IsOnline(d), d.Telemetry
    }).OrderBy(x => x.City).ThenBy(x => x.Site).ThenBy(x => x.Name));
});

app.MapGet("/api/groups", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var cities = devices.Values.GroupBy(d => string.IsNullOrWhiteSpace(d.City) ? "(sem cidade)" : d.City)
        .Select(g => new { name = g.Key, count = g.Count(), online = g.Count(IsOnline) }).OrderBy(x => x.name);
    var sites = devices.Values.GroupBy(d => new { d.City, d.Site })
        .Select(g => new { city = g.Key.City, site = g.Key.Site, count = g.Count(), online = g.Count(IsOnline) })
        .OrderBy(x => x.city).ThenBy(x => x.site);
    var channels = devices.Values.GroupBy(d => string.IsNullOrWhiteSpace(d.UpdateChannel) ? "stable" : d.UpdateChannel)
        .Select(g => new { name = g.Key, count = g.Count(), online = g.Count(IsOnline) }).OrderBy(x => x.name);
    return Results.Ok(new { cities, sites, channels });
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
        UpdateChannel = string.IsNullOrWhiteSpace(req.UpdateChannel) ? "stable" : req.UpdateChannel.Trim(),
        Tags = req.Tags?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new(),
        EnrollmentToken = token
    };
    devices[id] = d;
    await SaveDevicesAsync();
    await AuditAsync("device.created", id, d.Name);
    return Results.Ok(new
    {
        deviceId = id,
        enrollmentToken = token,
        d.Name, d.City, d.Site, d.InitialIp, d.UpdateChannel, d.Tags
    });
});

app.MapPatch("/api/devices/{id}", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();

    var req = await JsonSerializer.DeserializeAsync<UpdateDeviceRequest>(c.Request.Body, json);
    if (req is null) return Results.BadRequest();

    if (req.Name is not null) d.Name = req.Name.Trim();
    if (req.City is not null) d.City = req.City.Trim();
    if (req.Site is not null) d.Site = req.Site.Trim();
    if (req.UpdateChannel is not null) d.UpdateChannel = string.IsNullOrWhiteSpace(req.UpdateChannel) ? "stable" : req.UpdateChannel.Trim();
    if (req.Tags is not null)
        d.Tags = req.Tags.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    await SaveDevicesAsync();
    await AuditAsync("device.updated", id);
    return Results.Ok(new { d.Id, d.Name, d.City, d.Site, d.UpdateChannel, d.Tags });
});

app.MapGet("/api/devices/{id}/enrollment", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    return devices.TryGetValue(id, out var d)
        ? Results.Ok(new { deviceId = d.Id, enrollmentToken = d.EnrollmentToken, d.Name, d.City, d.Site, d.InitialIp })
        : Results.NotFound();
});

app.MapPost("/api/devices/{id}/rotate-token", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    d.EnrollmentToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    if (sessions.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    await SaveDevicesAsync();
    await AuditAsync("device.token_rotated", id);
    return Results.Ok(new { deviceId = id, enrollmentToken = d.EnrollmentToken });
});

app.MapDelete("/api/devices/{id}", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryRemove(id, out _)) return Results.NotFound();
    if (sessions.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    await SaveDevicesAsync();
    await AuditAsync("device.deleted", id);
    return Results.Ok();
});

app.MapGet("/api/devices/{id}/jobs", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    return Results.Ok(d.Jobs.Values.OrderByDescending(j => j.UpdatedAt).Take(200));
});

app.MapPost("/api/devices/{id}/jobs", async (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    var req = await JsonSerializer.DeserializeAsync<JobRequest>(c.Request.Body, json);
    if (req is null || string.IsNullOrWhiteSpace(req.Type) || !allowedJobs.Contains(req.Type))
        return Results.BadRequest("job type not allowed");
    return Results.Ok(await QueueJob(d, req.Type, req.Args));
});

app.MapPost("/api/bulk/jobs", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    var req = await JsonSerializer.DeserializeAsync<BulkJobRequest>(c.Request.Body, json);
    if (req is null || string.IsNullOrWhiteSpace(req.Type) || !allowedJobs.Contains(req.Type))
        return Results.BadRequest("job type not allowed");

    var selector = new TargetSelector
    {
        Ids = req.Ids,
        AllOnline = req.AllOnline,
        OnlyOnline = req.OnlyOnline,
        City = req.City,
        Site = req.Site,
        Channel = req.Channel,
        Tag = req.Tag
    };
    var targets = ResolveTargets(selector).ToArray();
    var results = new List<JobState>();
    foreach (var d in targets) results.Add(await QueueJob(d, req.Type, req.Args));
    return Results.Ok(new { count = results.Count, deviceIds = targets.Select(x => x.Id), jobs = results });
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

    var upload = await SaveUpload(file);
    return Results.Ok(await QueueJob(d, "pushFile", new()
    {
        ["url"] = FileUrl(c, upload.Key),
        ["target"] = target,
        ["sha256"] = upload.Sha256
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

    var upload = await SaveUpload(file);
    return Results.Ok(await QueueJob(d, "installApk", new()
    {
        ["url"] = FileUrl(c, upload.Key),
        ["sha256"] = upload.Sha256,
        ["packageName"] = form["packageName"].ToString(),
        ["expectedVersion"] = form["version"].ToString()
    }));
});

app.MapPost("/api/bulk/upload", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var target = form["target"].ToString();
    if (file is null || string.IsNullOrWhiteSpace(target)) return Results.BadRequest("file + target required");

    var upload = await SaveUpload(file);
    var targets = ResolveTargets(SelectorFromForm(form)).ToArray();
    var jobs = new List<JobState>();
    foreach (var d in targets)
        jobs.Add(await QueueJob(d, "pushFile", new()
        {
            ["url"] = FileUrl(c, upload.Key),
            ["target"] = target,
            ["sha256"] = upload.Sha256
        }));
    return Results.Ok(new { count = jobs.Count, deviceIds = targets.Select(x => x.Id), jobs });
});

app.MapPost("/api/bulk/install-apk", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null) return Results.BadRequest("file required");

    var upload = await SaveUpload(file);
    var targets = ResolveTargets(SelectorFromForm(form)).ToArray();
    var jobs = new List<JobState>();
    foreach (var d in targets)
        jobs.Add(await QueueJob(d, "installApk", new()
        {
            ["url"] = FileUrl(c, upload.Key),
            ["sha256"] = upload.Sha256,
            ["packageName"] = form["packageName"].ToString(),
            ["expectedVersion"] = form["version"].ToString()
        }));
    return Results.Ok(new { count = jobs.Count, deviceIds = targets.Select(x => x.Id), sha256 = upload.Sha256, jobs });
});

app.MapGet("/api/releases", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    return Results.Ok(releases.Values.OrderByDescending(x => x.CreatedAt).Select(r => new
    {
        r.Id, r.Version, r.PackageName, r.Channel, r.Notes, r.OriginalName,
        r.Sha256, r.SizeBytes, r.CreatedAt
    }));
});

app.MapPost("/api/releases", async (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!c.Request.HasFormContentType) return Results.BadRequest("multipart/form-data required");

    var form = await c.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    var version = form["version"].ToString().Trim();
    if (file is null || string.IsNullOrWhiteSpace(version)) return Results.BadRequest("file + version required");
    if (!file.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest("APK required");

    var upload = await SaveUpload(file);
    var release = new ApkRelease
    {
        Id = Guid.NewGuid().ToString("N"),
        Version = version,
        PackageName = string.IsNullOrWhiteSpace(form["packageName"]) ? "com.tridi.audience" : form["packageName"].ToString().Trim(),
        Channel = string.IsNullOrWhiteSpace(form["channel"]) ? "stable" : form["channel"].ToString().Trim(),
        Notes = form["notes"].ToString().Trim(),
        FileKey = upload.Key,
        OriginalName = upload.OriginalName,
        Sha256 = upload.Sha256,
        SizeBytes = upload.Size,
        CreatedAt = DateTimeOffset.UtcNow
    };
    releases[release.Id] = release;
    await SaveReleasesAsync();
    await AuditAsync("release.created", release.Id, release.Version);
    return Results.Ok(release);
});

app.MapPost("/api/releases/{releaseId}/deploy", async (string releaseId, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!releases.TryGetValue(releaseId, out var release)) return Results.NotFound();

    var req = await JsonSerializer.DeserializeAsync<DeployReleaseRequest>(c.Request.Body, json) ?? new DeployReleaseRequest();
    var selector = new TargetSelector
    {
        Ids = req.Ids,
        AllOnline = req.AllOnline,
        OnlyOnline = req.OnlyOnline,
        City = req.City,
        Site = req.Site,
        Channel = req.Channel,
        Tag = req.Tag
    };

    var targets = ResolveTargets(selector).ToArray();
    var percent = Math.Clamp(req.Percent <= 0 ? 100 : req.Percent, 1, 100);
    var take = targets.Length == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(targets.Length * (percent / 100.0)));
    targets = targets.OrderBy(x => x.Id, StringComparer.Ordinal).Take(take).ToArray();

    if (req.DryRun)
        return Results.Ok(new
        {
            dryRun = true,
            release = new { release.Id, release.Version, release.Channel, release.Sha256 },
            percent,
            count = targets.Length,
            devices = targets.Select(x => new { x.Id, x.Name, x.City, x.Site, x.AudienceVersion })
        });

    var rolloutId = Guid.NewGuid().ToString("N");
    var jobs = new List<JobState>();
    foreach (var d in targets)
    {
        jobs.Add(await QueueJob(d, "installApk", new()
        {
            ["url"] = FileUrl(c, release.FileKey),
            ["sha256"] = release.Sha256,
            ["packageName"] = release.PackageName,
            ["expectedVersion"] = release.Version,
            ["releaseId"] = release.Id,
            ["rolloutId"] = rolloutId
        }));
    }

    await AuditAsync("release.deployed", release.Id, $"rollout={rolloutId};count={jobs.Count};percent={percent}");
    return Results.Ok(new
    {
        rolloutId,
        release = new { release.Id, release.Version, release.Channel, release.Sha256 },
        percent,
        count = jobs.Count,
        deviceIds = targets.Select(x => x.Id),
        jobs
    });
});

app.MapDelete("/api/releases/{releaseId}", async (string releaseId, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!releases.TryRemove(releaseId, out var release)) return Results.NotFound();

    await SaveReleasesAsync();
    await AuditAsync("release.deleted", releaseId, release.Version);
    return Results.Ok();
});

app.MapGet("/api/audit", (HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!File.Exists(auditPath)) return Results.Ok(Array.Empty<AuditEntry>());

    var limit = int.TryParse(c.Request.Query["limit"], out var parsed) ? Math.Clamp(parsed, 1, 1000) : 200;
    var entries = File.ReadLines(auditPath)
        .TakeLast(limit)
        .Select(line =>
        {
            try { return JsonSerializer.Deserialize<AuditEntry>(line, json); }
            catch { return null; }
        })
        .Where(x => x is not null)
        .Reverse()
        .ToArray();
    return Results.Ok(entries);
});

app.MapGet("/api/devices/{id}/screenshot", (string id, HttpContext c) =>
{
    if (!IsAdmin(c)) return Results.Unauthorized();
    if (!devices.TryGetValue(id, out var d)) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(d.LastScreenshotBase64)) return Results.NoContent();
    try { return Results.File(Convert.FromBase64String(d.LastScreenshotBase64), "image/png"); }
    catch { return Results.Problem("invalid screenshot payload"); }
});

app.MapGet("/agent/files/{key}", (string key, HttpContext c) =>
{
    if (!TryAuthenticateDevice(c, out _) && !allowLegacyFileDownloads) return Results.Unauthorized();
    var safe = Path.GetFileName(key);
    var path = Path.Combine(uploadDir, safe);
    return File.Exists(path) ? Results.File(path, "application/octet-stream") : Results.NotFound();
});

app.Map("/agent", async c =>
{
    if (!c.WebSockets.IsWebSocketRequest) { c.Response.StatusCode = 400; return; }

    var id = c.Request.Query["deviceId"].ToString();
    var token = c.Request.Headers["X-Fleet-Device-Token"].ToString();
    if (string.IsNullOrWhiteSpace(token) && allowLegacyAgentAuth)
        token = c.Request.Query["token"].ToString();

    if (string.IsNullOrWhiteSpace(id) || !devices.TryGetValue(id, out var device) || !DeviceTokenMatches(device, token))
    {
        c.Response.StatusCode = 401;
        return;
    }

    device.LastIp = c.Connection.RemoteIpAddress?.ToString() ?? "";
    device.LastSeen = DateTimeOffset.UtcNow;

    using var ws = await c.WebSockets.AcceptWebSocketAsync();
    var channel = Channel.CreateUnbounded<string>();
    if (sessions.TryRemove(id, out var oldChannel)) oldChannel.Writer.TryComplete();
    sessions[id] = channel;

    foreach (var state in device.Jobs.Values.Where(j =>
                 j.Status == "queued" || (j.Status == "sent" && DateTimeOffset.UtcNow - j.UpdatedAt > TimeSpan.FromMinutes(2))))
    {
        var args = state.Args ?? new Dictionary<string, string>();
        var envelope = new JobEnvelope(state.Id, state.Type, args);
        await channel.Writer.WriteAsync(JsonSerializer.Serialize(new { type = "job", job = envelope }, json));
        device.Jobs[state.Id] = state with { Status = "sent", UpdatedAt = DateTimeOffset.UtcNow };
    }
    await SaveDevicesAsync();

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
                if (string.IsNullOrWhiteSpace(device.Name))
                    device.Name = root.TryGetProperty("name", out var n) ? n.GetString() ?? device.Id : device.Id;
                if (string.IsNullOrWhiteSpace(device.City))
                    device.City = root.TryGetProperty("city", out var city) ? city.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(device.Site))
                    device.Site = root.TryGetProperty("site", out var site) ? site.GetString() ?? "" : "";

                device.Model = root.TryGetProperty("model", out var model) ? model.GetString() ?? "" : "";
                device.AndroidVersion = root.TryGetProperty("androidVersion", out var av) ? av.GetString() ?? "" : "";
                device.AgentVersion = root.TryGetProperty("agentVersion", out var ag) ? ag.GetString() ?? "" : "";
                device.PrivilegeMode = root.TryGetProperty("privilegeMode", out var pm) ? pm.GetString() ?? "unknown" : "unknown";
                device.RootAvailable = root.TryGetProperty("rootAvailable", out var ra) && ra.GetBoolean();
                device.AudiencePackage = root.TryGetProperty("audiencePackage", out var ap) ? ap.GetString() ?? "" : "";
                device.AudienceVersion = root.TryGetProperty("audienceVersion", out var aVer) ? aVer.GetString() ?? "" : "";
                await SaveDevicesAsync();
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

                if (root.TryGetProperty("audienceVersion", out var reportedVersion) && reportedVersion.ValueKind == JsonValueKind.String)
                    device.AudienceVersion = reportedVersion.GetString() ?? device.AudienceVersion;

                var old = device.Jobs.TryGetValue(jid, out var oldState) ? oldState : null;
                device.Jobs[jid] = new JobState(jid, old?.Type ?? "unknown", ok ? "done" : "failed", output,
                    DateTimeOffset.UtcNow, old?.Args);
                await SaveDevicesAsync();
                await AuditAsync(ok ? "job.done" : "job.failed", device.Id, $"{jid}:{old?.Type}:{output}");
            }
        }
    }
    catch (Exception ex)
    {
        await AuditAsync("agent.disconnected_error", id, ex.Message);
    }
    finally
    {
        if (sessions.TryGetValue(id, out var active) && ReferenceEquals(active, channel)) sessions.TryRemove(id, out _);
        channel.Writer.TryComplete();
        try { await sender; } catch { }
    }
});

app.Run();

async Task<StoredUpload> SaveUpload(IFormFile file)
{
    var original = Path.GetFileName(file.FileName);
    var key = Guid.NewGuid().ToString("N") + "_" + original;
    var path = Path.Combine(uploadDir, key);

    await using (var fs = File.Create(path))
        await file.CopyToAsync(fs);

    await using var read = File.OpenRead(path);
    using var sha = SHA256.Create();
    var hash = await sha.ComputeHashAsync(read);
    return new StoredUpload(key, Convert.ToHexString(hash).ToLowerInvariant(), new FileInfo(path).Length, original);
}

static ConcurrentDictionary<string, Device> LoadDevices(string path, JsonSerializerOptions json)
{
    try
    {
        if (!File.Exists(path)) return new();
        var list = JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(path), json) ?? [];
        foreach (var d in list)
        {
            d.Tags ??= new();
            d.UpdateChannel = string.IsNullOrWhiteSpace(d.UpdateChannel) ? "stable" : d.UpdateChannel;
            d.Jobs ??= new();
        }
        return new ConcurrentDictionary<string, Device>(list.ToDictionary(x => x.Id, x => x));
    }
    catch { return new(); }
}

static ConcurrentDictionary<string, ApkRelease> LoadReleases(string path, JsonSerializerOptions json)
{
    try
    {
        if (!File.Exists(path)) return new();
        var list = JsonSerializer.Deserialize<List<ApkRelease>>(File.ReadAllText(path), json) ?? [];
        return new ConcurrentDictionary<string, ApkRelease>(list.ToDictionary(x => x.Id, x => x));
    }
    catch { return new(); }
}

record LoginRequest(string? Username, string? Password);
record CreateDeviceRequest(string? Id, string? Name, string? City, string? Site, string? InitialIp, string? UpdateChannel, string[]? Tags);
record UpdateDeviceRequest(string? Name, string? City, string? Site, string? UpdateChannel, string[]? Tags);
record JobRequest(string Type, Dictionary<string, string>? Args);
record BulkJobRequest(string[]? Ids, bool AllOnline, bool OnlyOnline, string? City, string? Site, string? Channel, string? Tag, string Type, Dictionary<string, string>? Args);
record JobEnvelope(string Id, string Type, Dictionary<string, string> Args);
record JobState(string Id, string Type, string Status, string? Output, DateTimeOffset UpdatedAt, Dictionary<string, string>? Args = null);
record StoredUpload(string Key, string Sha256, long Size, string OriginalName);
record AuditEntry(DateTimeOffset At, string Action, string Target, string Details);

sealed class TargetSelector
{
    public string[]? Ids { get; set; }
    public bool AllOnline { get; set; }
    public bool OnlyOnline { get; set; }
    public string? City { get; set; }
    public string? Site { get; set; }
    public string? Channel { get; set; }
    public string? Tag { get; set; }
}

sealed class DeployReleaseRequest
{
    public string[]? Ids { get; set; }
    public bool AllOnline { get; set; }
    public bool OnlyOnline { get; set; }
    public string? City { get; set; }
    public string? Site { get; set; }
    public string? Channel { get; set; }
    public string? Tag { get; set; }
    public int Percent { get; set; } = 100;
    public bool DryRun { get; set; }
}

sealed class ApkRelease
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string Channel { get; set; } = "stable";
    public string Notes { get; set; } = "";
    public string FileKey { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

sealed class Device
{
    public string Id { get; set; } = "";
    public string EnrollmentToken { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string Site { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string UpdateChannel { get; set; } = "stable";
    public string InitialIp { get; set; } = "";
    public string LastIp { get; set; } = "";
    public string Model { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public string AgentVersion { get; set; } = "";
    public string PrivilegeMode { get; set; } = "unknown";
    public bool RootAvailable { get; set; }
    public string AudiencePackage { get; set; } = "";
    public string AudienceVersion { get; set; } = "";
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
    public Dictionary<string, object> Telemetry { get; set; } = new();
    public ConcurrentDictionary<string, JobState> Jobs { get; set; } = new();
    [JsonIgnore] public string? LastScreenshotBase64 { get; set; }
    [JsonIgnore] public DateTimeOffset? LastScreenshotAt { get; set; }
}
