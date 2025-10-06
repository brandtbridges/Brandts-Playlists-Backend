using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PlexProxy.Options;   // PlexOptions
using PlexProxy.Models;    // PlaylistSummaryDto, PlaylistsResponseDto

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("https://www.brandtbridges.com")
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});



// Bind Plex options
var plexSection = builder.Configuration.GetSection("Plex");
var plexBaseUrl = plexSection.GetValue<string>("BaseUrl") ?? "http://127.0.0.1:32400";
var plexToken = plexSection.GetValue<string>("Token") ?? throw new InvalidOperationException("Plex:Token is required");

// PMS HttpClient (JSON + token)
builder.Services.AddHttpClient("plex", client =>
{
    client.BaseAddress = new Uri(plexBaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.Add("X-Plex-Token", plexToken);
    client.DefaultRequestHeaders.Add("X-Plex-Product", "PlexPlaylistProxy");
    client.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "PlexPlaylistProxy-IIS");
});

builder.Services.AddMemoryCache();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseHttpsRedirection();
app.UseCors();


var cache = app.Services.GetRequiredService<IMemoryCache>();

string NewTicket(string partKey, TimeSpan? ttl = null)
{
    var ticket = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
    cache.Set($"t:{ticket}", partKey, new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = ttl ?? TimeSpan.FromMinutes(5)
    });
    return ticket;
}

bool TryGetPartKey(string ticket, out string partKey) =>
    cache.TryGetValue($"t:{ticket}", out partKey!);

// ---- GET /api/playlist/{id} : returns tracks with proxied stream URLs ----
// Uses PMS: /playlists/{id}/items  (supply Accept: application/json).  [3](https://stackoverflow.com/questions/73695008/how-to-install-dotnet-core-6-webapp-on-iis-server-window-11)
app.MapGet("/api/playlist/{id}", async (string id, IHttpClientFactory f) =>
{
    var client = f.CreateClient("plex");
    using var resp = await client.GetAsync($"/playlists/{Uri.EscapeDataString(id)}/items");
    if (!resp.IsSuccessStatusCode)
        return Results.StatusCode((int)resp.StatusCode);

    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
    var mc = doc.RootElement.GetProperty("MediaContainer");
    var tracks = new List<object>();

    if (mc.TryGetProperty("Metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
    {
        foreach (var m in metaArr.EnumerateArray())
        {
            if (m.TryGetProperty("type", out var tType) && tType.GetString() != "track")
                continue;

            string? partKey = null;
            if (m.TryGetProperty("Media", out var mediaArr) && mediaArr.ValueKind == JsonValueKind.Array && mediaArr.GetArrayLength() > 0)
            {
                var media0 = mediaArr[0];
                if (media0.TryGetProperty("Part", out var partArr) && partArr.ValueKind == JsonValueKind.Array && partArr.GetArrayLength() > 0)
                {
                    partKey = partArr[0].GetProperty("key").GetString(); // e.g., /library/parts/.../file.mp3
                }
            }
            if (string.IsNullOrEmpty(partKey)) continue;

            var ticket = NewTicket(partKey!);

            cache.Set($"rk:{m.GetProperty("ratingKey").GetString()}", partKey!,
                new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6) });


            string title = m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string artist = m.TryGetProperty("grandparentTitle", out var a) ? a.GetString() ?? "" : "";
            string album = m.TryGetProperty("parentTitle", out var p) ? p.GetString() ?? "" : "";
            long? duration = m.TryGetProperty("duration", out var d) ? d.GetInt64() : null;

            string? thumbRel = m.TryGetProperty("thumb", out var th) ? th.GetString() : null;
            string? artUrl = thumbRel != null
                ? $"/api/art?path={Convert.ToBase64String(Encoding.UTF8.GetBytes(thumbRel))}"
                : null;

            tracks.Add(new
            {
                id = m.GetProperty("ratingKey").GetString(),
                title,
                artist,
                album,
                durationMs = duration,
                artUrl,
                streamUrl = $"/api/stream/{ticket}"
            });
        }
    }

    return Results.Json(new { count = tracks.Count, tracks });
});

