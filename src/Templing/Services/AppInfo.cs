using System.Reflection;

namespace Templing.Services;

/// <summary>What the app knows about itself: its version (from &lt;Version&gt; in Templing.csproj) and where its source lives.</summary>
public static class AppInfo
{
    public const string RepositoryUrl = "https://github.com/lykaiosnz/templing";

    /// <summary>The full informational version, e.g. "0.1.0+3f2a9c1…" (the build appends the commit).</summary>
    public static readonly string InformationalVersion =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// The demo build (<c>-p:Demo=true</c>; Pages serves it at /templing/demo/): sample data in memory instead of the
    /// browser's IndexedDB (<see cref="DataStore.Demo"/>). Set by Templing.csproj as assembly metadata.
    /// </summary>
    public static readonly bool Demo =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "Demo" && a.Value == "true");

    /// <summary>For display: "0.1.0 (3f2a9c1)", or just "0.1.0" when the build had no commit.</summary>
    public static readonly string Version = Format(InformationalVersion);

    /// <summary>"0.1.0+3f2a9c1…" as "0.1.0 (3f2a9c1)".</summary>
    public static string Format(string info)
    {
        var parts = info.Split('+', 2);
        return parts.Length == 2 && parts[1].Length > 0 ? $"{parts[0]} ({parts[1][..Math.Min(7, parts[1].Length)]})" : parts[0];
    }
}
