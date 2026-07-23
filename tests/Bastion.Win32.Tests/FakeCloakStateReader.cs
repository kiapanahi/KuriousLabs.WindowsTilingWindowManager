using Bastion.Win32;

namespace Bastion.Win32.Tests;

/// <summary>
/// Test double for <see cref="ICloakStateReader"/> — returns a fixed, test-controlled answer
/// instead of ever calling <c>DwmGetWindowAttribute</c>, so the Coalescer's CLOAKED/UNCLOAKED-burst
/// heuristic (DESIGN.md §3.2/§4) is unit-testable without a real window.
/// </summary>
internal sealed class FakeCloakStateReader : ICloakStateReader
{
    /// <summary>The value every <see cref="IsCloaked"/> call returns. Defaults to <see langword="false"/>.</summary>
    public bool IsCloakedResult { get; set; }

    public bool IsCloaked(nint hwnd) => IsCloakedResult;
}
