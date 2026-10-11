using System.Management.Automation;
using WinPloy.Core.Models;

namespace WinPloy.Core.Deployment;

public interface ITargetRunner
{
    /// <summary>Traite un poste. Méthode bloquante, appelée hors du thread de l'interface.</summary>
    IReadOnlyList<TargetResult> Run(string computer, DeploymentRequest request, IProgress<TargetStatus> progress, CancellationToken cancellationToken);
}

/// <summary>
/// Traitement d'un poste : contrôle WinRM, contrôle d'intégrité et acheminement des paquets
/// personnalisés, exécution distante.
/// </summary>
public sealed class TargetRunner : ITargetRunner
{
    private const string RemoveStageScript = """
        param([string]$Path)
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        """;

    public IReadOnlyList<TargetResult> Run(string computer, DeploymentRequest request, IProgress<TargetStatus> progress, CancellationToken cancellationToken)
    {
        var winrm = request.WinRm;
        progress.Report(new TargetStatus($"Vérification du port WinRM {winrm.Label}...", "Vérification..."));
        if (!TcpProbe.IsOpen(computer, winrm.Port, TimeSpan.FromSeconds(3), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TargetException(
                $"WinRM injoignable sur {computer} (port {winrm.Label}). Vérifier que le poste est allumé, "
                + "que WinRM est activé par GPO et que le port des réglages correspond à celui du parc.");
        }
        progress.Report(new TargetStatus("WinRM joignable, ouverture de la session...", "Accessible"));

        using var session = RemoteSession.Open(computer, winrm, request.Credential, cancellationToken);
        progress.Report(new TargetStatus("Session WinRM ouverte, exécution...", "Actif"));

        // Dossier de travail protégé : seuls SYSTEM et les administrateurs locaux y ont accès.
        // Les scripts exécutés en SYSTEM par tâche planifiée en viennent, de même que le cache.
        var workRoot = session.InvokeForText(
            RemoteScripts.NewWorkDir,
            "Création du dossier de travail impossible sur le poste.",
            PsObjects.New(("Parent", ""), ("PruneDays", RemoteFileCopier.CacheRetentionDays)));
        // Chemin sur le poste cible : séparateur Windows, indépendamment de l'hôte.
        var cacheDir = workRoot.TrimEnd('\\') + @"\cache";

        var results = new List<TargetResult>();
        var staged = new List<PSObject>();
        foreach (var package in request.Packages.Where(p => p.IsCustom))
        {
            try
            {
                staged.Add(Stage(session, package, request, workRoot, cacheDir, progress, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new TargetResult(package.Id, package.Name, ResultStatus.Failed, $"Préparation : {ex.Message}"));
            }
        }

        var winget = request.Packages
            .Where(p => !p.IsCustom)
            .Select(p => PsObjects.New(("Id", p.Id), ("Name", p.Name)))
            .ToArray();
        var errors = new List<string>();
        var output = session.Invoke(
            RemoteScripts.RemoteAction,
            errors,
            winget,
            staged.ToArray(),
            request.Action.ToString(),
            request.PackageTimeoutMin,
            workRoot);

        var remote = output.Select(ToResult).OfType<TargetResult>().ToList();
        if (remote.Count == 0 && errors.Count > 0)
        {
            remote.Add(TargetResult.ForAll(ResultStatus.Failed, FriendlyErrors.Describe(string.Join(" | ", errors))));
        }
        results.AddRange(remote);
        return results;
    }

    private static PSObject Stage(
        RemoteSession session,
        CatalogueEntry package,
        DeploymentRequest request,
        string workRoot,
        string cacheDir,
        IProgress<TargetStatus> progress,
        CancellationToken cancellationToken)
    {
        var install = request.Action == DeployAction.Install;
        var scriptPath = install ? package.InstallScript : package.UninstallScript;
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            throw new TargetException($"Aucun script {(install ? "d'installation" : "de désinstallation")} défini pour ce paquet.");
        }

        var sourceDir = Path.GetDirectoryName(scriptPath) ?? "";
        if (!File.Exists(scriptPath))
        {
            throw new TargetException($"Script introuvable depuis la machine d'administration : {scriptPath}");
        }
        if (request.DepotRoot.Length > 0 && PackageValidation.SameDirectory(sourceDir, request.DepotRoot))
        {
            throw new TargetException("Le script est à la racine du dépôt : tout le dépôt serait copié. Placer chaque paquet dans son propre sous-dossier.");
        }

        // En mode SMB le poste lit le dépôt lui-même : le contrôle d'intégrité a lieu là-bas,
        // après la copie, et non ici — sinon il ne couvrirait pas le chemin réellement emprunté.
        if (package.HasIntegrityManifest && request.TransferMode == TransferMode.WinRm)
        {
            progress.Report(new TargetStatus($"Contrôle d'intégrité de {package.Name}…", ""));
            if (PackageIntegrity.Verify(package.Files, sourceDir) is { } problem)
            {
                throw new TargetException(
                    $"Contrôle d'intégrité refusé — {problem}. Si la modification est voulue, "
                    + "recalculer l'empreinte depuis la fenêtre du paquet.");
            }
        }

        var stageDir = session.InvokeForText(
            RemoteScripts.NewWorkDir,
            "Création du dossier temporaire impossible sur le poste.",
            PsObjects.New(("Parent", workRoot), ("PruneDays", 0)));
        try
        {
            Transfer(session, package, request, sourceDir, stageDir, cacheDir, progress, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            RemoveQuietly(session, stageDir);
            throw;
        }

        return PsObjects.New(
            ("Id", package.Id),
            ("Name", package.Name),
            ("Leaf", Path.GetFileName(scriptPath)),
            ("StageDir", stageDir),
            ("RunAs", package.RunAs));
    }

    private static void Transfer(
        RemoteSession session,
        CatalogueEntry package,
        DeploymentRequest request,
        string sourceDir,
        string stageDir,
        string cacheDir,
        IProgress<TargetStatus> progress,
        CancellationToken cancellationToken)
    {
        var relay = new Progress<string>(message => progress.Report(new TargetStatus(message, "")));

        if (request.TransferMode == TransferMode.SmbPull)
        {
            if (request.Credential is null)
            {
                throw new TargetException(
                    "Mode de transfert SMB : des identifiants explicites sont nécessaires pour que le poste "
                    + "accède au partage (bouton « Compte »).");
            }

            progress.Report(new TargetStatus($"{package.Name} : copie depuis le partage par le poste…", ""));
            session.InvokeOrThrow(
                RemoteScripts.PullFromShare,
                PsObjects.New(
                    ("Source", sourceDir),
                    ("StageDir", stageDir),
                    ("Credential", request.Credential),
                    ("Items", package.Files.Select(f => PsObjects.New(("Rel", f.Key), ("Sha", f.Value))).ToArray())));
            return;
        }

        var report = RemoteFileCopier.CopyDirectory(session, sourceDir, stageDir, cacheDir, package.Files, relay, cancellationToken);
        progress.Report(new TargetStatus($"{package.Name} : {report.Summary}.", ""));
    }

    private static void RemoveQuietly(RemoteSession session, string stageDir)
    {
        try
        {
            session.Invoke(RemoveStageScript, new List<string>(), stageDir);
        }
        catch (Exception)
        {
            // Le dossier temporaire reste sur le poste : l'erreur de copie d'origine est plus utile.
        }
    }

    private static TargetResult? ToResult(PSObject? obj)
    {
        if (obj?.Properties["Statut"] is null)
        {
            return null;
        }
        return new TargetResult(Text(obj, "Id"), Text(obj, "Name"), Text(obj, "Statut"), Text(obj, "Message"));
    }

    private static string Text(PSObject obj, string name) => obj.Properties[name]?.Value?.ToString() ?? "";
}
