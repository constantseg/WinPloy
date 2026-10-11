namespace WinPloy.Core.Models;

/// <summary>Façon d'amener le dossier d'un paquet personnalisé sur le poste cible.</summary>
public enum TransferMode
{
    /// <summary>Le dossier est envoyé par la session WinRM : le poste cible n'accède jamais au partage.</summary>
    WinRm,

    /// <summary>Le poste cible monte le partage avec les identifiants fournis et copie lui-même le dossier.</summary>
    SmbPull,
}

/// <summary>Réglages enregistrés dans ProgramData\WinPloy\config.json.</summary>
public sealed record AppSettings
{
    public const string DefaultCataloguePath = @"\\SERVEUR\Depot\catalogue.json";
    public const int DefaultTimeoutMin = 30;
    public const int MinTimeoutMin = 1;
    public const int MaxTimeoutMin = 240;
    public const int DefaultParallel = 10;
    public const int MinParallel = 1;
    public const int MaxParallelLimit = 200;

    /// <summary>
    /// Au-delà, le poste d'administration devient le goulot d'étranglement en mode de transfert WinRM :
    /// l'interface le signale sans l'interdire.
    /// </summary>
    public const int RecommendedMaxParallel = 50;

    public const int DefaultWinRmPort = 5985;
    public const int DefaultWinRmSslPort = 5986;
    public const int MinPort = 1;
    public const int MaxPort = 65535;

    public string CataloguePath { get; init; } = DefaultCataloguePath;

    public int PackageTimeoutMin { get; init; } = DefaultTimeoutMin;

    public int MaxParallel { get; init; } = DefaultParallel;

    /// <summary>Port du service WinRM des postes cibles : 5985 en HTTP, 5986 en HTTPS.</summary>
    public int WinRmPort { get; init; } = DefaultWinRmPort;

    /// <summary>Session WinRM sur HTTPS. Le port n'est pas déduit : les deux réglages sont indépendants.</summary>
    public bool UseSsl { get; init; }

    /// <summary>
    /// Accepte un certificat non validable sur la cible (autosigné, nom différent).
    /// Sans effet en HTTP. Affaiblit l'authentification du serveur : à réserver aux parcs
    /// dont les certificats WinRM ne viennent pas d'une autorité de confiance.
    /// </summary>
    public bool SkipCertificateCheck { get; init; }

    public TransferMode TransferMode { get; init; } = TransferMode.WinRm;

    /// <summary>Dossier du catalogue : racine du dépôt des paquets personnalisés.</summary>
    public string DepotRoot => Path.GetDirectoryName(CataloguePath) ?? "";

    /// <summary>Port par défaut du protocole choisi, proposé quand on bascule HTTP/HTTPS.</summary>
    public static int DefaultPortFor(bool useSsl) => useSsl ? DefaultWinRmSslPort : DefaultWinRmPort;
}
