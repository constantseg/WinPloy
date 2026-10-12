using System.Globalization;
using WinPloy.Core.Models;

namespace WinPloy.Services;

/// <summary>
/// Langue de l'interface. Les textes viennent de Properties\Strings.resx (anglais, langue neutre)
/// et de Strings.fr.resx ; une langue de Windows sans traduction retombe sur l'anglais.
/// </summary>
public static class UiLanguage
{
    /// <summary>
    /// Fixe la langue de l'interface pour tout le processus, threads de déploiement compris.
    /// À appeler avant la première fenêtre : les textes sont lus au chargement de chaque fenêtre.
    /// </summary>
    public static void Apply(string language)
    {
        if (language == AppSettings.SystemLanguage)
        {
            return;
        }

        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Nom affiché d'une langue proposée dans les réglages.</summary>
    public static string DisplayName(string language) => language switch
    {
        AppSettings.SystemLanguage => Properties.Strings.Settings_LanguageAuto,
        "en" => "English",
        "fr" => "Français",
        _ => language,
    };
}
