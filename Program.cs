using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 12_000_000);
var app = builder.Build();

// ================= config =================
string? geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
string? anthropicKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
bool mockAi = Environment.GetEnvironmentVariable("MOCK_AI") == "1";
string? supaUrl = Environment.GetEnvironmentVariable("SUPABASE_URL")?.TrimEnd('/');
if (supaUrl is not null && supaUrl.EndsWith("/rest/v1")) supaUrl = supaUrl[..^"/rest/v1".Length];
string? supaKey = Environment.GetEnvironmentVariable("SUPABASE_SERVICE_KEY");
string dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);
string playbookPath = Path.Combine(AppContext.BaseDirectory, "Data", "seed-playbooks.json");

if (!mockAi && geminiKey is null && anthropicKey is null)
    Console.WriteLine("WARNING: no GEMINI_API_KEY or ANTHROPIC_API_KEY — /api/extract will fail. Set MOCK_AI=1 for offline demo.");

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var fileJson = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

IStore store = (supaUrl != null && supaKey != null)
    ? new SupabaseStore(http, supaUrl, supaKey, webJson)
    : new FileStore(dataDir, fileJson);
Console.WriteLine($"RenewalBrain | Store: {(store is SupabaseStore ? "Supabase (persistent)" : "file (ephemeral on redeploy)")} | AI: {(mockAi ? "mock" : anthropicKey != null ? "anthropic" : geminiKey != null ? "gemini" : "none")}");

var playbooks = JsonSerializer.Deserialize<List<Playbook>>(File.ReadAllText(playbookPath), webJson) ?? new();
Console.WriteLine($"Loaded {playbooks.Count} renewal playbooks.");

// lead-time intelligence: days before expiry when action should start
var leadMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
    ["passport"] = 180, ["visa"] = 60, ["driving-license"] = 30, ["vehicle-insurance"] = 30,
    ["vehicle-revenue-license"] = 21, ["health-insurance"] = 30, ["warranty"] = 14,
    ["domain"] = 30, ["professional-cert"] = 60, ["prescription"] = 7, ["subscription"] = 7,
    ["id-card"] = 60, ["tenancy"] = 60, ["other"] = 30
};
int LeadFor(string? type, int? explicitLead) =>
    explicitLead is > 0 and < 400 ? explicitLead.Value
    : leadMap.TryGetValue(type ?? "other", out var d) ? d : 30;

// ================= rate limit (AI endpoints) =================
var hits = new ConcurrentDictionary<string, List<DateTime>>();
bool Limited(string ip)
{
    var now = DateTime.UtcNow;
    var list = hits.GetOrAdd(ip, _ => new List<DateTime>());
    lock (list) { list.RemoveAll(t => (now - t).TotalMinutes > 10); list.Add(now); return list.Count > 25; }
}

// ================= AI extraction =================
string ExtractPrompt() => $$"""
You are RenewalBrain, an assistant that reads a document (image or text) and extracts ONLY expiry-tracking metadata.
PRIVACY RULES (absolute): do NOT output document numbers, passport numbers, policy numbers, account numbers, or any long identifier. Only the metadata below.

Today's date: {{DateTime.UtcNow:yyyy-MM-dd}}.

Respond with ONLY a JSON object, no markdown fences:
{
 "title": "short human label, e.g. 'Vehicle insurance — Toyota Aqua' or 'UK Student visa'",
 "type": "one of: passport | visa | driving-license | vehicle-insurance | vehicle-revenue-license | health-insurance | warranty | domain | professional-cert | prescription | subscription | id-card | tenancy | other",
 "category": "one of: Travel | Vehicle | Home | Health | Work | Digital | Finance | Other",
 "issuer": "issuing org/company if visible, else empty string",
 "country": "ISO-like short country code if inferable (LK, UK, US...), else '*'",
 "person": "the holder's FIRST NAME ONLY if clearly visible, else empty string",
 "expiresOn": "YYYY-MM-DD — the expiry/valid-until/renewal-due date. If only issue date + validity period are visible, compute it. If truly absent, empty string",
 "leadDays": integer — how many days before expiry action should start for this document type (passports 180, visas 60, insurance 30, warranties 14, prescriptions 7...),
 "notes": "one short helpful sentence, e.g. what the renewal involves — never include identifiers",
 "confidence": 0-100 integer
}
""";

