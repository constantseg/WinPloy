using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinPloy.Core.Services;

/// <summary>
/// Les cibles sont ajoutées à TrustedHosts du client WinRM : sans cela, toute bascule
/// de Kerberos vers NTLM (compte local, SPN, DNS, contexte courant) fait échouer la session.
/// Mise à jour par l'API COM WSMan.Automation (équivalent de Set-Item WSMan:\localhost\Client\TrustedHosts).
///
/// Les entrées ajoutées pour une opération sont retirées à la fin de celle-ci : la liste du poste
/// d'administration ne grossit pas indéfiniment, et l'assouplissement ne survit pas au besoin
/// ponctuel qui l'a motivé. Seules les entrées réellement ajoutées par WinPloy sont retirées,
/// jamais celles qui étaient déjà là.
/// </summary>
public sealed class TrustedHostsService
{
    private const string ClientConfigUri = "winrm/config/client";
    private const string WinRmServiceName = "WinRM";

    private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(30);

    private readonly LogService _log;

    public TrustedHostsService(LogService log) => _log = log;

    /// <summary>Nouvelle valeur de TrustedHosts, ou null si rien n'est à ajouter.</summary>
    public static string? Merge(string? current, IEnumerable<string> computers, out IReadOnlyList<string> missing)
    {
        missing = [];
        var items = Split(current);
        if (items.Contains("*"))
        {
            return null;
        }

        var toAdd = computers
            .Where(c => !items.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toAdd.Count == 0)
        {
            return null;
        }

        missing = toAdd;
        return string.Join(",", items.Concat(toAdd));
    }

    /// <summary>
    /// Valeur de TrustedHosts débarrassée de <paramref name="computers"/>, ou null si aucune
    /// de ces entrées n'y figure. Le joker « * » est laissé intact : il ne vient pas de WinPloy.
    /// </summary>
    public static string? Unmerge(string? current, IEnumerable<string> computers)
    {
        var items = Split(current);
        if (items.Contains("*"))
        {
            return null;
        }

        var toRemove = computers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = items.Where(i => !toRemove.Contains(i)).ToList();
        return kept.Count == items.Count ? null : string.Join(",", kept);
    }

    /// <summary>Ajoute les postes à TrustedHosts. Retourne les entrées réellement ajoutées, à retirer ensuite.</summary>
    public IReadOnlyList<string> Add(IReadOnlyCollection<string> computers)
    {
        if (computers.Count == 0)
        {
            return [];
        }

        IReadOnlyList<string> added = [];
        var written = Edit(
            current => Merge(current, computers, out added),
            onPolicy: () => throw new InvalidOperationException(
                "TrustedHosts est imposé par GPO : ajouter les postes dans la stratégie de groupe."));

        if (!written)
        {
            // Rien n'a été écrit : il n'y aura rien à retirer à la fin de l'opération.
            return [];
        }

        _log.Write($"TrustedHosts WinRM : ajout de {string.Join(", ", added)}.");
        return added;
    }

    /// <summary>Retire des entrées de TrustedHosts. Les erreurs sont journalisées, pas levées : le nettoyage ne doit pas masquer le résultat de l'opération.</summary>
    public void Remove(IReadOnlyCollection<string> computers)
    {
        if (computers.Count == 0)
        {
            return;
        }

        try
        {
            var removed = Edit(
                current => Unmerge(current, computers),
                // Devenu « imposé par GPO » entre-temps : la valeur locale n'est plus la nôtre à modifier.
                onPolicy: () => { });

            if (removed)
            {
                _log.Write($"TrustedHosts WinRM : retrait de {string.Join(", ", computers)}.");
            }
        }
        catch (Exception ex)
        {
            _log.Write(
                $"TrustedHosts WinRM : retrait de {string.Join(", ", computers)} impossible ({ex.Message}). "
                + "Les postes restent dans la liste des hôtes approuvés.",
                LogService.Warning);
        }
    }

    /// <summary>
    /// Lit la configuration du client WinRM, applique <paramref name="change"/> à TrustedHosts
    /// et réécrit la configuration si la valeur a changé. Retourne vrai si la configuration a
    /// effectivement été réécrite — <paramref name="change"/> peut n'avoir rien à faire, et une
    /// valeur imposée par GPO n'est jamais réécrite.
    /// </summary>
    private bool Edit(Func<string, string?> change, Action onPolicy)
    {
        EnsureWinRmServiceRunning();

        var type = Type.GetTypeFromProgID("WSMan.Automation", throwOnError: true)!;
        object? wsman = null;
        object? session = null;
        try
        {
            wsman = Activator.CreateInstance(type)!;
            session = ((dynamic)wsman).CreateSession();
            dynamic client = session!;
            string xml = client.Get(ClientConfigUri, 0);

            var document = XDocument.Parse(xml);
            var node = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "TrustedHosts")
                ?? throw new InvalidOperationException("Paramètre TrustedHosts absent de la configuration du client WinRM.");
            var updated = change(node.Value);
            if (updated is null)
            {
                return false;
            }
            if (IsFromPolicy(node))
            {
                onPolicy();
                return false;
            }

            // Les paramètres imposés par GPO sont en lecture seule : ils ne sont pas renvoyés.
            foreach (var locked in document.Descendants().Where(e => e != node && IsFromPolicy(e)).ToList())
            {
                locked.Remove();
            }
            node.Value = updated;
            client.Put(ClientConfigUri, document.ToString(SaveOptions.DisableFormatting), 0);
            return true;
        }
        finally
        {
            if (session is not null)
            {
                Marshal.FinalReleaseComObject(session);
            }
            if (wsman is not null)
            {
                Marshal.FinalReleaseComObject(wsman);
            }
        }
    }

    /// <summary>La configuration du client WinRM n'est accessible que si le service WinRM local tourne.</summary>
    private void EnsureWinRmServiceRunning()
    {
        using var service = new ServiceController(WinRmServiceName);
        if (service.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        if (service.Status != ServiceControllerStatus.StartPending)
        {
            service.Start();
        }
        service.WaitForStatus(ServiceControllerStatus.Running, ServiceStartTimeout);
        _log.Write("Service WinRM local démarré (nécessaire pour TrustedHosts).");
    }

    private static List<string> Split(string? value)
        => Regex.Split((value ?? "").Trim(), @"\s*,\s*").Where(s => s.Length > 0).ToList();

    private static bool IsFromPolicy(XElement element)
        => string.Equals((string?)element.Attribute("Source"), "GPO", StringComparison.OrdinalIgnoreCase);
}
