using System.Globalization;
using System.Windows.Data;
using WinPloy.Core.Models;
using WinPloy.Properties;

namespace WinPloy.Converters;

/// <summary>
/// Libellé affiché pour un état de poste ou un statut de paquet.
/// Les valeurs internes (EN COURS, ECHEC...) restent celles des fichiers d'historique et des exports.
/// </summary>
public sealed class StatusLabelConverter : IValueConverter
{
    public static string Label(string? value) => value switch
    {
        HostStates.Pending => Strings.Status_Pending,
        HostStates.Running => Strings.Status_Running,
        HostStates.Ok => Strings.Status_Ok,
        HostStates.Failed => Strings.Status_Failed,
        HostStates.Stopped => Strings.Status_Stopped,
        _ => value ?? "",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Label(value?.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
