namespace WinPloy.Core.Deployment;

/// <summary>
/// Autorise un poste dans <c>TrustedHosts</c> du client WinRM, à la demande et une seule fois.
/// </summary>
public interface ITrustedHostsGate
{
    /// <summary>
    /// Autorise <paramref name="computer"/> s'il ne l'a pas déjà été. Retourne vrai si la
    /// configuration a changé, donc si un nouvel essai de session a un sens.
    /// </summary>
    bool TryAuthorize(string computer);
}

/// <summary>
/// Portail d'accès à <c>TrustedHosts</c> pour la durée d'une opération.
///
/// Sur un domaine, <c>TrustedHosts</c> est inutile : WinRM authentifie en Kerberos et un poste
/// joint au domaine, joint par son nom, porte la confiance par lui-même. La liste ne sert qu'aux
/// cas où Kerberos ne peut pas s'appliquer — connexion par adresse IP, compte local, poste hors
/// domaine — c'est-à-dire quand l'authentification retombe sur NTLM.
///
/// WinPloy n'y touche donc plus par précaution : un poste n'y est ajouté que si le client WinRM
/// a refusé la session en désignant lui-même <c>TrustedHosts</c> comme la cause. Dans un domaine
/// correctement configuré, rien n'est jamais écrit.
///
/// Les postes traités en parallèle passent par le même verrou : la configuration du client WinRM
/// se lit et se réécrit d'un bloc, deux écritures simultanées se perdraient.
/// </summary>
public sealed class TrustedHostsGate : ITrustedHostsGate
{
    private readonly Func<IReadOnlyCollection<string>, IReadOnlyList<string>> _authorize;
    private readonly Action<string> _warn;
    private readonly object _sync = new();
    private readonly HashSet<string> _attempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _added = [];

    public TrustedHostsGate(Func<IReadOnlyCollection<string>, IReadOnlyList<string>> authorize, Action<string> warn)
    {
        _authorize = authorize;
        _warn = warn;
    }

    /// <summary>Entrées réellement écrites, à retirer à la fin de l'opération.</summary>
    public IReadOnlyList<string> Added
    {
        get
        {
            lock (_sync)
            {
                return _added.ToArray();
            }
        }
    }

    public bool TryAuthorize(string computer)
    {
        lock (_sync)
        {
            // Un seul essai par poste : sans cela, un refus pour une autre raison bouclerait.
            if (!_attempted.Add(computer))
            {
                return false;
            }

            try
            {
                var added = _authorize([computer]);
                _added.AddRange(added);
                return added.Count > 0;
            }
            catch (Exception ex)
            {
                _warn($"{computer} : ajout aux hôtes approuvés WinRM impossible ({ex.Message}).");
                return false;
            }
        }
    }
}
