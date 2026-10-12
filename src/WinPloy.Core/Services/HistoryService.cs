using System.Globalization;
using System.Text.Json.Nodes;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Services;

/// <summary>
/// Historique des opérations (historique-deploiements.json), limité aux 200 dernières.
/// Les entrées existantes sont gardées telles quelles, y compris celles écrites par la version PowerShell.
/// </summary>
public sealed class HistoryService
{
    public const int MaxEntries = 200;

    private readonly string _file;
    private readonly LogService _log;

    public HistoryService(string file, LogService log)
    {
        _file = file;
        _log = log;
    }

    public void Append(IEnumerable<PackageResult> results)
    {
        JsonArray history;
        try
        {
            history = File.Exists(_file) ? JsonHelpers.ToArray(JsonHelpers.ParseFile(_file)) : new JsonArray();
        }
        catch (Exception)
        {
            history = new JsonArray();
        }

        var rows = new JsonArray();
        foreach (var result in results)
        {
            rows.Add(new JsonObject
            {
                ["Ordinateur"] = result.Ordinateur,
                ["Paquet"] = result.Paquet,
                ["Id"] = result.Id,
                ["Statut"] = result.Statut,
                ["Message"] = result.Message,
                ["Horodatage"] = result.Horodatage.ToString("s", CultureInfo.InvariantCulture),
            });
        }

        history.Add(new JsonObject
        {
            ["Date"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            ["Resultats"] = rows,
        });
        while (history.Count > MaxEntries)
        {
            history.RemoveAt(0);
        }

        try
        {
            File.WriteAllText(_file, JsonHelpers.Serialize(history), JsonHelpers.Utf8NoBom);
        }
        catch (Exception ex)
        {
            _log.Write(string.Format(Strings.HistoryWriteFailed, ex.Message), LogService.Warning);
        }
    }
}
