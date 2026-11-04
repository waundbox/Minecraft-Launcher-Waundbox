using System.Net;
using System.Text.Json;
using Launcher.Core.Errors;
using Launcher.Core.Mods;

namespace Launcher.Infrastructure.Mods;

/// <summary>
/// Клиент каталога Modrinth, API v2 (https://docs.modrinth.com/api/). По правилам сервиса каждый запрос должен нести
/// уникальный User-Agent (задаётся у <see cref="HttpClient"/>), лимит — 300 запросов в минуту с одного адреса.
/// </summary>
public sealed class HttpModrinthClient : IModrinthClient
{
    public static readonly Uri DefaultBaseUri = new("https://api.modrinth.com/v2/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;

    public HttpModrinthClient(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
    }

    public async Task<ModSearchResult> SearchAsync(ModSearchQuery query, CancellationToken ct)
    {
        var url = $"search?query={Uri.EscapeDataString(query.Text ?? "")}" +
                  $"&facets={Uri.EscapeDataString(BuildFacets(query))}" +
                  $"&index={IndexName(query.Sort)}&offset={Math.Max(0, query.Offset)}&limit={Math.Clamp(query.Limit, 1, 100)}";
        return ModrinthParser.ParseSearch((await GetAsync(url, ct).ConfigureAwait(false))!);
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(string projectId, string gameVersion, string? loader, CancellationToken ct)
    {
        var url = $"project/{Uri.EscapeDataString(projectId)}/version" +
                  $"?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}" +
                  (loader is null ? "" : $"&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}") +
                  "&include_changelog=false";
        var json = await GetAsync(url, ct, notFoundIsNull: true).ConfigureAwait(false);
        return json is null ? [] : ModrinthParser.ParseVersions(json);
    }

    public async Task<ModVersion?> GetVersionAsync(string versionId, CancellationToken ct)
    {
        var json = await GetAsync($"version/{Uri.EscapeDataString(versionId)}", ct, notFoundIsNull: true).ConfigureAwait(false);
        return json is null ? null : ModrinthParser.ParseVersion(json);
    }

    public async Task<IReadOnlyList<ModProject>> GetProjectsAsync(IReadOnlyCollection<string> idsOrSlugs, CancellationToken ct)
    {
        if (idsOrSlugs.Count == 0) return [];
        var url = $"projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(idsOrSlugs))}";
        var json = await GetAsync(url, ct, notFoundIsNull: true).ConfigureAwait(false);
        return json is null ? [] : ModrinthParser.ParseProjects(json);
    }

    /// <summary>Фильтры поиска: массив массивов, внутри — «или», между — «и» (формат Modrinth).</summary>
    internal static string BuildFacets(ModSearchQuery query)
    {
        var facets = new List<string[]>
        {
            new[] { $"project_type:{query.Type.ProjectType()}" },
            new[] { $"versions:{query.GameVersion}" }
        };
        // Загрузчик — это категория проекта. Только для модов: ресурспаки и шейдеры от загрузчика не зависят.
        if (query.Type == ModContentType.Mod && !string.IsNullOrEmpty(query.Loader))
            facets.Add([$"categories:{query.Loader}"]);
        return JsonSerializer.Serialize(facets);
    }

    private static string IndexName(ModSort sort) => sort switch
    {
        ModSort.Downloads => "downloads",
        ModSort.Follows => "follows",
        ModSort.Newest => "newest",
        ModSort.Updated => "updated",
        _ => "relevance"
    };

    private async Task<string?> GetAsync(string relative, CancellationToken ct, bool notFoundIsNull = false)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_baseUri, relative), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && notFoundIsNull) return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ModException("Modrinth просит подождать: слишком много запросов. Повторите через минуту.");
            if (!response.IsSuccessStatusCode)
                throw new ModException($"Modrinth ответил ошибкой {(int)response.StatusCode}. Повторите позже.");
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new ModException("Не удалось связаться с Modrinth (api.modrinth.com): проверьте подключение к интернету.", ex);
        }
    }
}
