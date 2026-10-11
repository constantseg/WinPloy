using System.Text.RegularExpressions;

namespace WinPloy.Core.Deployment;

/// <summary>Messages d'erreur lisibles pour les échecs de connexion les plus fréquents.</summary>
public static class FriendlyErrors
{
    public const string NoResultMessage = "Aucun résultat renvoyé par le poste.";

    private static readonly Regex AccessDenied = new("Access is denied|Accès refusé|logon failure|access.*denied", RegexOptions.IgnoreCase);

    /// <summary>
    /// « TrustedHosts » est le nom du paramètre de configuration : le client WinRM le cite tel quel
    /// dans son message de refus, y compris sur un Windows en français.
    /// </summary>
    private static readonly Regex TrustedHostsRefusal = new("TrustedHosts", RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Vrai si le client WinRM a refusé la session en désignant <c>TrustedHosts</c> comme la cause.
    /// C'est le seul cas où ajouter le poste à la liste a une chance de changer quelque chose : un
    /// refus pour droits insuffisants ou mot de passe erroné n'a rien à voir avec elle.
    /// </summary>
    public static bool NeedsTrustedHost(string? raw)
        => raw is { Length: > 0 } && TrustedHostsRefusal.IsMatch(raw);

    public static string Flatten(Exception exception) => exception is AggregateException aggregate
        ? string.Join(" | ", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct())
        : exception.Message;
}
