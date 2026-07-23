using Bastion.Core;

namespace Bastion.Win32;

/// <summary>
/// One <see cref="WindowRegistry"/> entry — DESIGN.md §3.3's internal key
/// "<c>(HWND, PID, first-seen timestamp)</c>" (the <c>HWND</c> itself is the registry's dictionary
/// key, not repeated here) plus the resolved <see cref="WindowIdentity"/> and minted
/// <see cref="WindowId"/>.
/// </summary>
/// <remarks>Immutable once constructed — <see cref="WindowRegistry"/> never mutates an entry in
/// place; "never forget" (DESIGN.md §3.3) means an entry is either present unchanged or purged
/// entirely, never partially updated.</remarks>
internal sealed record WindowRegistryEntry(WindowId WindowId, uint Pid, DateTimeOffset FirstSeenUtc, WindowIdentity Identity);
