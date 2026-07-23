namespace Bastion.Win32;

/// <summary>
/// The best identity <see cref="WindowIdentityResolver"/> could resolve for a window, per
/// DESIGN.md §3.3's identity-resolution chain. Rules matching (GitHub issue #9's JSONC config
/// loader) is the eventual consumer — <see cref="Kind"/> lets a rule distinguish an AUMID pattern
/// from an exe-path pattern without re-deriving which chain rung produced the value.
/// </summary>
internal readonly record struct WindowIdentity(WindowIdentityKind Kind, string? Value)
{
    /// <summary>The terminal, total case: every rung of the chain failed.</summary>
    public static WindowIdentity Unknown { get; } = new(WindowIdentityKind.Unknown, null);
}
