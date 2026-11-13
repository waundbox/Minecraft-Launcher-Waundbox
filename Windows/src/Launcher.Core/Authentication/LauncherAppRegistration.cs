namespace Launcher.Core.Authentication;

/// <summary>
/// Приложение Microsoft (Azure), от имени которого лаунчер входит в аккаунты игроков.
/// Регистрируется ОДИН раз автором лаунчера; игрокам ничего регистрировать не нужно — они только вводят код на странице Microsoft.
/// Client id — не секрет (так же вшит в другие лаунчеры). Секретов (client secret) у публичного приложения нет.
/// Вписывайте сюда только идентификатор СВОЕГО приложения, одобренного для Minecraft API; чужие идентификаторы использовать нельзя.
/// </summary>
public static class LauncherAppRegistration
{
    /// <summary>Application (client) ID вида xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx. Приложение «Waundbox» зарегистрировано в Azure; одобрение для Minecraft API нужно получить отдельно.</summary>
    public const string ClientId = "79f40e61-890c-4a2e-b3b9-aa2e5adffa2a";
}
