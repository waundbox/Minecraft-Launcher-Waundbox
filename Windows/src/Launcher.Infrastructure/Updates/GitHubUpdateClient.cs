using System.Net;
using Launcher.Core.Updates;

namespace Launcher.Infrastructure.Updates;

/// <summary>Читает список выпусков лаунчера через GitHub API (для публичного репозитория ключ не нужен).</summary>
public sealed class GitHubUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;
    private readonly Uri _releasesUri;
    private readonly Uri _feedUri;
    private readonly string _owner;
    private readonly string _repository;

    /// <summary>GitHub не отдал список выпусков через API (лимит запросов или сбой сервера): можно попробовать ленту.</summary>
    private sealed class ApiUnavailableException : UpdateException
    {
        public ApiUnavailableException(string message) : base(message) { }
    }

    /// <param name="apiBase">Адрес API (для тестов); по умолчанию https://api.github.com/.</param>
    /// <param name="webBase">Адрес сайта GitHub (для тестов); по умолчанию https://github.com/.</param>
    public GitHubUpdateClient(HttpClient http, string owner = UpdateSource.Owner, string repository = UpdateSource.Repository,
        Uri? apiBase = null, Uri? webBase = null)
    {
        _http = http;
        _owner = owner;
        _repository = repository;
        _releasesUri = new Uri(apiBase ?? new Uri("https://api.github.com/"), $"repos/{owner}/{repository}/releases?per_page=30");
        _feedUri = new Uri(webBase ?? new Uri("https://github.com/"), $"{owner}/{repository}/releases.atom");
    }

    /// <summary>
    /// Список выпусков через GitHub API; если API отказал (у запросов без входа лимит 60 в час на один адрес, при VPN он общий),
    /// берётся лента выпусков releases.atom: у неё лимита нет. Если не вышло и там, показывается исходная ошибка.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
    {
        try
        {
            return await GetFromApiAsync(ct).ConfigureAwait(false);
        }
        catch (ApiUnavailableException first)
        {
            try
            {
                return await GetFromFeedAsync(ct).ConfigureAwait(false);
            }
            catch (UpdateException)
            {
                throw new UpdateException(first.Message, first);
            }
        }
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetFromFeedAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(_feedUri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub ответил ошибкой {(int)response.StatusCode} при проверке обновлений.");
            var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ReleaseParser.ParseAtom(xml, _owner, _repository);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpdateException("Не удалось связаться с GitHub: проверьте подключение к интернету.", ex);
        }
    }

    private static string LimitMessage(HttpResponseMessage response)
    {
        const string text = "GitHub временно ограничил число запросов (так бывает при VPN или в общей сети).";
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
            && long.TryParse(values.FirstOrDefault(), out var unix) && unix > 0)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();
            return $"{text} Попробуйте после {when:HH:mm}.";
        }
        return text + " Попробуйте проверить обновления чуть позже.";
    }

    private async Task<IReadOnlyList<ReleaseInfo>> GetFromApiAsync(CancellationToken ct)
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
                    throw new ApiUnavailableException(LimitMessage(response));
            }
            if ((int)response.StatusCode >= 500)
                throw new ApiUnavailableException($"GitHub ответил ошибкой {(int)response.StatusCode} при проверке обновлений.");
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