// ---- GET /api/stream/for/{ratingKey} : mint a fresh ticket for this track ----
app.MapGet("/api/stream/for/{ratingKey}", async (
    string ratingKey,
    IHttpClientFactory f,
    CancellationToken ct) =>
{
    // 1) Try cache first
    if (!cache.TryGetValue($"rk:{ratingKey}", out string? partKey) || string.IsNullOrWhiteSpace(partKey))
    {
        // 2) Fallback: fetch the track to recover Part.key
        var client = f.CreateClient("plex");
        using var resp = await client.GetAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}", ct);
        if (!resp.IsSuccessStatusCode) return Results.StatusCode((int)resp.StatusCode);

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc))
            return Results.NotFound(new { error = "No MediaContainer" });

        string? recovered = null;
        if (mc.TryGetProperty("Metadata", out var meta) && meta.ValueKind == JsonValueKind.Array && meta.GetArrayLength() > 0)
        {
            var m = meta[0];
            if (m.TryGetProperty("Media", out var mediaArr) && mediaArr.ValueKind == JsonValueKind.Array && mediaArr.GetArrayLength() > 0)
            {
                var media0 = mediaArr[0];
                if (media0.TryGetProperty("Part", out var partArr) && partArr.ValueKind == JsonValueKind.Array && partArr.GetArrayLength() > 0)
                {
                    recovered = partArr[0].GetProperty("key").GetString(); // e.g. /library/parts/.../file.mp3
                }
            }
        }
        if (string.IsNullOrWhiteSpace(recovered)) return Results.NotFound(new { error = "No Part.key" });

        partKey = recovered!;
        cache.Set($"rk:{ratingKey}", partKey, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6) });
    }

    var fresh = NewTicket(partKey!, TimeSpan.FromMinutes(5));
    // Keep the payload minimal; the client will construct the full URL
    return Results.Json(new { ticket = fresh });
});

