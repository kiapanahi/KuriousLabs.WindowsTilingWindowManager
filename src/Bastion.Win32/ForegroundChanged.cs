namespace Bastion.Win32;

/// <summary>The foreground window changed (<c>EVENT_SYSTEM_FOREGROUND</c>).</summary>
/// <param name="Hwnd">The window that became foreground.</param>
internal sealed record ForegroundChanged(nint Hwnd) : CoalescedIntent;
