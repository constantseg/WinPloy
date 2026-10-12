using System.Text.RegularExpressions;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Deployment;

/// <summary>État final d'un poste, calculé à partir de ses lignes de résultat.</summary>
public sealed record HostOutcome(string Etat, string Info)
{
    private const int MaxInfoLength = 160;
    private static readonly Regex Whitespace = new(@"\s+");

    public static HostOutcome? Compute(IReadOnlyList<PackageResult> results)
    {
        if (results.Count == 0)
        {
            return null;
        }

        if (results.Any(r => r.Statut == ResultStatus.Stopped))
        {
            return new HostOutcome(HostStates.Stopped, results[0].Message);
        }

        var failed = results.Where(r => r.Statut != ResultStatus.Ok).ToList();
        if (failed.Count == 0)
        {
            return new HostOutcome(HostStates.Ok, string.Format(Strings.OutcomeAllOk, results.Count));
        }

        var message = Whitespace.Replace(failed[0].Message, " ").Trim();
        if (message.Length > MaxInfoLength)
        {
            message = message[..MaxInfoLength] + "…";
        }
        var info = results.Count > 1 ? string.Format(Strings.OutcomeSomeFailed, failed.Count, results.Count, message) : message;
        return new HostOutcome(HostStates.Failed, info);
    }
}
