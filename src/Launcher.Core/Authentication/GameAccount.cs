using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Launcher.Core.Errors;

namespace Launcher.Core.Authentication;

/// <summary>Данные игрока, которые передаются игре при запуске.</summary>
/// <param name="Uuid">UUID игрока без дефисов.</param>
/// <param name="UserType">"msa" для официальной авторизации Microsoft, "legacy" для development-режима.</param>
/// <param name="IsDevelopment">true — это НЕ официальная авторизация (см. <see cref="DevelopmentAccount"/>).</param>
/// <param name="AuthlibServer">Для аккаунтов сторонних серверов авторизации (Ely.by): адрес, который передаётся в authlib-injector; иначе null.</param>
public sealed record GameAccount(
    string Username, string Uuid, string AccessToken, string UserType, bool IsDevelopment, string? AuthlibServer = null);

/// <summary>
/// DEVELOPMENT-аккаунт для проверки механики запуска без входа в Microsoft.
/// Это не официальная авторизация и не подтверждает владение игрой: у такого игрока нет настоящего токена,
/// он не сможет зайти на серверы с проверкой лицензии. Официальная авторизация (Microsoft/Xbox/Minecraft) будет
/// добавлена отдельным этапом в модуле Authentication.
/// </summary>
public static class DevelopmentAccount
{
    public const string DefaultUsername = "Player";
    public const string UserType = "legacy";
    public const string PlaceholderToken = "waundbox-development-token";

    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.CultureInvariant);

    /// <exception cref="LauncherException">Недопустимый ник.</exception>
    public static GameAccount Create(string username)
    {
        if (!UsernameRegex.IsMatch(username))
            throw new LauncherException("Ник должен содержать от 3 до 16 символов: латинские буквы, цифры и знак подчёркивания.");

        return new GameAccount(username, OfflineUuid(username), PlaceholderToken, UserType, IsDevelopment: true);
    }

    /// <summary>Тот же алгоритм, что у Java UUID.nameUUIDFromBytes("OfflinePlayer:" + ник): стабильный UUID для одного и того же ника.</summary>
    public static string OfflineUuid(string username)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30); // версия 3
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80); // вариант RFC 4122
        return Convert.ToHexStringLower(hash);
    }
}
