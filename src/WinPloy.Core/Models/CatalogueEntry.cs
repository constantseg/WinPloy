using System.Text.Json.Nodes;
using WinPloy.Core.Deployment;
using WinPloy.Core.Services;

namespace WinPloy.Core.Models;

/// <summary>
/// Entrée du catalogue partagé.
///   WinGet : { "Type": "WinGet", "Id": "Git.Git", "Name": "Git" }
///   Custom : { "Type": "Custom", "Id": "custom:Nom", "Name": "Nom",
///              "InstallScript": "\\srv\partage\Nom\install.ps1",
///              "UninstallScript": "\\srv\partage\Nom\uninstall.ps1", "RunAs": "User|System",
///              "Files": { "install.ps1": "<sha256>", "setup.msi": "<sha256>" } }
/// Le type est toujours déduit de l'Id (préfixe "custom:").
/// « Files » est facultatif : absent, le contenu du dossier n'est pas contrôlé avant la copie.
/// </summary>
public sealed class CatalogueEntry
{
    public const string CustomPrefix = "custom:";
    public const string RunAsUser = "User";
    public const string RunAsSystem = "System";

    private static readonly IReadOnlyDictionary<string, string> NoFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private CatalogueEntry(
        PackageType type,
        string id,
        string name,
        string installScript,
        string uninstallScript,
        string runAs,
        IReadOnlyDictionary<string, string> files)
    {
        Type = type;
        Id = id;
        Name = name;
        InstallScript = installScript;
        UninstallScript = uninstallScript;
        RunAs = runAs;
        Files = files;
    }

    public PackageType Type { get; }

    public string Id { get; }

    public string Name { get; }

    public string InstallScript { get; }

    public string UninstallScript { get; }

    public string RunAs { get; }

    /// <summary>Manifeste d'intégrité du dossier du paquet : chemin relatif, empreinte SHA-256. Vide = pas de contrôle.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    public bool IsCustom => Type == PackageType.Custom;

    public bool RunsAsSystem => RunAs == RunAsSystem;

    /// <summary>Vrai si le contenu du dossier est contrôlé avant la copie sur le poste.</summary>
    public bool HasIntegrityManifest => Files.Count > 0;

    public string Label => IsCustom ? $"{Name} [Custom]" : Name;

    /// <summary>Libellé du type pour l'interface.</summary>
    public string TypeLabel => IsCustom ? "Personnalisé" : "WinGet";

    /// <summary>Normalise une entrée. Retourne null si l'Id est vide.</summary>
    public static CatalogueEntry? Create(
        string? id,
        string? name,
        string? installScript = null,
        string? uninstallScript = null,
        string? runAs = null,
        IReadOnlyDictionary<string, string>? files = null)
    {
        var cleanId = (id ?? "").Trim();
        if (cleanId.Length == 0)
        {
            return null;
        }

        var displayName = string.IsNullOrEmpty(name) ? cleanId : name;
        if (!cleanId.StartsWith(CustomPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new CatalogueEntry(PackageType.WinGet, cleanId, displayName, "", "", RunAsUser, NoFiles);
        }

        var mode = string.Equals(runAs, RunAsSystem, StringComparison.OrdinalIgnoreCase) ? RunAsSystem : RunAsUser;
        return new CatalogueEntry(PackageType.Custom, cleanId, displayName, installScript ?? "", uninstallScript ?? "", mode, Normalize(files));
    }

    /// <summary>Lit une entrée JSON. Les noms de propriété ne tiennent pas compte de la casse, comme en PowerShell.</summary>
    public static CatalogueEntry? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return Create(
            Read(obj, "Id"),
            Read(obj, "Name"),
            Read(obj, "InstallScript"),
            Read(obj, "UninstallScript"),
            Read(obj, "RunAs"),
            ReadFiles(obj));
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["Type"] = Type.ToString(),
            ["Id"] = Id,
            ["Name"] = Name,
        };
        if (IsCustom)
        {
            obj["InstallScript"] = InstallScript;
            obj["UninstallScript"] = UninstallScript;
            obj["RunAs"] = RunAs;
            if (HasIntegrityManifest)
            {
                var files = new JsonObject();
                foreach (var (relative, hash) in Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                {
                    files[relative] = hash;
                }
                obj[PackageIntegrity.JsonProperty] = files;
            }
        }
        return obj;
    }

    public bool HasSameId(string? id) => string.Equals(Id, id, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Label;

    private static string Read(JsonObject obj, string name)
        => JsonHelpers.TryGetProperty(obj, name, out var value) ? JsonHelpers.AsString(value) : "";

    private static IReadOnlyDictionary<string, string>? ReadFiles(JsonObject obj)
    {
        if (!JsonHelpers.TryGetProperty(obj, PackageIntegrity.JsonProperty, out var node) || node is not JsonObject files)
        {
            return null;
        }

        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, hash) in files)
        {
            var value = JsonHelpers.AsString(hash);
            if (relative.Length > 0 && value.Length > 0)
            {
                manifest[relative] = value;
            }
        }
        return manifest;
    }

    /// <summary>Copie défensive : l'entrée du catalogue reste immuable même si l'appelant garde son dictionnaire.</summary>
    private static IReadOnlyDictionary<string, string> Normalize(IReadOnlyDictionary<string, string>? files)
        => files is null || files.Count == 0
            ? NoFiles
            : new Dictionary<string, string>(files, StringComparer.OrdinalIgnoreCase);
}
