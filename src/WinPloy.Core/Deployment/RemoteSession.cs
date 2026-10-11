using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;

namespace WinPloy.Core.Deployment;

/// <summary>
/// Session WinRM vers un poste : runspace distant sur l'endpoint par défaut (Microsoft.PowerShell,
/// donc Windows PowerShell 5.1 sur le poste). Toute commande en cours s'arrête à l'annulation.
/// </summary>
internal sealed class RemoteSession : IDisposable
{
    private const string ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";
    private const int OpenTimeoutMs = 30000;

    private readonly Runspace _runspace;
    private readonly CancellationToken _cancellationToken;

    private RemoteSession(Runspace runspace, CancellationToken cancellationToken)
    {
        _runspace = runspace;
        _cancellationToken = cancellationToken;
    }

    public static RemoteSession Open(string computer, WinRmOptions options, PSCredential? credential, CancellationToken cancellationToken)
    {
        var lastError = "";
        var mayNeedTrustedHost = false;
        foreach (var candidate in CredentialCandidates.Build(computer, credential))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = new WSManConnectionInfo(options.UseSsl, computer, options.Port, "/wsman", ShellUri, candidate)
            {
                AuthenticationMechanism = CredentialCandidates.MechanismFor(computer, candidate),
                OpenTimeout = OpenTimeoutMs,
                // Pas de délai par opération : une installation peut durer longtemps.
                OperationTimeout = 0,
            };
            if (options.UseSsl && options.SkipCertificateCheck)
            {
                connection.SkipCACheck = true;
                connection.SkipCNCheck = true;
                connection.SkipRevocationCheck = true;
            }

            var runspace = RunspaceFactory.CreateRunspace(connection);
            try
            {
                runspace.Open();
                return new RemoteSession(runspace, cancellationToken);
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                mayNeedTrustedHost |= CredentialCandidates.MayNeedTrustedHost(computer, candidate);
                runspace.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var who = credential is null ? "le contexte courant" : $"le compte '{credential.UserName}'";
        var message = $"Ouverture de session WinRM refusée sur {computer} ({options.Label}) avec {who}. {lastError}";
        throw mayNeedTrustedHost
            ? new TrustedHostsRequiredException(message)
            : new TargetException(message);
    }

    /// <summary>Exécute un script dans la session. Les erreurs non bloquantes sont ajoutées à <paramref name="errors"/>.</summary>
    public Collection<PSObject> Invoke(string script, ICollection<string> errors, params object?[] arguments)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        using var shell = PowerShell.Create();
        shell.Runspace = _runspace;
        shell.AddScript(script);
        foreach (var argument in arguments)
        {
            shell.AddArgument(argument);
        }

        Collection<PSObject> output;
        using (_cancellationToken.Register(() => StopQuietly(shell)))
        {
            output = shell.Invoke();
        }

        _cancellationToken.ThrowIfCancellationRequested();
        foreach (var error in shell.Streams.Error)
        {
            errors.Add(error.ToString());
        }
        return output;
    }

    /// <summary>Exécute un script et lève une exception si le poste renvoie une erreur.</summary>
    public Collection<PSObject> InvokeOrThrow(string script, params object?[] arguments)
    {
        var errors = new List<string>();
        var output = Invoke(script, errors, arguments);
        if (errors.Count > 0)
        {
            throw new TargetException(string.Join(" | ", errors));
        }
        return output;
    }

    /// <summary>Dernière valeur non vide renvoyée par un script, convertie en texte.</summary>
    public string InvokeForText(string script, string whenEmpty, params object?[] arguments)
        => InvokeOrThrow(script, arguments)
            .Select(o => o?.BaseObject?.ToString())
            .LastOrDefault(s => !string.IsNullOrEmpty(s))
            ?? throw new TargetException(whenEmpty);

    public void Dispose()
    {
        try
        {
            _runspace.Dispose();
        }
        catch (Exception)
        {
            // Session déjà coupée (poste éteint, réseau) : rien à libérer de plus.
        }
    }

    private static void StopQuietly(PowerShell shell)
    {
        try
        {
            shell.BeginStop(null, null);
        }
        catch (Exception)
        {
            // Commande déjà terminée.
        }
    }
}

/// <summary>Scripts PowerShell embarqués dans l'assembly.</summary>
internal static class RemoteScripts
{
    private static readonly Lazy<string> Action = new(() => Load("WinPloy.RemoteAction.ps1"));
    private static readonly Lazy<string> WorkDir = new(() => Load("WinPloy.NewWorkDir.ps1"));
    private static readonly Lazy<string> Pull = new(() => Load("WinPloy.PullFromShare.ps1"));
    private static readonly Lazy<string> Receive = new(() => Load("WinPloy.ReceiveFiles.ps1"));

    public static string RemoteAction => Action.Value;

    /// <summary>Crée un dossier de travail protégé (SYSTEM et Administrateurs seulement).</summary>
    public static string NewWorkDir => WorkDir.Value;

    /// <summary>Le poste tire le dossier du paquet depuis le partage.</summary>
    public static string PullFromShare => Pull.Value;

    /// <summary>Réception d'un bloc de fichier et gestion du cache adressé par empreinte.</summary>
    public static string ReceiveFiles => Receive.Value;

    private static string Load(string name)
    {
        using var stream = typeof(RemoteScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Ressource introuvable : {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