static string MockExtract() => JsonSerializer.Serialize(new
{
    title = "Vehicle insurance — Toyota Aqua",
    type = "vehicle-insurance",
    category = "Vehicle",
    issuer = "Ceylinco General",
    country = "LK",
    person = "Kalaru",
    expiresOn = DateTime.UtcNow.AddDays(45).ToString("yyyy-MM-dd"),
    leadDays = 30,
    notes = "Comprehensive cover; renewal quotes are best requested 3-4 weeks before expiry.",
    confidence = 93
});

async Task<string> AskAi(object[] parts)
{
    if (mockAi) return MockExtract();
    if (geminiKey is not null)
    {
        var body = new
        {
            contents = new[] { new { role = "user", parts } },
            generationConfig = new { maxOutputTokens = 900, thinkingConfig = new { thinkingBudget = 0 } }
        };
        var req = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-goog-api-key", geminiKey);
        var res = await http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"AI error {(int)res.StatusCode}");
        var doc = JsonNode.Parse(raw);
        var p = doc?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        return string.Concat(p?.Select(x => (string?)x?["text"]) ?? Enumerable.Empty<string?>());
    }
    else
    {
        // Anthropic path (text or image)
        var content = new List<object>();
        foreach (var part in parts)
        {
            var pj = JsonSerializer.SerializeToNode(part)!;
            if (pj["inline_data"] is JsonNode idn)
                content.Add(new { type = "image", source = new { type = "base64", media_type = (string?)idn["mime_type"], data = (string?)idn["data"] } });
            else if (pj["text"] is JsonNode tn)
                content.Add(new { type = "text", text = (string?)tn.AsValue().GetValue<string>() });
        }
        var body = new { model = "claude-sonnet-4-6", max_tokens = 900, messages = new[] { new { role = "user", content = content.ToArray() } } };
        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", anthropicKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        var res = await http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"AI error {(int)res.StatusCode}");
        var doc = JsonNode.Parse(text);
        return string.Concat(doc?["content"]?.AsArray().Where(b => (string?)b?["type"] == "text").Select(b => (string?)b?["text"]) ?? Enumerable.Empty<string?>());
    }
}

static string ExtractJson(string text)
{
    text = text.Replace("```json", "").Replace("```", "").Trim();
    int s = text.IndexOf('{'); int e = text.LastIndexOf('}');
    if (s < 0) throw new Exception("no JSON in AI reply");
    return e > s ? text[s..(e + 1)] : text[s..];
}

// scrub any long identifier the model might leak despite instructions
static string Scrub(string s) => Regex.Replace(s ?? "", @"\b[A-Z0-9]{2}[A-Z0-9\- ]{6,}\b", "•••");

// ================= endpoints =================
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true, app = "renewalbrain",
    provider = mockAi ? "mock" : (geminiKey != null ? "gemini" : anthropicKey != null ? "anthropic" : "none"),
    store = store is SupabaseStore ? "supabase" : "file",
    playbooks = playbooks.Count
}));

app.MapPost("/api/extract", async (HttpContext ctx, ExtractRequest reqBody) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    if (Limited(ip)) return Results.StatusCode(429);
    bool hasImage = !string.IsNullOrWhiteSpace(reqBody.ImageBase64);
    bool hasText = !string.IsNullOrWhiteSpace(reqBody.Text) && reqBody.Text!.Trim().Length >= 12;
    if (!hasImage && !hasText)
        return Results.BadRequest(new { error = "send imageBase64+mimeType or text" });
    if (hasImage && (reqBody.ImageBase64!.Length > 9_000_000))
        return Results.BadRequest(new { error = "image too large — keep under ~6MB" });
    try
    {
        var parts = new List<object>();
        if (hasImage)
            parts.Add(new { inline_data = new { mime_type = reqBody.MimeType ?? "image/jpeg", data = reqBody.ImageBase64 } });
        if (hasText) parts.Add(new { text = "DOCUMENT TEXT:\n" + reqBody.Text });
        parts.Add(new { text = ExtractPrompt() });

        var raw = await AskAi(parts.ToArray());
        var node = JsonNode.Parse(ExtractJson(raw))!;
        foreach (var f in new[] { "title", "issuer", "notes", "person" })
            if (node[f] is JsonValue v) node[f] = Scrub(v.GetValue<string>());
        var type = (string?)node["type"] ?? "other";
        int lead = LeadFor(type, (int?)node["leadDays"]);
        node["leadDays"] = lead;
        var exp = (string?)node["expiresOn"];
        node["actByOn"] = DateTime.TryParse(exp, out var ed) ? ed.AddDays(-lead).ToString("yyyy-MM-dd") : "";
        // privacy receipt: the image was processed in-memory only and is now gone
        node["privacy"] = "image processed and discarded — only dates and labels above are kept";
        return Results.Content(node.ToJsonString(), "application/json");
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = "extraction failed", detail = ex.Message }, statusCode: 502);
    }
});

