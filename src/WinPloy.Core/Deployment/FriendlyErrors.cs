using System.Text.RegularExpressions;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Deployment;

/// <summary>Messages d'erreur lisibles pour les échecs de connexion les plus fréquents.</summary>
public static class FriendlyErrors
{
    public static string NoResultMessage => Strings.NoResult;

    private static readonly Regex AccessDenied = new("Access is denied|Accès refusé|logon failure|access.*denied", RegexOptions.IgnoreCase);

    public static string Describe(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Contains("0x8009030e", StringComparison.OrdinalIgnoreCase))
        {
            return string.Format(Strings.AuthFailed, text);
        }
        if (AccessDenied.IsMatch(text))
        {
            return string.Format(Strings.AccessDenied, text);
        }
        return text.Length > 0 ? text : NoResultMessage;
    }

    public static string Flatten(Exception exception) => exception is AggregateException aggregate
        ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct())
        : exception.Message;
}
