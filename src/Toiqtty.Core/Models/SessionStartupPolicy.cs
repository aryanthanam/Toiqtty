namespace Toiqtty.Models;

internal static class SessionStartupPolicy
{
    internal static SavedSession Select(SavedSession persisted, bool reconnecting)
    {
        if (reconnecting) return persisted;
        // Disk layout describes a previous host, not living shell ownership.
        // Keep preferences; startup configuration will create a new layout.
        return new SavedSession { Settings = Configuration.LuaConfigResult.Clone(persisted.Settings) };
    }
}
