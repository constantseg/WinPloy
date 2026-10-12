using System.Globalization;
using System.Runtime.CompilerServices;

namespace WinPloy.Tests;

/// <summary>
/// Les assertions portent sur les messages français : la langue de l'interface est fixée pour
/// toute la suite, quelle que soit celle de la machine qui lance les tests.
/// </summary>
internal static class TestCulture
{
    public static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    [ModuleInitializer]
    internal static void UseFrench()
    {
        CultureInfo.DefaultThreadCurrentUICulture = French;
        CultureInfo.CurrentUICulture = French;
    }

    /// <summary>Exécute <paramref name="action"/> avec l'interface dans une autre langue.</summary>
    public static void With(string culture, Action action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
