using System.Management.Automation;

namespace WinPloy.Core.Deployment;

/// <summary>Objets passés en argument aux scripts distants.</summary>
internal static class PsObjects
{
    /// <summary>PSCustomObject, désérialisé sur le poste comme celui de Select-Object.</summary>
    public static PSObject New(params (string Name, object? Value)[] properties)
    {
        var obj = new PSObject();
        foreach (var (name, value) in properties)
        {
            obj.Properties.Add(new PSNoteProperty(name, value));
        }
        return obj;
    }
}
