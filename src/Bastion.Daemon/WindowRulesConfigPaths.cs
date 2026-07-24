namespace Bastion.Daemon;

/// <summary>
/// Every filesystem path GitHub issue #9's config-loading subsystem reads from or writes to.
/// Injectable (rather than a set of hardcoded statics) specifically so tests point the whole
/// subsystem at a temporary directory instead of the real per-user profile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two different homes for the shipped file vs. the user overlay, deliberately.</b> The shipped,
/// curated rules file ships next to <c>bastiond.exe</c> (<see cref="AppContext.BaseDirectory"/>) and
/// is overwritten on every install/update — it is versioned with the binary, exactly like any other
/// asset the app ships, so an update always carries a refreshed curated list with no "first run,
/// copy it into the user's profile" step that could go stale relative to the binary or be skipped
/// by an installer that declines to overwrite an existing file. The user's own overlay
/// (<see cref="UserRulesFilePath"/>) is the only file that must independently persist across
/// upgrades, so it alone lives under <c>%USERPROFILE%\.config\bastion\</c> (DESIGN.md §3.9) — the
/// directory <see cref="UserConfigDirectory"/>'s <see cref="IConfigDirectoryWatcher"/> watches.
/// </para>
/// <para>
/// <b><see cref="SchemaFilePath"/> is co-located with the user's editable file, not the shipped
/// one</b>: the published schema exists so the user's editor can validate/autocomplete the file
/// they actually hand-edit (<see cref="UserRulesFilePath"/>); the shipped file is not meant to be
/// hand-edited at all.
/// </para>
/// </remarks>
internal sealed record WindowRulesConfigPaths
{
    /// <summary>The curated community rules file shipped next to the daemon executable.</summary>
    public required string ShippedRulesFilePath { get; init; }

    /// <summary>
    /// The directory containing <see cref="UserRulesFilePath"/> and <see cref="SchemaFilePath"/> —
    /// what <see cref="IConfigDirectoryWatcher"/> watches (the *directory*, not the file: editors
    /// do atomic rename-replace, which a single-file watch handle can miss — DESIGN.md §3.9).
    /// </summary>
    public required string UserConfigDirectory { get; init; }

    /// <summary>The user's own editable rules overlay, layered over <see cref="ShippedRulesFilePath"/>.</summary>
    public required string UserRulesFilePath { get; init; }

    /// <summary>Where the published <c>JsonSchemaExporter</c> schema for <see cref="UserRulesFilePath"/>'s shape is written.</summary>
    public required string SchemaFilePath { get; init; }

    /// <summary>The real, production paths: shipped file beside the executable, user files under <c>%USERPROFILE%\.config\bastion\</c>.</summary>
    public static WindowRulesConfigPaths CreateDefault()
    {
        string userConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bastion");

        return new WindowRulesConfigPaths
        {
            ShippedRulesFilePath = Path.Combine(AppContext.BaseDirectory, "rules.default.jsonc"),
            UserConfigDirectory = userConfigDirectory,
            UserRulesFilePath = Path.Combine(userConfigDirectory, "rules.jsonc"),
            SchemaFilePath = Path.Combine(userConfigDirectory, "rules.schema.json"),
        };
    }
}