// ---- GET /api/stream/{ticket} : byte-for-byte proxy with Range support ----
// PMS tokens are required on server endpoints; we send header auth and pass through Content-Range, etc. [2](https://learn.microsoft.com/en-us/aspnet/core/migration/50-to-60-samples?view=aspnetcore-9.0)
// --- GET /api/stream/{ticket} : byte-for-byte proxy with Range support ---
app.MapGet("/api/stream/{ticket}", async (HttpContext ctx, string ticket, IHttpClientFactory f) =>
{
    static bool PassHeader(string k)
    {
        var n = k.ToLowerInvariant();
        return n is "content-type" or "content-length" or "accept-ranges" or "content-range"
            or "last-modified" or "etag" or "cache-control";
    }

    // 1) Try memory mapping first
    if (!TryGetPartKey(ticket, out var partKey))
    {
        // 1a) Fallback: if client provided ratingKey, recover partKey from PMS
        if (ctx.Request.Query.TryGetValue("rk", out var rk) && !string.IsNullOrWhiteSpace(rk))
        {
            var client = f.CreateClient("plex");
            using var meta = await client.GetAsync($"/library/metadata/{Uri.EscapeDataString(rk!)}", ctx.RequestAborted);
            if (meta.IsSuccessStatusCode)
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(await meta.Content.ReadAsStreamAsync(ctx.RequestAborted), cancellationToken: ctx.RequestAborted);
                if (doc.RootElement.TryGetProperty("MediaContainer", out var mc)
                    && mc.TryGetProperty("Metadata", out var arr)
                    && arr.ValueKind == System.Text.Json.JsonValueKind.Array
                    && arr.GetArrayLength() > 0)
                {
                    var m0 = arr[0];
                    if (m0.TryGetProperty("Media", out var mediaArr)
                        && mediaArr.ValueKind == System.Text.Json.JsonValueKind.Array
                        && mediaArr.GetArrayLength() > 0)
                    {
                        var pArr = mediaArr[0].GetProperty("Part");
                        if (pArr.ValueKind == System.Text.Json.JsonValueKind.Array && pArr.GetArrayLength() > 0)
                        {
                            partKey = pArr[0].GetProperty("key").GetString()!;
                            // Re-mint the lost ticket mapping so subsequent byte-range requests succeed
                            cache.Set($"t:{ticket}", partKey, new MemoryCacheEntryOptions
                            {
                                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
                            });
                        }
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(partKey))
            return Results.StatusCode(StatusCodes.Status410Gone); // still missing → Gone
    }

    // 2) Proxy the PMS part (with Range passthrough)
    var plex = f.CreateClient("plex");
    var req = new HttpRequestMessage(HttpMethod.Get, partKey);
    if (ctx.Request.Headers.TryGetValue("Range", out var range))
    {
        req.Headers.TryAddWithoutValidation("Range", (string)range);
    }

    using var upstream = await plex.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)upstream.StatusCode;

    foreach (var h in upstream.Headers)
        if (PassHeader(h.Key))
            ctx.Response.Headers[h.Key] = new Microsoft.Extensions.Primitives.StringValues(h.Value.ToArray());
    foreach (var h in upstream.Content.Headers)
        if (PassHeader(h.Key))
            ctx.Response.Headers[h.Key] = new Microsoft.Extensions.Primitives.StringValues(h.Value.Select(v => v.ToString()).ToArray());

    await upstream.Content.CopyToAsync(ctx.Response.Body);
    return Results.Empty;
});

// ---- Optional: album art proxy (avoids exposing token) ----
app.MapGet("/api/art", async (HttpContext ctx, IHttpClientFactory f) =>
{
    if (!ctx.Request.Query.TryGetValue("path", out var b64)) return Results.BadRequest("Missing path");
    string path;
    try { path = Encoding.UTF8.GetString(Convert.FromBase64String(b64!)); }
    catch { return Results.BadRequest("Invalid path"); }
    if (!path.StartsWith("/")) return Results.BadRequest("Path must be relative PMS path");

    var client = f.CreateClient("plex");
    using var upstream = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    if (!upstream.IsSuccessStatusCode)
        return Results.StatusCode((int)upstream.StatusCode);

    ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
    await upstream.Content.CopyToAsync(ctx.Response.Body);
    return Results.Empty;
});

// ---- GET /api/playlists : list Plex playlists (audio/music by default) ----
// Uses PMS: /playlists?playlistType=audio (JSON). Token sent via header on the "plex" client.
app.MapGet("/api/playlists", async (
    string? type,
    int? take,
    IHttpClientFactory f,
    IMemoryCache cache,
    CancellationToken ct) =>
{
    // Normalize inputs
    var t = (type ?? "music").ToLowerInvariant();              // music|video|photo|all
    var max = Math.Clamp(take ?? 200, 1, 1000);
    var cacheKey = $"playlists::{t}::{max}";

    if (cache.TryGetValue(cacheKey, out PlaylistsResponseDto? cached) && cached is not null)
        return Results.Json(cached);

    // Build upstream path (JSON expected by your client config)
    string playlistTypeParam = t switch
    {
        "music" => "playlistType=audio",
        "video" => "playlistType=video",
        "photo" => "playlistType=photo",
        "all" => "",
        _ => "playlistType=audio"
    };
    var qs = new List<string>();
    if (!string.IsNullOrEmpty(playlistTypeParam)) qs.Add(playlistTypeParam);
    var upstream = "/playlists" + (qs.Count > 0 ? $"?{string.Join("&", qs)}" : "");

    var client = f.CreateClient("plex");
    using var resp = await client.GetAsync(upstream, ct);
    if (!resp.IsSuccessStatusCode)
        return Results.StatusCode((int)resp.StatusCode);

    using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc))
        return Results.Json(new PlaylistsResponseDto(Array.Empty<PlaylistSummaryDto>(), 0));

    var list = new List<PlaylistSummaryDto>();
    if (mc.TryGetProperty("Metadata", out var metaArr) && metaArr.ValueKind == JsonValueKind.Array)
    {
        foreach (var m in metaArr.EnumerateArray())
        {
            var id = m.TryGetProperty("ratingKey", out var rk) ? rk.GetString() ?? "" : "";
            var name = m.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;

            var pType = m.TryGetProperty("playlistType", out var pt) ? pt.GetString() : null;
            int? leaf = m.TryGetProperty("leafCount", out var lc) && lc.TryGetInt32(out var lv) ? lv : null;
            int? durSec = null;
            if (m.TryGetProperty("duration", out var du) && du.ValueKind is JsonValueKind.Number)
            {
                if (du.TryGetInt64(out var ms)) durSec = (int)Math.Clamp(ms / 1000L, 0, int.MaxValue);
                else if (du.TryGetInt32(out var ms32)) durSec = Math.Max(ms32 / 1000, 0);
            }

            list.Add(new PlaylistSummaryDto(
                Id: id,
                Title: name,
                PlaylistType: pType ?? "",
                LeafCount: leaf,
                DurationSec: durSec
            ));

            if (list.Count >= max) break;
        }
    }

    var dto = new PlaylistsResponseDto(list, list.Count);
    cache.Set(cacheKey, dto, TimeSpan.FromSeconds(30)); // short TTL, stays fresh
    return Results.Json(dto);
});


app.Run();

