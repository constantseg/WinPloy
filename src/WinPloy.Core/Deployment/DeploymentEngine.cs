using System.Diagnostics;
using WinPloy.Core.Models;

namespace WinPloy.Core.Deployment;

/// <summary>Suivi d'une opération. Appelé dans le contexte de synchronisation de l'appelant de RunAsync.</summary>
public interface IDeploymentObserver
{
    void OnTargetStarted(string computer);

    void OnTargetStatus(string computer, TargetStatus status);

    /// <summary><paramref name="elapsed"/> est null pour un poste jamais lancé.</summary>
    void OnTargetCompleted(string computer, IReadOnlyList<TargetResult> results, TimeSpan? elapsed);
}

/// <summary>
/// Orchestration : file d'attente des postes, parallélisme limité, délai global par poste, arrêt.
/// Les attentes gardent le contexte de l'appelant (pas de ConfigureAwait(false)) :
/// lancé depuis l'interface, l'observateur est toujours appelé sur le thread de l'interface.
/// </summary>
public sealed class DeploymentEngine
{
    public const string StoppedByUserMessage = "Arrêté par l'utilisateur. Une action déjà lancée sur le poste peut continuer.";
    public const string NotStartedMessage = "Non lancé (arrêt demandé).";

    private readonly ITargetRunner _runner;

    public DeploymentEngine(ITargetRunner runner) => _runner = runner;

    public async Task RunAsync(DeploymentRequest request, IDeploymentObserver observer, CancellationToken stopToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, request.MaxParallel));
        var tasks = request.Targets.Select(t => RunTargetAsync(t, request, observer, gate, stopToken)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task RunTargetAsync(string computer, DeploymentRequest request, IDeploymentObserver observer, SemaphoreSlim gate, CancellationToken stopToken)
    {
        try
        {
            await gate.WaitAsync(stopToken);
        }
        catch (OperationCanceledException)
        {
            ReportNotStarted(computer, observer);
            return;
        }

        try
        {
            // La place peut se libérer pendant le traitement de l'arrêt : le poste n'est pas lancé.
            if (stopToken.IsCancellationRequested)
            {
                ReportNotStarted(computer, observer);
                return;
            }
            await RunAcquiredAsync(computer, request, observer, stopToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ReportNotStarted(string computer, IDeploymentObserver observer)
        => observer.OnTargetCompleted(computer, [TargetResult.ForAll(ResultStatus.Stopped, NotStartedMessage)], null);

    private async Task RunAcquiredAsync(string computer, DeploymentRequest request, IDeploymentObserver observer, CancellationToken stopToken)
    {
        var clock = Stopwatch.StartNew();
        var completed = false;
        observer.OnTargetStarted(computer);

        var timeout = new CancellationTokenSource(request.JobTimeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stopToken, timeout.Token);
        var token = linked.Token;

        // Un poste abandonné (arrêt, délai) peut encore signaler son avancement : ignoré une fois terminé.
        var progress = new Progress<TargetStatus>(status =>
        {
            if (!completed)
            {
                observer.OnTargetStatus(computer, status);
            }
        });

        // Chaque poste occupe son thread pendant toute l'opération : le pool de threads n'en
        // injecte qu'un par seconde passé le nombre de cœurs, ce qui étalerait le démarrage de
        // plusieurs minutes sur un parc large. LongRunning donne un thread dédié tout de suite.
        var work = Task.Factory.StartNew(
            () => _runner.Run(computer, request, progress, token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        _ = work.ContinueWith(
            t =>
            {
                _ = t.Exception;
                linked.Dispose();
                timeout.Dispose();
            },
            TaskScheduler.Default);

        TargetResult[] results;
        try
        {
            var output = await work.WaitAsync(token);
            results = output.Count > 0
                ? output.ToArray()
                : new[] { TargetResult.ForAll(ResultStatus.Failed, FriendlyErrors.NoResultMessage) };
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            var result = stopToken.IsCancellationRequested
                ? TargetResult.ForAll(ResultStatus.Stopped, StoppedByUserMessage)
                : TargetResult.ForAll(ResultStatus.Failed, $"Délai global de {request.JobTimeout.TotalMinutes:0} min dépassé, opération arrêtée.");
            results = new[] { result };
        }
        catch (Exception ex)
        {
            results = new[] { TargetResult.ForAll(ResultStatus.Failed, FriendlyErrors.Describe(FriendlyErrors.Flatten(ex))) };
        }

        completed = true;
        observer.OnTargetCompleted(computer, results, clock.Elapsed);
    }
}