// ---- items CRUD ----
app.MapGet("/api/items", async () => Results.Json(await store.GetItems()));

app.MapPost("/api/items", async (JsonNode body) =>
{
    var title = (string?)body["title"]; var exp = (string?)body["expiresOn"];
    if (string.IsNullOrWhiteSpace(title) || !DateTime.TryParse(exp, out var ed))
        return Results.BadRequest(new { error = "title and a valid expiresOn (YYYY-MM-DD) are required" });
    var type = (string?)body["type"] ?? "other";
    int lead = LeadFor(type, (int?)body["leadDays"]);
    var id = "it_" + Guid.NewGuid().ToString("N")[..10];
    var item = new JsonObject
    {
        ["id"] = id,
        ["title"] = Scrub(title!),
        ["type"] = type,
        ["category"] = (string?)body["category"] ?? "Other",
        ["issuer"] = Scrub((string?)body["issuer"] ?? ""),
        ["country"] = (string?)body["country"] ?? "*",
        ["person"] = Scrub((string?)body["person"] ?? ""),
        ["expiresOn"] = ed.ToString("yyyy-MM-dd"),
        ["leadDays"] = lead,
        ["actByOn"] = ((string?)body["actByOn"]) is string a && DateTime.TryParse(a, out var ad)
            ? ad.ToString("yyyy-MM-dd") : ed.AddDays(-lead).ToString("yyyy-MM-dd"),
        ["notes"] = Scrub((string?)body["notes"] ?? ""),
        ["lastRenewedOn"] = (string?)body["lastRenewedOn"] ?? "",
        ["createdAtUtc"] = DateTime.UtcNow.ToString("o")
    };
    try { await store.UpsertItem(id, item); }
    catch (Exception ex) { return Results.Json(new { error = "storage error — check Supabase setup (rb_items table + env vars)", detail = ex.Message }, statusCode: 502); }
    return Results.Content(item.ToJsonString(), "application/json");
});

app.MapPatch("/api/items/{id}", async (string id, JsonNode body) =>
{
    var existing = await store.GetItem(id);
    if (existing is null) return Results.NotFound();
    bool expiryChanged = false;
    foreach (var (k, v) in body.AsObject())
    {
        if (k is "id" or "createdAtUtc") continue;
        if (k == "expiresOn") { if (!DateTime.TryParse((string?)v, out _)) return Results.BadRequest(new { error = "invalid expiresOn" }); expiryChanged = true; }
        existing[k] = v?.DeepClone();
    }
    if (expiryChanged && body["actByOn"] is null)
    {
        var lead = LeadFor((string?)existing["type"], (int?)existing["leadDays"]);
        existing["leadDays"] = lead;
        existing["actByOn"] = DateTime.Parse((string)existing["expiresOn"]!).AddDays(-lead).ToString("yyyy-MM-dd");
    }
    foreach (var f in new[] { "title", "issuer", "notes", "person" })
        if (existing[f] is JsonValue v2) existing[f] = Scrub(v2.GetValue<string>());
    try { await store.UpsertItem(id, existing); }
    catch (Exception ex) { return Results.Json(new { error = "storage error — check Supabase setup", detail = ex.Message }, statusCode: 502); }
    return Results.Content(existing.ToJsonString(), "application/json");
});

app.MapDelete("/api/items/{id}", async (string id) =>
    await store.DeleteItem(id) ? Results.Ok(new { removed = 1 }) : Results.NotFound());

app.MapGet("/api/items/export", async () =>
    Results.File(Encoding.UTF8.GetBytes(new JsonArray((await store.GetItems()).Select(n => n.DeepClone()).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true })),
        "application/json", "renewalbrain-items.json"));

app.MapPost("/api/items/import", async (HttpRequest req) =>
{
    var arr = (await JsonNode.ParseAsync(req.Body))?.AsArray();
    if (arr is null || arr.Count == 0) return Results.BadRequest(new { error = "no items in file" });
    int n = 0;
    foreach (var it in arr)
    {
        var id = (string?)it?["id"] ?? ("it_" + Guid.NewGuid().ToString("N")[..10]);
        it!["id"] = id;
        await store.UpsertItem(id, it.DeepClone());
        n++;
    }
    return Results.Ok(new { imported = n });
});

