namespace Oroterm.Models;

public static class BuildInfo
{
    public static string Version { get; } = typeof(BuildInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
