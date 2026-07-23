using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Bastion.Win32;
using Windows.Win32.UI.Accessibility;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Exercises <see cref="HookUnregistration.ApplyResult"/> — the pure function extracted from
/// <see cref="WinEventPumpService"/>'s shutdown path so interop.md §3.2's
/// only-free-the-context-after-a-successful-unhook rule is independently unit-testable with a
/// synthetic hook handle and a throwaway registry — no live hook required.
/// </summary>
public sealed class HookUnregistrationTests
{
    private static readonly HWINEVENTHOOK s_arbitraryHook = new(new IntPtr(0x1234));

    [Fact]
    public void SuccessfulUnhookRemovesTheRegistryEntryAndReturnsTrue()
    {
        var context = GCHandle.Alloc(new object(), GCHandleType.Normal);
        try
        {
            var contexts = new ConcurrentDictionary<HWINEVENTHOOK, GCHandle> { [s_arbitraryHook] = context };

            bool result = HookUnregistration.ApplyResult(s_arbitraryHook, unhookSucceeded: true, contexts);

            Assert.True(result);
            Assert.False(contexts.ContainsKey(s_arbitraryHook));
        }
        finally
        {
            context.Free();
        }
    }

    [Fact]
    public void FailedUnhookLeavesTheRegistryEntryInPlaceAndReturnsFalse()
    {
        var context = GCHandle.Alloc(new object(), GCHandleType.Normal);
        try
        {
            var contexts = new ConcurrentDictionary<HWINEVENTHOOK, GCHandle> { [s_arbitraryHook] = context };

            bool result = HookUnregistration.ApplyResult(s_arbitraryHook, unhookSucceeded: false, contexts);

            Assert.False(result);
            Assert.True(contexts.TryGetValue(s_arbitraryHook, out GCHandle remaining));
            Assert.Equal(context, remaining);
        }
        finally
        {
            context.Free();
        }
    }
}
