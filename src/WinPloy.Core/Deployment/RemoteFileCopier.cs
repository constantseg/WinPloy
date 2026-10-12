using System.Collections.Concurrent;
using System.IO.Compression;
using System.Management.Automation;
using WinPloy.Core.Properties;

namespace WinPloy.Core.Deployment;

/// <summary>Ce qu'a coûté la copie d'un paquet sur un poste, pour le journal et le suivi.</summary>
internal sealed record CopyReport(int Files, int FromCache, long BytesSent, long BytesTotal)
{
    public string Summary
    {
        get
        {
            var cache = FromCache > 0 ? string.Format(Strings.CopyCached, FromCache) : "";
            return BytesSent == BytesTotal
                ? string.Format(Strings.CopySummary, Files, Megabytes(BytesSent), cache)
                : string.Format(Strings.CopySummaryPartial, Files, Megabytes(BytesSent), Megabytes(BytesTotal), cache);
        }
    }

    public static string Megabytes(long bytes) => string.Format(Strings.Megabytes, bytes / 1024d / 1024d);
}

/// <summary>
/// Copie le dossier d'un paquet de la machine d'administration vers le poste, par la session
/// WinRM : le poste cible n'accède jamais au partage.
///
/// Le contenu passe par un cache du poste adressé par empreinte SHA-256 : un fichier déjà
/// présent n'est pas renvoyé, et chaque bloc est compressé quand cela réduit sa taille.
/// Un redéploiement du même paquet, ou la relance d'un poste en échec, ne coûte donc
/// plus rien sur le réseau.
/// </summary>
internal static class RemoteFileCopier
{
    /// <summary>Taille d'un bloc : reste sous la limite par objet reçu de l'endpoint WinRM (10 Mo par défaut).</summary>
    public const int ChunkSize = 2 * 1024 * 1024;

    /// <summary>Âge au-delà duquel une entrée du cache du poste est supprimée.</summary>
    public const int CacheRetentionDays = 30;

    /// <summary>Empreintes des fichiers du dépôt, partagées entre les postes d'une même opération.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, long Ticks), string> LocalHashes = new();

    public static CopyReport CopyDirectory(
        RemoteSession session,
        string sourceDir,
        string stageDir,
        string cacheDir,
        IReadOnlyDictionary<string, string> knownHashes,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(path => new PackageFile(
                path,
                PackageIntegrity.RelativePath(sourceDir, path),
                new FileInfo(path).Length))
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // L'empreinte du manifeste a déjà été vérifiée : inutile de relire le fichier.
            file.Sha256 = knownHashes.TryGetValue(file.Relative, out var known) && known.Length > 0
                ? known.ToLowerInvariant()
                : LocalHash(file.Path);
        }

        var directories = Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories)
            .Select(dir => PackageIntegrity.RelativePath(sourceDir, dir))
            .ToArray();

        var items = files.Select(f => PsObjects.New(("Rel", f.Relative), ("Sha", f.Sha256))).ToArray();
        var missing = session
            .InvokeOrThrow(RemoteScripts.ReceiveFiles, Request("Plan", cacheDir, stageDir, items))
            .Select(o => o?.BaseObject?.ToString())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var toSend = files
            .Where(f => missing.Contains(f.Sha256))
            .GroupBy(f => f.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var bytesTotal = files.Sum(f => f.Length);
        var bytesToSend = toSend.Sum(f => f.Length);
        var fromCache = files.Count - toSend.Count;

        if (files.Count > 0)
        {
            progress?.Report(toSend.Count == 0
                ? string.Format(Strings.CopyAllCached, files.Count)
                : string.Format(Strings.CopyStarting, toSend.Count, CopyReport.Megabytes(bytesToSend))
                    + (fromCache > 0 ? string.Format(Strings.CopyStartingCached, fromCache) : "."));
        }

        var sent = 0L;
        foreach (var file in toSend)
        {
            SendFile(session, file, cacheDir, bytesToSend, ref sent, progress, cancellationToken);
        }

        session.InvokeOrThrow(
            RemoteScripts.ReceiveFiles,
            Request("Materialize", cacheDir, stageDir, items, directories, RemoteTexts.Build()));

        return new CopyReport(files.Count, fromCache, bytesToSend, bytesTotal);
    }

    private static void SendFile(
        RemoteSession session,
        PackageFile file,
        string cacheDir,
        long bytesToSend,
        ref long sent,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkSize];
        using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var append = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var raw = read == buffer.Length ? buffer : buffer.AsSpan(0, read).ToArray();
            var packed = Compress(raw, read);

            session.InvokeOrThrow(
                RemoteScripts.ReceiveFiles,
                PsObjects.New(
                    ("Mode", "Chunk"),
                    ("CacheDir", cacheDir),
                    ("Sha", file.Sha256),
                    ("Data", packed.Payload),
                    ("Compressed", packed.Compressed),
                    ("Append", append)));

            append = true;
            sent += read;
            if (bytesToSend > 0 && read > 0)
            {
                progress?.Report(
                    string.Format(Strings.CopySending, file.Relative, CopyReport.Megabytes(sent), CopyReport.Megabytes(bytesToSend)));
            }
            if (read < buffer.Length)
            {
                break;
            }
        }

        // L'empreinte est revérifiée sur le poste avant que le bloc rejoigne le cache.
        session.InvokeOrThrow(
            RemoteScripts.ReceiveFiles,
            PsObjects.New(("Mode", "Commit"), ("CacheDir", cacheDir), ("Sha", file.Sha256), ("Text", RemoteTexts.Build())));
    }

    /// <summary>
    /// Empreinte d'un fichier du dépôt, calculée une seule fois pour tous les postes.
    ///
    /// Sans manifeste d'intégrité, l'empreinte doit être calculée pour savoir si le poste a déjà
    /// le fichier. La faire par poste relirait tout le paquet depuis le partage autant de fois
    /// qu'il y a de postes — ce que le cache est précisément censé éviter. La clé inclut la taille
    /// et la date de modification : un paquet mis à jour pendant la session est bien recalculé.
    /// </summary>
    private static string LocalHash(string path)
    {
        var info = new FileInfo(path);
        var key = (path, info.Length, info.LastWriteTimeUtc.Ticks);
        return LocalHashes.GetOrAdd(key, k => PackageIntegrity.ComputeFile(k.Path));
    }

    /// <summary>Compresse un bloc, sauf si cela ne gagne rien (contenu déjà compressé : msi, exe, zip).</summary>
    private static (byte[] Payload, bool Compressed) Compress(byte[] raw, int length)
    {
        if (length == 0)
        {
            return (raw, false);
        }

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(raw, 0, length);
        }

        var compressed = buffer.ToArray();
        if (compressed.Length >= length)
        {
            return (length == raw.Length ? raw : raw.AsSpan(0, length).ToArray(), false);
        }
        return (compressed, true);
    }

    private static PSObject Request(
        string mode,
        string cacheDir,
        string stageDir,
        object? items,
        object? directories = null,
        object? texts = null)
        => PsObjects.New(
            ("Mode", mode),
            ("CacheDir", cacheDir),
            ("StageDir", stageDir),
            ("Items", items),
            ("Dirs", directories),
            ("Text", texts));

    private sealed class PackageFile
    {
        public PackageFile(string path, string relative, long length)
        {
            Path = path;
            Relative = relative;
            Length = length;
        }

        public string Path { get; }

        public string Relative { get; }

        public long Length { get; }

        public string Sha256 { get; set; } = "";
    }
}
