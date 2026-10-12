using System.Text.Json.Nodes;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Services;

public sealed class CatalogueException : Exception
{
    public CatalogueException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Catalogue partagé (fichier JSON sur un partage).
/// Chaque modification relit le fichier juste avant d'écrire pour limiter
/// l'écrasement des changements faits par un autre administrateur.
/// </summary>
public sealed class CatalogueService
{
    private readonly Func<string> _pathProvider;
    private readonly LogService _log;

    public CatalogueService(Func<string> pathProvider, LogService log)
    {
        _pathProvider = pathProvider;
        _log = log;
    }

    public string CataloguePath => _pathProvider();

    public List<CatalogueEntry> Read()
    {
        var path = CataloguePath;
        if (!File.Exists(path))
        {
            throw new CatalogueException(string.Format(Strings.CatalogueNotFound, path));
        }

        return JsonHelpers.ToArray(JsonHelpers.ParseFile(path))
            .Select(CatalogueEntry.FromJson)
            .OfType<CatalogueEntry>()
            .ToList();
    }

    /// <summary>Écriture atomique : fichier temporaire puis remplacement.</summary>
    public void Write(IEnumerable<CatalogueEntry> entries)
    {
        var path = CataloguePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var array = new JsonArray(entries.Select(e => (JsonNode?)e.ToJson()).ToArray());
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, JsonHelpers.Serialize(array), JsonHelpers.Utf8NoBom);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tmp, path, null);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // Le fichier temporaire a déjà été déplacé ou n'est pas supprimable : sans conséquence.
            }
        }
    }

    /// <summary>Charge le catalogue pour l'affichage. En cas d'erreur, journalise et retourne une liste vide.</summary>
    public List<CatalogueEntry> Import()
    {
        try
        {
            var entries = Read();
            _log.Write(string.Format(Strings.CatalogueLoaded, entries.Count));
            return entries;
        }
        catch (Exception ex)
        {
            _log.Write(string.Format(Strings.CatalogueUnreadable, ex.Message), LogService.Error);
            return [];
        }
    }

    public List<CatalogueEntry> Edit(Func<List<CatalogueEntry>, IEnumerable<CatalogueEntry?>> change)
    {
        var current = Read();
        var updated = change(current).OfType<CatalogueEntry>().ToList();
        Write(updated);
        return updated;
    }

    public List<CatalogueEntry> Add(CatalogueEntry entry)
    {
        var updated = Edit(current =>
        {
            if (current.Any(e => e.HasSameId(entry.Id)))
            {
                throw new CatalogueException(string.Format(Strings.CatalogueDuplicateId, entry.Id));
            }
            return current.Append(entry);
        });
        _log.Write(string.Format(Strings.CataloguePackageAdded, entry.Name, entry.Id));
        return updated;
    }

    public List<CatalogueEntry> Update(string oldId, CatalogueEntry entry)
    {
        var updated = Edit(current =>
        {
            if (!entry.HasSameId(oldId) && current.Any(e => e.HasSameId(entry.Id)))
            {
                throw new CatalogueException(string.Format(Strings.CatalogueDuplicateId, entry.Id));
            }
            if (!current.Any(e => e.HasSameId(oldId)))
            {
                throw new CatalogueException(string.Format(Strings.CataloguePackageGone, oldId));
            }
            return current.Select(e => e.HasSameId(oldId) ? entry : e);
        });
        _log.Write(string.Format(Strings.CataloguePackageUpdated, entry.Name, entry.Id));
        return updated;
    }

    public List<CatalogueEntry> Remove(IReadOnlyCollection<string> ids)
    {
        var updated = Edit(current => current.Where(e => !ids.Any(e.HasSameId)));
        foreach (var id in ids)
        {
            _log.Write(string.Format(Strings.CataloguePackageRemoved, id));
        }
        return updated;
    }
}