// ---- playbooks ----
app.MapGet("/api/playbook", (string type, string? country) =>
{
    var pb = playbooks.FirstOrDefault(p => p.Type == type && p.Country.Equals(country ?? "", StringComparison.OrdinalIgnoreCase))
          ?? playbooks.FirstOrDefault(p => p.Type == type && p.Country == "*");
    return pb is null ? Results.NotFound(new { error = "no playbook for this type yet" }) : Results.Json(pb);
});
app.MapGet("/api/playbooks", () => Results.Json(playbooks));

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// ================= models =================
record ExtractRequest(string? ImageBase64, string? MimeType, string? Text);
record Playbook(string Type, string Country, string Title, int LeadDays, string[] Steps, string[] Documents, string TypicalProcessing, string Tip);

// ================= storage =================
interface IStore
{
    Task<List<JsonNode>> GetItems();
    Task<JsonNode?> GetItem(string id);
    Task UpsertItem(string id, JsonNode doc);
    Task<bool> DeleteItem(string id);
}

class FileStore : IStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public FileStore(string dir, JsonSerializerOptions _) { _path = Path.Combine(dir, "items.json"); }
    private List<JsonNode> Read() => File.Exists(_path)
        ? (JsonNode.Parse(File.ReadAllText(_path))?.AsArray().Select(n => n!.DeepClone()).ToList() ?? new()) : new();
    private void Write(List<JsonNode> items) =>
        File.WriteAllText(_path, new JsonArray(items.Select(n => n.DeepClone()).ToArray()).ToJsonString());
    public async Task<List<JsonNode>> GetItems() { await _lock.WaitAsync(); try { return Read(); } finally { _lock.Release(); } }
    public async Task<JsonNode?> GetItem(string id) { await _lock.WaitAsync(); try { return Read().FirstOrDefault(n => (string?)n["id"] == id); } finally { _lock.Release(); } }
    public async Task UpsertItem(string id, JsonNode doc)
    { await _lock.WaitAsync(); try { var items = Read(); items.RemoveAll(n => (string?)n["id"] == id); items.Add(doc); Write(items); } finally { _lock.Release(); } }
    public async Task<bool> DeleteItem(string id)
    { await _lock.WaitAsync(); try { var items = Read(); var n = items.RemoveAll(x => (string?)x["id"] == id); if (n > 0) Write(items); return n > 0; } finally { _lock.Release(); } }
}

class SupabaseStore : IStore
{
    private readonly HttpClient _http; private readonly string _url, _key; private readonly JsonSerializerOptions _json;
    public SupabaseStore(HttpClient http, string url, string key, JsonSerializerOptions json)
    { _http = http; _url = url; _key = key; _json = json; }
    private HttpRequestMessage Req(HttpMethod m, string path, string? body = null, string? prefer = null)
    {
        var r = new HttpRequestMessage(m, $"{_url}/rest/v1/{path}");
        r.Headers.Add("apikey", _key);
        r.Headers.Add("Authorization", $"Bearer {_key}");
        if (prefer != null) r.Headers.Add("Prefer", prefer);
        if (body != null) r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }
    private async Task<JsonNode?> Send(HttpRequestMessage r)
    {
        var res = await _http.SendAsync(r);
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"store error {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }
    public async Task<List<JsonNode>> GetItems()
    {
        var rows = await Send(Req(HttpMethod.Get, "rb_items?select=doc&order=id.asc"));
        return rows?.AsArray().Select(r => r!["doc"]!.DeepClone()).ToList() ?? new();
    }
    public async Task<JsonNode?> GetItem(string id)
    {
        var rows = await Send(Req(HttpMethod.Get, $"rb_items?select=doc&id=eq.{Uri.EscapeDataString(id)}"));
        var arr = rows?.AsArray();
        return arr is { Count: > 0 } ? arr[0]!["doc"]!.DeepClone() : null;
    }
    public Task UpsertItem(string id, JsonNode doc) =>
        Send(Req(HttpMethod.Post, "rb_items?on_conflict=id",
            new JsonArray(new JsonObject { ["id"] = id, ["doc"] = doc.DeepClone() }).ToJsonString(), "resolution=merge-duplicates"));
    public async Task<bool> DeleteItem(string id)
    {
        var res = await Send(Req(HttpMethod.Delete, $"rb_items?id=eq.{Uri.EscapeDataString(id)}", prefer: "return=representation"));
        return res?.AsArray().Count > 0;
    }
}
