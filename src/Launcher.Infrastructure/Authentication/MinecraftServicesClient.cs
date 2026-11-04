using System.Net;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;

namespace Launcher.Infrastructure.Authentication;

/// <summary>Minecraft Services: вход через Xbox и получение профиля игрока.</summary>
public sealed class MinecraftServicesClient : IMinecraftServicesClient
{
    public static readonly Uri DefaultBaseUri = new("https://api.minecraftservices.com/");

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly TimeProvider _clock;

    public MinecraftServicesClient(HttpClient http, Uri? baseUri = null, TimeProvider? clock = null)
    {
        _http = http;
        _baseUri = baseUri ?? DefaultBaseUri;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<MinecraftSession> LoginWithXboxAsync(string userHash, string xstsToken, CancellationToken ct)
    {
        var body = new { identityToken = $"XBL3.0 x={userHash};{xstsToken}" };
        var (status, doc) = await AuthHttp.PostJsonAsync(_http, new Uri(_baseUri, "authentication/login_with_xbox"), body, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            if (status == HttpStatusCode.Forbidden)
                throw new AuthException(
                    "Minecraft Services отклонили приложение (HTTP 403). Скорее всего, ваше приложение Azure ещё не одобрено для доступа " +
                    "к Minecraft API: новые приложения должны пройти проверку (см. README, раздел «Авторизация»).");

            var token = AuthHttp.String(doc?.RootElement ?? default, "access_token");
            if ((int)status >= 400 || token is null)
                throw new AuthException($"Minecraft Services отклонили вход (HTTP {(int)status}).");

            var expiresIn = AuthHttp.Int(doc!.RootElement, "expires_in") ?? 86400;
            return new MinecraftSession(token, _clock.GetUtcNow().AddSeconds(expiresIn));
        }
    }

    public async Task<MinecraftSkinInfo?> GetSkinAsync(string minecraftAccessToken, CancellationToken ct)
    {
        var (status, doc) = await AuthHttp.GetBearerAsync(_http, new Uri(_baseUri, "minecraft/profile"), minecraftAccessToken, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            if (status == HttpStatusCode.NotFound) return null;
            if ((int)status >= 400 || doc is null)
                throw new AuthException($"Не удалось получить скин аккаунта (HTTP {(int)status}).");

            if (!doc.RootElement.TryGetProperty("skins", out var skins) || skins.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            foreach (var skin in skins.EnumerateArray())
            {
                if (AuthHttp.String(skin, "state") is { } state && !state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Uri.TryCreate(AuthHttp.String(skin, "url"), UriKind.Absolute, out var url)) continue;
                var slim = string.Equals(AuthHttp.String(skin, "variant"), "SLIM", StringComparison.OrdinalIgnoreCase);
                return new MinecraftSkinInfo(url, slim);
            }
            return null;
        }
    }

    public async Task UploadSkinAsync(string minecraftAccessToken, byte[] png, bool slim, CancellationToken ct)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "skin.png");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "minecraft/profile/skins")) { Content = form };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", minecraftAccessToken);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode) return;
            throw new AuthException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Mojang не принял вход (токен устарел). Выйдите и войдите через Microsoft заново.",
                HttpStatusCode.TooManyRequests => "Mojang просит подождать: слишком много смен скина. Повторите через несколько минут.",
                HttpStatusCode.BadRequest => "Mojang отклонил этот файл как скин. Нужен PNG 64×64.",
                _ => $"Не удалось сменить скин (HTTP {(int)response.StatusCode})."
            });
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new AuthException("Не удалось связаться с Minecraft Services: проверьте подключение к интернету.", ex);
        }
    }

    public async Task<byte[]> DownloadSkinAsync(Uri url, CancellationToken ct)
    {
        // Mojang отдаёт адрес скина как http://textures.minecraft.net/…: принимаем и http, и https, но скачиваем всегда по https
        if (url.Scheme is not ("http" or "https") || !url.Host.Equals("textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
            throw new AuthException("Адрес скина указывает не на сервер текстур Minecraft — загрузка отменена.");
        if (url.Scheme == Uri.UriSchemeHttp) url = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > Launcher.Core.Skins.SkinImage.MaxBytes)
                throw new AuthException("Файл скина слишком большой.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (bytes.Length > Launcher.Core.Skins.SkinImage.MaxBytes) throw new AuthException("Файл скина слишком большой.");
            return bytes;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            throw new AuthException("Не удалось скачать скин с сервера Minecraft.", ex);
        }
    }

    public async Task<MinecraftProfile?> GetProfileAsync(string minecraftAccessToken, CancellationToken ct)
    {
        var (status, doc) = await AuthHttp.GetBearerAsync(_http, new Uri(_baseUri, "minecraft/profile"), minecraftAccessToken, ct)
            .ConfigureAwait(false);
        using (doc)
        {
            if (status == HttpStatusCode.NotFound) return null; // у аккаунта нет Java Edition
            var root = doc?.RootElement ?? default;
            var id = AuthHttp.String(root, "id");
            var name = AuthHttp.String(root, "name");
            if ((int)status >= 400 || id is null || name is null)
                throw new AuthException($"Не удалось получить профиль Minecraft (HTTP {(int)status}).");
            return new MinecraftProfile(id, name);
        }
    }
}
