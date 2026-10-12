using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using WinPloy.Core.Deployment;
using WinPloy.Core.Models;
using WinPloy.Core.Properties;

namespace WinPloy.Tests;

public class LocalizationTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)(:[^}]*)?\}");

    [Fact]
    public void FrenchResources_CoverEveryKey_WithSamePlaceholders()
    {
        var neutral = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var french = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("fr"), true, false)!;
        var count = 0;

        foreach (DictionaryEntry entry in neutral)
        {
            var key = (string)entry.Key;
            var translated = french.GetString(key);
            Assert.False(string.IsNullOrEmpty(translated), $"Traduction française absente : {key}");
            Assert.Equal(Placeholders((string)entry.Value!), Placeholders(translated!));
            count++;
        }

        Assert.True(count > 0);
    }

    [Fact]
    public void English_IsTheNeutralLanguage()
    {
        TestCulture.With("en-US", () =>
        {
            Assert.StartsWith("Access denied", FriendlyErrors.Describe("Access is denied"));
            Assert.Equal("(all)", TargetResult.AllPackagesName);
        });
    }

    [Fact]
    public void UnsupportedLanguage_FallsBackToEnglish()
    {
        TestCulture.With("de-DE", () => Assert.Equal("No result returned by the computer.", FriendlyErrors.NoResultMessage));
    }

    [Fact]
    public void HostOutcome_FollowsTheInterfaceLanguage()
    {
        var results = new[] { new PackageResult("PC1", "Git", "Git.Git", ResultStatus.Ok, "ok", DateTime.Now) };

        Assert.Equal("1 paquet(s) traité(s) avec succès.", HostOutcome.Compute(results)!.Info);
        TestCulture.With("en-US", () => Assert.Equal("1 package(s) processed successfully.", HostOutcome.Compute(results)!.Info));
    }

    [Fact]
    public void RemoteTexts_AreKeyedWithoutPrefix_InTheCurrentLanguage()
    {
        var texts = RemoteTexts.Build();

        Assert.Equal("Déjà installé ({0})", texts["AlreadyInstalled"]);
        Assert.Contains("InstallDone", texts.Keys.Cast<string>());
        Assert.Contains("UninstallDone", texts.Keys.Cast<string>());
        Assert.DoesNotContain(texts.Keys.Cast<string>(), k => k.StartsWith("Remote_", StringComparison.Ordinal));

        TestCulture.With("en-US", () => Assert.Equal("Already installed ({0})", RemoteTexts.Build()["AlreadyInstalled"]));
    }

    private static string[] Placeholders(string text)
        => Placeholder.Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal).ToArray();
}
