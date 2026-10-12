using System.Globalization;
using System.Text.Json.Nodes;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Services;

/// <summary>Lecture et écriture de config.json. Les valeurs hors bornes reprennent leur valeur par défaut.</summary>
public sealed class SettingsService
{
    private readonly string _file;
    private readonly LogService _log;

    public SettingsService(string file, LogService log)
    {
        _file = file;
        _log = log;
    }

    public AppSettings Load()
    {
        var settings = new AppSettings();
        if (!File.Exists(_file))
        {
            return settings;
        }

        JsonObject? obj;
        try
        {
            obj = JsonHelpers.ParseFile(_file) as JsonObject;
        }
        catch (Exception)
        {
            return settings;
        }

        if (obj is null)
        {
            return settings;
        }

        if (JsonHelpers.TryGetProperty(obj, "CataloguePath", out var path) && JsonHelpers.AsString(path) is { Length: > 0 } cataloguePath)
        {
            settings = settings with { CataloguePath = cataloguePath };
        }
        if (JsonHelpers.TryGetProperty(obj, "PackageTimeoutMin", out var timeout))
        {
            settings = settings with
            {
                PackageTimeoutMin = ParseClamped(JsonHelpers.AsString(timeout), AppSettings.MinTimeoutMin, AppSettings.MaxTimeoutMin, AppSettings.DefaultTimeoutMin),
            };
        }
        if (JsonHelpers.TryGetProperty(obj, "MaxParallel", out var parallel))
        {
            settings = settings with
            {
                MaxParallel = ParseClamped(JsonHelpers.AsString(parallel), AppSettings.MinParallel, AppSettings.MaxParallelLimit, AppSettings.DefaultParallel),
            };
        }
        if (JsonHelpers.TryGetProperty(obj, "UseSsl", out var useSsl))
        {
            settings = settings with { UseSsl = ParseBool(JsonHelpers.AsString(useSsl), false) };
        }
        if (JsonHelpers.TryGetProperty(obj, "SkipCertificateCheck", out var skipCertificate))
        {
            settings = settings with { SkipCertificateCheck = ParseBool(JsonHelpers.AsString(skipCertificate), false) };
        }

        // Lu après UseSsl : le port par défaut dépend du protocole, et un fichier
        // écrit par une version antérieure n'a pas de port.
        settings = settings with
        {
            WinRmPort = JsonHelpers.TryGetProperty(obj, "WinRmPort", out var port)
                ? ParseClamped(JsonHelpers.AsString(port), AppSettings.MinPort, AppSettings.MaxPort, AppSettings.DefaultPortFor(settings.UseSsl))
                : AppSettings.DefaultPortFor(settings.UseSsl),
        };

        if (JsonHelpers.TryGetProperty(obj, "TransferMode", out var transferMode))
        {
            settings = settings with { TransferMode = ParseTransferMode(JsonHelpers.AsString(transferMode)) };
        }
        if (JsonHelpers.TryGetProperty(obj, "Language", out var language))
        {
            settings = settings with { Language = ParseLanguage(JsonHelpers.AsString(language)) };
        }
        return settings;
    }

    public bool TrySave(AppSettings settings, out string? error)
    {
        try
        {
            var obj = new JsonObject
            {
                ["CataloguePath"] = settings.CataloguePath,
                ["PackageTimeoutMin"] = settings.PackageTimeoutMin,
                ["MaxParallel"] = settings.MaxParallel,
                ["WinRmPort"] = settings.WinRmPort,
                ["UseSsl"] = settings.UseSsl,
                ["SkipCertificateCheck"] = settings.SkipCertificateCheck,
                ["TransferMode"] = settings.TransferMode.ToString(),
                ["Language"] = settings.Language,
            };
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_file, JsonHelpers.Serialize(obj), JsonHelpers.Utf8NoBom);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Write(string.Format(Strings.SettingsSaveFailed, ex.Message), LogService.Error);
            return false;
        }
    }

    /// <summary>Entier compris entre <paramref name="min"/> et <paramref name="max"/>, sinon <paramref name="fallback"/>.</summary>
    public static int ParseClamped(string? text, int min, int max, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : fallback;
    }

    /// <summary>Booléen écrit en JSON (true) ou en texte ("true"), sinon <paramref name="fallback"/>.</summary>
    public static bool ParseBool(string? text, bool fallback)
        => bool.TryParse((text ?? "").Trim('"'), out var value) ? value : fallback;

    /// <summary>Langue proposée par WinPloy (« en », « fr »), sinon langue de Windows.</summary>
    public static string ParseLanguage(string? text)
    {
        var language = (text ?? "").Trim('"').Trim().ToLowerInvariant();
        return AppSettings.SupportedLanguages.Contains(language) ? language : AppSettings.SystemLanguage;
    }

    /// <summary>Mode de transfert nommé, sinon WinRM.</summary>
    public static TransferMode ParseTransferMode(string? text)
        => Enum.TryParse<TransferMode>((text ?? "").Trim('"'), ignoreCase: true, out var mode)
            ? mode
            : TransferMode.WinRm;
}
