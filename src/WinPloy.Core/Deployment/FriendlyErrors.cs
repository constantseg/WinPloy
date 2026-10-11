using System.Text.RegularExpressions;

namespace WinPloy.Core.Deployment;

/// <summary>Messages d'erreur lisibles pour les échecs de connexion les plus fréquents.</summary>
public static class FriendlyErrors
{
    public const string NoResultMessage = "Aucun résultat renvoyé par le poste.";

    private static readonly Regex AccessDenied = new("Access is denied|Accès refusé|logon failure|access.*denied", RegexOptions.IgnoreCase);

    public static string Describe(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Contains("0x8009030e", StringComparison.OrdinalIgnoreCase))
        {
            return $@"Authentification WinRM impossible (0x8009030e). Vérifier le format du compte (DOMAINE\utilisateur ou POSTE\utilisateur). Détail : {text}";
        }
        if (AccessDenied.IsMatch(text))
        {
            return $"Accès refusé : le compte n'a pas les droits administrateur sur le poste. Détail : {text}";
        }
        return text.Length > 0 ? text : NoResultMessage;
    }

    public static string Flatten(Exception exception) => exception is AggregateException aggregate
        ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct())
        : exception.Message;
}
