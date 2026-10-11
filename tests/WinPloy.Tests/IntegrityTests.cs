using WinPloy.Core.Deployment;
using WinPloy.Core.Models;

namespace WinPloy.Tests;

public class PackageIntegrityTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Compute_CoversSubdirectories_AndVerifyAccepts()
    {
        Build();

        var manifest = PackageIntegrity.Compute(_temp.Path);

        Assert.Equal(3, manifest.Count);
        Assert.Contains("install.ps1", manifest.Keys);
        Assert.Contains("setup.msi", manifest.Keys);
        Assert.Contains(Path.Combine("lang", "fr.txt").Replace('/', '\\'), manifest.Keys);
        Assert.All(manifest.Values, hash => Assert.Equal(64, hash.Length));
        Assert.Null(PackageIntegrity.Verify(manifest, _temp.Path));
    }

    [Fact]
    public void Verify_ModifiedFile_IsRefused()
    {
        Build();
        var manifest = PackageIntegrity.Compute(_temp.Path);

        File.WriteAllText(_temp.File("install.ps1"), "Start-Process cmd");

        var problem = PackageIntegrity.Verify(manifest, _temp.Path);
        Assert.Contains("empreinte SHA-256 différente", problem);
        Assert.Contains("install.ps1", problem);
    }

    [Fact]
    public void Verify_RemovedFile_IsRefused()
    {
        Build();
        var manifest = PackageIntegrity.Compute(_temp.Path);

        File.Delete(_temp.File("setup.msi"));

        Assert.Contains("absent du dépôt", PackageIntegrity.Verify(manifest, _temp.Path));
    }

    [Fact]
    public void Verify_AddedFile_IsRefused()
    {
        Build();
        var manifest = PackageIntegrity.Compute(_temp.Path);

        File.WriteAllText(_temp.File("payload.exe"), "MZ");

        var problem = PackageIntegrity.Verify(manifest, _temp.Path);
        Assert.Contains("fichier ajouté", problem);
        Assert.Contains("payload.exe", problem);
    }

    [Fact]
    public void Verify_EmptyManifest_ChecksNothing()
    {
        Assert.Null(PackageIntegrity.Verify(new Dictionary<string, string>(), @"Z:\dossier\inexistant"));
    }

    [Fact]
    public void Verify_MissingDirectory_IsRefused()
    {
        var manifest = new Dictionary<string, string> { ["install.ps1"] = new string('a', 64) };

        Assert.Contains("dossier du paquet introuvable", PackageIntegrity.Verify(manifest, _temp.File("absent")));
    }

    [Fact]
    public void Verify_IgnoresCaseOfPathsAndHashes()
    {
        File.WriteAllText(_temp.File("install.ps1"), "Write-Host ok");
        var hash = PackageIntegrity.ComputeFile(_temp.File("install.ps1"));

        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["INSTALL.PS1"] = hash.ToUpperInvariant(),
        };

        Assert.Null(PackageIntegrity.Verify(manifest, _temp.Path));
    }

    [Fact]
    public void ComputeFile_MatchesKnownSha256()
    {
        File.WriteAllText(_temp.File("vide.txt"), "");

        // SHA-256 de la chaîne vide.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            PackageIntegrity.ComputeFile(_temp.File("vide.txt")));
    }

    private void Build()
    {
        File.WriteAllText(_temp.File("install.ps1"), "Write-Host installation");
        File.WriteAllText(_temp.File("setup.msi"), "contenu msi");
        Directory.CreateDirectory(_temp.File("lang"));
        File.WriteAllText(Path.Combine(_temp.File("lang"), "fr.txt"), "bonjour");
    }
}

public class CatalogueEntryIntegrityTests
{
    [Fact]
    public void ToJson_ThenFromJson_KeepsManifest()
    {
        var files = new Dictionary<string, string>
        {
            ["setup.msi"] = "bbbb",
            ["install.ps1"] = "aaaa",
        };

        var json = CatalogueEntry.Create("custom:A", "A", "i", "u", "System", files)!.ToJson();

        // Trié : deux administrateurs qui recalculent l'empreinte produisent le même fichier.
        Assert.Equal("""{"install.ps1":"aaaa","setup.msi":"bbbb"}""", json["Files"]!.ToJsonString());

        var reloaded = CatalogueEntry.FromJson(json)!;
        Assert.True(reloaded.HasIntegrityManifest);
        Assert.Equal("aaaa", reloaded.Files["INSTALL.PS1"]);
        Assert.Equal(2, reloaded.Files.Count);
    }

    [Fact]
    public void ToJson_NoManifest_OmitsProperty()
    {
        var json = CatalogueEntry.Create("custom:A", "A", "i", "u", "User")!.ToJson();

        Assert.False(json.ContainsKey("Files"));
        Assert.False(CatalogueEntry.FromJson(json)!.HasIntegrityManifest);
    }

    [Fact]
    public void WinGetEntry_NeverCarriesManifest()
    {
        var entry = CatalogueEntry.Create("Git.Git", "Git", files: new Dictionary<string, string> { ["x"] = "y" })!;

        Assert.False(entry.HasIntegrityManifest);
        Assert.False(entry.ToJson().ContainsKey("Files"));
    }

    [Fact]
    public void Manifest_IsCopied_NotShared()
    {
        var files = new Dictionary<string, string> { ["install.ps1"] = "aaaa" };
        var entry = CatalogueEntry.Create("custom:A", "A", "i", "u", "User", files)!;

        files["ajoute.exe"] = "bbbb";

        Assert.Single(entry.Files);
    }

    [Fact]
    public void FromJson_SkipsEmptyEntries()
    {
        var json = CatalogueEntry.Create("custom:A", "A", "i", "u", "User")!.ToJson();
        json["Files"] = new System.Text.Json.Nodes.JsonObject
        {
            ["install.ps1"] = "aaaa",
            ["vide.txt"] = "",
        };

        Assert.Single(CatalogueEntry.FromJson(json)!.Files);
    }
}
