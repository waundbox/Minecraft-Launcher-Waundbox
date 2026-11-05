using System.Net;
using Launcher.Core.Updates;

namespace Launcher.Infrastructure.Updates;

/// <summary>Читает список выпусков лаунчера через GitHub API (для публичного репозитория ключ не нужен).</summary>
public sealed class GitHubUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;
    private readonly Uri _releasesUri;
    private readonly string _owner;
    private readonly string _repository;

    /// <param name="apiBase">Адрес API (для тестов); по умолчанию https://api.github.com/.</param>
    public GitHubUpdateClient(HttpClient http, string owner = UpdateSource.Owner, string repository = UpdateSource.Repository, Uri? apiBase = null)
    {
        _http = http;
        _owner = owner;
        _repository = repository;
        _releasesUri = new Uri(apiBase ?? new Uri("https://api.github.com/"), $"repos/{owner}/{repository}/releases?per_page=30");
    }

    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    throw new UpdateException("Страница выпусков Waundbox не найдена на GitHub.");
                case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                    throw new UpdateException("GitHub временно ограничил число запросов. Попробуйте проверить обновления чуть позже.");
            }
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub ответил ошибкой {(int)response.StatusCode} при проверке обновлений.");

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ReleaseParser.ParseReleases(json);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("Не удалось связаться с GitHub: проверьте подключение к интернету.", ex);
        }
    }

    public async Task<string?> ReadHashFileAsync(string url, CancellationToken ct)
    {
        if (!UpdateSource.IsTrustedAssetUrl(url, _owner, _repository)) return null;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength is > 4096) return null; // файл суммы маленький; большой — подозрительный
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length > 4096 ? null : ReleaseParser.ParseHashFile(text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("Не удалось скачать файл контрольной суммы обновления.", ex);
        }
    }
}
