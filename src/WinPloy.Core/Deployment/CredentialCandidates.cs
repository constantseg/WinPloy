using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Net;

namespace WinPloy.Core.Deployment;

/// <summary>
/// Comptes essayés pour ouvrir la session WinRM.
/// Un nom sans domaine ("admin") est essayé tel quel puis qualifié en compte local ("POSTE\admin").
/// ".\admin" devient "POSTE\admin".
/// </summary>
public static class CredentialCandidates
{
    public static IReadOnlyList<string> BuildUserNames(string computer, string userName)
    {
        if (!userName.Contains('\\') && !userName.Contains('@'))
        {
            return [userName, $@"{computer}\{userName}"];
        }
        if (userName.StartsWith(@".\", StringComparison.Ordinal))
        {
            return [$@"{computer}\{userName[2..]}"];
        }
        return [userName];
    }

    /// <summary>Sans identifiants, un seul essai avec le contexte courant (null).</summary>
    public static IReadOnlyList<PSCredential?> Build(string computer, PSCredential? credential)
    {
        if (credential is null)
        {
            return [null];
        }

        return BuildUserNames(computer, credential.UserName)
            .Select(user => user == credential.UserName ? credential : new PSCredential(user, credential.Password))
            .ToList();
    }

    /// <summary>
    /// Mécanisme d'authentification WinRM pour cette combinaison cible/compte.
    ///
    /// Sans identifiants, il ne faut surtout pas imposer <c>Negotiate</c> : le client WinRM refuse
    /// les informations d'identification par défaut avec négociation sous HTTP tant que la cible
    /// n'est pas dans <c>TrustedHosts</c>. <c>Default</c> laisse WinRM choisir, donc Kerberos sur un
    /// poste nommé joint au domaine, et la liste n'entre jamais en jeu. Une cible désignée par son
    /// adresse n'a pas de SPN : Kerberos y est impossible, et c'est l'option « informations
    /// d'identification implicites » qui dispense de la liste.
    ///
    /// Avec des identifiants explicites, <c>Negotiate</c> convient : il choisit Kerberos pour un
    /// compte de domaine et NTLM pour un compte local.
    /// </summary>
    public static AuthenticationMechanism MechanismFor(string computer, PSCredential? candidate)
    {
        if (candidate is not null)
        {
            return AuthenticationMechanism.Negotiate;
        }
        return IsAddress(computer)
            ? AuthenticationMechanism.NegotiateWithImplicitCredential
            : AuthenticationMechanism.Default;
    }

    /// <summary>
    /// Vrai si ajouter la cible à <c>TrustedHosts</c> peut débloquer cette tentative, c'est-à-dire
    /// si Kerberos ne peut pas s'appliquer : cible désignée par son adresse, compte local, ou nom
    /// de compte nu dont rien ne dit qu'il appartient au domaine.
    ///
    /// Déduit de la cible et du compte, jamais du message d'erreur : le client WinRM joint le même
    /// texte d'aide citant <c>TrustedHosts</c> à des échecs qui n'ont rien à voir avec la liste.
    /// </summary>
    public static bool MayNeedTrustedHost(string computer, PSCredential? candidate)
    {
        if (IsAddress(computer))
        {
            return true;
        }
        if (candidate is null)
        {
            return false;
        }

        var user = candidate.UserName;
        if (user.Contains('@'))
        {
            return false;
        }

        var separator = user.IndexOf('\\');
        if (separator < 0)
        {
            return true;
        }

        var prefix = user[..separator];
        return prefix == "." || prefix.Equals(computer, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAddress(string computer) => IPAddress.TryParse(computer, out _);
}
