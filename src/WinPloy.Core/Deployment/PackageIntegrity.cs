using System.Security.Cryptography;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Deployment;

/// <summary>
/// Empreinte SHA-256 du dossier d'un paquet personnalisé.
///
/// Le dossier entier est copié sur le poste puis exécuté, souvent en SYSTEM : qui peut écrire
/// dans le dépôt peut faire exécuter du code sur tout le parc. Le manifeste fige donc le contenu
/// attendu du dossier — chemin relatif, empreinte — et WinPloy refuse de déployer un dossier qui
/// n'y correspond plus, qu'un fichier ait été modifié, retiré ou ajouté.
///
/// Un manifeste vide désactive le contrôle : les catalogues écrits avant cette version
/// continuent de fonctionner tels quels.
/// </summary>
public static class PackageIntegrity
{
    /// <summary>Nom de la propriété du catalogue qui porte le manifeste.</summary>
    public const string JsonProperty = "Files";

    /// <summary>Empreinte d'un fichier, en hexadécimal minuscule.</summary>
    public static string ComputeFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Manifeste de tous les fichiers du dossier, sous-dossiers compris.</summary>
    public static IReadOnlyDictionary<string, string> Compute(string directory)
    {
        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFiles(directory))
        {
            manifest[RelativePath(directory, file)] = ComputeFile(file);
        }
        return manifest;
    }

    /// <summary>
    /// Compare le dossier au manifeste. Retourne le premier écart constaté, ou null si le dossier
    /// correspond exactement. Un manifeste vide ne vérifie rien.
    /// </summary>
    public static string? Verify(IReadOnlyDictionary<string, string> expected, string directory)
    {
        if (expected.Count == 0)
        {
            return null;
        }
        if (!Directory.Exists(directory))
        {
            return string.Format(Strings.IntegrityFolderNotFound, directory);
        }

        // Chemin relatif attendu -> chemin réel du fichier. Le fichier est ouvert par le chemin
        // trouvé sur le disque, jamais par la clé du manifeste : celle-ci vient du catalogue et
        // peut différer par la casse ou le séparateur.
        var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateFiles(directory))
        {
            actual[RelativePath(directory, file)] = file;
        }

        foreach (var (relative, hash) in expected.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!actual.TryGetValue(relative, out var path))
            {
                return string.Format(Strings.IntegrityFileMissing, relative);
            }
            if (!string.Equals(ComputeFile(path), hash, StringComparison.OrdinalIgnoreCase))
            {
                return string.Format(Strings.IntegrityHashMismatch, relative);
            }
        }

        var extra = actual.Keys.Where(k => !expected.ContainsKey(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return extra is null ? null : string.Format(Strings.IntegrityFileAdded, extra);
    }

    /// <summary>Chemin relatif au dossier du paquet, séparateurs Windows (clé du manifeste).</summary>
    public static string RelativePath(string directory, string file)
        => Path.GetRelativePath(directory, file).Replace('/', '\\');

    private static IEnumerable<string> EnumerateFiles(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
}
