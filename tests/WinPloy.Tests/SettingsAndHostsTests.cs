using System.Management.Automation.Runspaces;
using WinPloy.Core.Deployment;
using WinPloy.Core.Models;
using WinPloy.Core.Services;

namespace WinPloy.Tests;

public class WinRmSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SettingsService _service;

    public WinRmSettingsTests()
    {
        _service = new SettingsService(_temp.File("config.json"), new LogService(_temp.File("gui.log")));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Defaults_AreHttpOn5985_AndWinRmTransfer()
    {
        var settings = new AppSettings();

        Assert.Equal(5985, settings.WinRmPort);
        Assert.False(settings.UseSsl);
        Assert.False(settings.SkipCertificateCheck);
        Assert.Equal(TransferMode.WinRm, settings.TransferMode);
    }

    [Theory]
    [InlineData(false, 5985)]
    [InlineData(true, 5986)]
    public void DefaultPortFor_FollowsProtocol(bool useSsl, int expected)
    {
        Assert.Equal(expected, AppSettings.DefaultPortFor(useSsl));
    }

    [Fact]
    public void Load_FileWithoutPort_TakesDefaultOfItsProtocol()
    {
        // Fichier écrit par une version antérieure, puis passé en HTTPS à la main.
        File.WriteAllText(_temp.File("config.json"), """{ "CataloguePath": "\\\\srv\\d\\c.json", "UseSsl": true }""");

        var settings = _service.Load();

        Assert.True(settings.UseSsl);
        Assert.Equal(5986, settings.WinRmPort);
    }

    [Fact]
    public void Load_PortOutOfRange_TakesDefaultOfItsProtocol()
    {
        File.WriteAllText(_temp.File("config.json"), """{ "WinRmPort": 70000, "UseSsl": "true" }""");

        Assert.Equal(5986, _service.Load().WinRmPort);
    }

    [Fact]
    public void Load_UnknownTransferMode_FallsBackToWinRm()
    {
        File.WriteAllText(_temp.File("config.json"), """{ "TransferMode": "Carrier pigeon" }""");

        Assert.Equal(TransferMode.WinRm, _service.Load().TransferMode);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsEveryWinRmSetting()
    {
        var expected = new AppSettings
        {
            CataloguePath = @"\\srv\depot\catalogue.json",
            PackageTimeoutMin = 45,
            MaxParallel = 120,
            WinRmPort = 5986,
            UseSsl = true,
            SkipCertificateCheck = true,
            TransferMode = TransferMode.SmbPull,
        };

        Assert.True(_service.TrySave(expected, out var error));
        Assert.Null(error);
        Assert.Equal(expected, _service.Load());
    }

    [Fact]
    public void Load_ParallelAbove200_TakesDefault()
    {
        File.WriteAllText(_temp.File("config.json"), """{ "MaxParallel": 201 }""");
        Assert.Equal(AppSettings.DefaultParallel, _service.Load().MaxParallel);

        File.WriteAllText(_temp.File("config.json"), """{ "MaxParallel": 200 }""");
        Assert.Equal(200, _service.Load().MaxParallel);
    }

    [Theory]
    [InlineData("true", false, true)]
    [InlineData("False", true, false)]
    [InlineData("\"true\"", false, true)]
    [InlineData("oui", false, false)]
    [InlineData(null, true, true)]
    public void ParseBool_AcceptsJsonAndText(string? text, bool fallback, bool expected)
    {
        Assert.Equal(expected, SettingsService.ParseBool(text, fallback));
    }
}

public class TrustedHostsUnmergeTests
{
    [Fact]
    public void Unmerge_RemovesOnlyWhatWasAdded()
    {
        Assert.Equal("pc1,pc9", TrustedHostsService.Unmerge("pc1 , PC2 , pc9", ["pc2"]));
    }

    [Fact]
    public void Unmerge_IgnoresCase_AndHandlesSpacing()
    {
        Assert.Equal("PC2", TrustedHostsService.Unmerge(" PC1 , PC2 , pc3 ", ["pc1", "PC3"]));
    }

    [Fact]
    public void Unmerge_NothingToRemove_ReturnsNull()
    {
        Assert.Null(TrustedHostsService.Unmerge("PC1,PC2", ["PC9"]));
        Assert.Null(TrustedHostsService.Unmerge("", ["PC1"]));
    }

    [Fact]
    public void Unmerge_Wildcard_IsLeftAlone()
    {
        // « * » n'a pas été posé par WinPloy : le retirer ouvrirait un incident de production.
        Assert.Null(TrustedHostsService.Unmerge("*", ["PC1"]));
    }

    [Fact]
    public void Unmerge_LastEntry_GivesEmptyValue()
    {
        Assert.Equal("", TrustedHostsService.Unmerge("PC1", ["pc1"]));
    }

    [Fact]
    public void MergeThenUnmerge_RestoresTheOriginalValue()
    {
        const string original = "SERVEUR1,SERVEUR2";
        var added = TrustedHostsService.Merge(original, ["PC1", "PC2"], out var missing)!;

        Assert.Equal(["PC1", "PC2"], missing);
        Assert.Equal(original, TrustedHostsService.Unmerge(added, missing));
    }
}

public class DeploymentRequestValidationTests
{
    [Fact]
    public void SmbPull_WithoutCredential_IsRefused()
    {
        var problem = Request(TransferMode.SmbPull, credential: false, custom: true).Validate();

        Assert.Contains("identifiants explicites", problem);
    }

    [Fact]
    public void SmbPull_WithCredential_IsAccepted()
    {
        Assert.Null(Request(TransferMode.SmbPull, credential: true, custom: true).Validate());
    }

    [Fact]
    public void SmbPull_WinGetOnly_NeedsNoCredential()
    {
        // Aucun dossier à copier : le partage n'est jamais sollicité.
        Assert.Null(Request(TransferMode.SmbPull, credential: false, custom: false).Validate());
    }

    [Fact]
    public void WinRmTransfer_NeedsNoCredential()
    {
        Assert.Null(Request(TransferMode.WinRm, credential: false, custom: true).Validate());
    }

    [Fact]
    public void WinRmOptions_LabelNamesPortAndProtocol()
    {
        Assert.Equal("5985/HTTP", WinRmOptions.Default.Label);
        Assert.Equal("5986/HTTPS", WinRmOptions.From(new AppSettings { WinRmPort = 5986, UseSsl = true }).Label);
    }

    private static DeploymentRequest Request(TransferMode mode, bool credential, bool custom)
    {
        var package = custom
            ? CatalogueEntry.Create("custom:A", "A", @"\\srv\d\A\i.ps1")!
            : CatalogueEntry.Create("Git.Git", "Git")!;

        return new DeploymentRequest
        {
            Targets = ["PC1"],
            Packages = [package],
            Action = DeployAction.Install,
            PackageTimeoutMin = 30,
            MaxParallel = 10,
            DepotRoot = @"\\srv\d",
            TransferMode = mode,
            Credential = credential ? Credentials.Fake() : null,
        };
    }
}

internal static class Credentials
{
    public static System.Management.Automation.PSCredential Fake()
    {
        var password = new System.Security.SecureString();
        password.AppendChar('x');
        return new System.Management.Automation.PSCredential(@"DOMAINE\admin", password);
    }
}

public class TrustedHostsGateTests
{
    [Fact]
    public void Gate_AuthorizesOncePerComputer()
    {
        var calls = new List<string>();
        var gate = Gate(calls, added: true);

        Assert.True(gate.TryAuthorize("PC1"));
        // Deuxième refus pour une autre raison : ne pas réécrire ni boucler.
        Assert.False(gate.TryAuthorize("PC1"));
        Assert.True(gate.TryAuthorize("PC2"));

        Assert.Equal(["PC1", "PC2"], calls);
        Assert.Equal(["PC1", "PC2"], gate.Added);
    }

    [Fact]
    public void Gate_ComputerAlreadyTrusted_ReportsNoChange()
    {
        // Le poste était déjà dans la liste : rien n'a été écrit, donc rien à retirer ensuite.
        var gate = Gate([], added: false);

        Assert.False(gate.TryAuthorize("PC1"));
        Assert.Empty(gate.Added);
    }

    [Fact]
    public void Gate_WriteRefused_IsReportedAndSwallowed()
    {
        var warnings = new List<string>();
        var gate = new TrustedHostsGate(
            _ => throw new InvalidOperationException("TrustedHosts est imposé par GPO"),
            warnings.Add);

        Assert.False(gate.TryAuthorize("PC1"));
        Assert.Empty(gate.Added);
        Assert.Contains("PC1", warnings.Single());
        Assert.Contains("imposé par GPO", warnings.Single());
    }

    [Fact]
    public void Gate_NothingAuthorized_LeavesNothingToUndo()
    {
        // Le cas courant sur un domaine : Kerberos suffit, la liste n'est jamais touchée.
        Assert.Empty(Gate([], added: false).Added);
    }

    [Fact]
    public void Request_WithoutGate_NeverTouchesTrustedHosts()
    {
        var request = new DeploymentRequest
        {
            Targets = ["PC1"],
            Packages = [CatalogueEntry.Create("Git.Git", "Git")!],
            Action = DeployAction.Install,
            PackageTimeoutMin = 30,
            MaxParallel = 10,
            DepotRoot = @"\\srv\d",
        };

        Assert.Null(request.TrustedHosts);
    }

    private static TrustedHostsGate Gate(List<string> calls, bool added) => new(
        computers =>
        {
            calls.AddRange(computers);
            return added ? computers.ToArray() : [];
        },
        _ => { });
}

/// <summary>
/// Choix du mécanisme d'authentification et décision de toucher ou non à TrustedHosts.
/// Déduits de la cible et du compte : le client WinRM joint le même texte d'aide citant
/// TrustedHosts à des échecs qui n'ont rien à voir avec la liste, donc le message ne décide rien.
/// </summary>
public class AuthenticationMechanismTests
{
    [Fact]
    public void CurrentContext_OnNamedHost_UsesDefault_SoKerberosApplies()
    {
        // Imposer Negotiate ici ferait refuser la session sous HTTP tant que la cible n'est pas
        // dans TrustedHosts : « les informations d'identification par défaut avec négociation
        // sous HTTP ne peuvent être utilisées que si... ».
        Assert.Equal(AuthenticationMechanism.Default, CredentialCandidates.MechanismFor("PC1", null));
        Assert.False(CredentialCandidates.MayNeedTrustedHost("PC1", null));
    }

    [Fact]
    public void CurrentContext_OnAddress_UsesImplicitCredentials()
    {
        // Pas de SPN pour une adresse : Kerberos est impossible.
        Assert.Equal(
            AuthenticationMechanism.NegotiateWithImplicitCredential,
            CredentialCandidates.MechanismFor("10.0.0.12", null));
        Assert.True(CredentialCandidates.MayNeedTrustedHost("10.0.0.12", null));
    }

    [Fact]
    public void ExplicitCredential_UsesNegotiate()
    {
        Assert.Equal(AuthenticationMechanism.Negotiate, CredentialCandidates.MechanismFor("PC1", User(@"DOMAINE\admin")));
    }

    [Theory]
    [InlineData(@"DOMAINE\admin", false)]
    [InlineData("admin@domaine.local", false)]
    [InlineData(@"PC1\admin", true)]
    [InlineData(@"pc1\admin", true)]
    [InlineData(@".\admin", true)]
    [InlineData("admin", true)]
    public void MayNeedTrustedHost_FollowsTheAccountForm(string user, bool expected)
    {
        // Un nom nu peut désigner un compte local : CredentialCandidates l'essaie aussi en POSTE\nom.
        Assert.Equal(expected, CredentialCandidates.MayNeedTrustedHost("PC1", User(user)));
    }

    private static System.Management.Automation.PSCredential User(string name)
    {
        var password = new System.Security.SecureString();
        password.AppendChar('x');
        return new System.Management.Automation.PSCredential(name, password);
    }
}
