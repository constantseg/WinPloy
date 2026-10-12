using System.Management.Automation;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Deployment;

/// <summary>Paramètres de connexion au service WinRM des postes cibles.</summary>
public sealed record WinRmOptions(int Port, bool UseSsl, bool SkipCertificateCheck)
{
    public static readonly WinRmOptions Default = new(AppSettings.DefaultWinRmPort, false, false);

    /// <summary>« 5986/HTTPS » ou « 5985/HTTP », pour les messages d'erreur et le journal.</summary>
    public string Label => $"{Port}/{(UseSsl ? "HTTPS" : "HTTP")}";

    public static WinRmOptions From(AppSettings settings)
        => new(settings.WinRmPort, settings.UseSsl, settings.SkipCertificateCheck);
}

/// <summary>Paramètres figés d'une opération (installation ou désinstallation) sur une liste de postes.</summary>
public sealed class DeploymentRequest
{
    public required IReadOnlyList<string> Targets { get; init; }

    public required IReadOnlyList<CatalogueEntry> Packages { get; init; }

    public required DeployAction Action { get; init; }

    /// <summary>Null = contexte du compte qui exécute WinPloy.</summary>
    public PSCredential? Credential { get; init; }

    public required int PackageTimeoutMin { get; init; }

    public required int MaxParallel { get; init; }

    public required string DepotRoot { get; init; }

    public WinRmOptions WinRm { get; init; } = WinRmOptions.Default;

    /// <summary>Acheminement du dossier des paquets personnalisés vers le poste.</summary>
    public TransferMode TransferMode { get; init; } = TransferMode.WinRm;

    /// <summary>
    /// Autorise un poste dans <c>TrustedHosts</c> quand le client WinRM refuse la session pour
    /// cette raison. Null = ne jamais y toucher.
    /// </summary>
    public ITrustedHostsGate? TrustedHosts { get; init; }

    /// <summary>Remplace le délai global calculé (tests).</summary>
    public TimeSpan? JobTimeoutOverride { get; init; }

    /// <summary>Délai global par poste : délai par paquet x nombre de paquets + 5 min.</summary>
    public TimeSpan JobTimeout => JobTimeoutOverride ?? TimeSpan.FromMinutes(PackageTimeoutMin * Math.Max(1, Packages.Count) + 5);

    /// <summary>
    /// Message d'erreur si l'opération ne peut pas être lancée, sinon null.
    /// Le mode SMB demande au poste cible de se connecter au partage : sans identifiants
    /// explicites, la session WinRM n'a aucune identité réseau à présenter (double saut).
    /// </summary>
    public string? Validate()
    {
        if (TransferMode == TransferMode.SmbPull && Credential is null && Packages.Any(p => p.IsCustom))
        {
            return Strings.SmbNeedsCredential;
        }
        return null;
    }
}

/// <summary>Résultat brut d'un paquet renvoyé par le poste.</summary>
public sealed record TargetResult(string Id, string Name, string Statut, string Message)
{
    public static string AllPackagesName => Strings.AllPackages;

    /// <summary>Résultat unique qui couvre tous les paquets du poste (poste injoignable, arrêt, délai).</summary>
    public static TargetResult ForAll(string statut, string message) => new("", AllPackagesName, statut, message);
}

/// <summary>Avancement d'un poste : texte de la colonne « Détail » et état WinRM.</summary>
public sealed record TargetStatus(string Message, string WinRM);

/// <summary>Erreur attendue sur un poste : le message est affiché tel quel.</summary>
public class TargetException : Exception
{
    public TargetException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Toutes les tentatives d'ouverture ont échoué, et au moins une ne pouvait pas passer par
/// Kerberos : ajouter la cible à <c>TrustedHosts</c> a une chance de la débloquer.
/// </summary>
public sealed class TrustedHostsRequiredException : TargetException
{
    public TrustedHostsRequiredException(string message)
        : base(message)
    {
    }
}
