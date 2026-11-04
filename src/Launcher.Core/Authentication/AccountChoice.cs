namespace Launcher.Core.Authentication;

public enum AccountKind
{
    Microsoft,
    ElyBy,
    Development
}

/// <summary>Правило выбора аккаунта для запуска. Чистая функция: сеть и диск не нужны.</summary>
public static class AccountChoice
{
    public const string MicrosoftId = "microsoft";
    public const string ElyId = "elyby";
    public const string DevelopmentId = "dev";

    public static string ToId(AccountKind kind) => kind switch
    {
        AccountKind.Microsoft => MicrosoftId,
        AccountKind.ElyBy => ElyId,
        _ => DevelopmentId
    };

    public static AccountKind? FromId(string? id) => id switch
    {
        MicrosoftId => AccountKind.Microsoft,
        ElyId => AccountKind.ElyBy,
        DevelopmentId => AccountKind.Development,
        _ => null
    };

    /// <summary>
    /// Выбранный пользователем аккаунт, если он доступен; иначе первый доступный в порядке Microsoft, Ely.by, режим разработки.
    /// Режим разработки доступен всегда.
    /// </summary>
    public static AccountKind Resolve(string? preferred, bool microsoftSignedIn, bool elySignedIn)
    {
        bool Available(AccountKind k) => k switch
        {
            AccountKind.Microsoft => microsoftSignedIn,
            AccountKind.ElyBy => elySignedIn,
            _ => true
        };

        if (FromId(preferred) is { } wanted && Available(wanted)) return wanted;
        if (microsoftSignedIn) return AccountKind.Microsoft;
        if (elySignedIn) return AccountKind.ElyBy;
        return AccountKind.Development;
    }
}
