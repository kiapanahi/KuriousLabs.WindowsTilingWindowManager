using Bastion.Win32;
using Xunit;

namespace Bastion.Win32.Tests;

/// <summary>
/// Tier 1-adjacent tests (docs/engineering/testing.md §3) for <see cref="ShellComThread"/>: proves
/// work executes on a distinct, named STA thread and that both results and exceptions marshal back
/// through the returned <see cref="Task{TResult}"/> correctly.
/// </summary>
public sealed class ShellComThreadTests
{
    [Fact]
    public async Task InvokeAsyncRunsOnADistinctNamedStaThread()
    {
        using var shellComThread = new ShellComThread();
        int callingThreadId = Environment.CurrentManagedThreadId;

        (int ThreadId, ApartmentState ApartmentState, string? ThreadName) result = await shellComThread.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), Thread.CurrentThread.Name));

        Assert.NotEqual(callingThreadId, result.ThreadId);
        Assert.Equal(ApartmentState.STA, result.ApartmentState);
        Assert.Equal("Bastion.ShellComThread", result.ThreadName);
    }

    [Fact]
    public async Task InvokeAsyncReturnsTheActionsResult()
    {
        using var shellComThread = new ShellComThread();

        int result = await shellComThread.InvokeAsync(() => 42);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task InvokeAsyncMarshalsAnExceptionBackAsAFaultedTaskRatherThanThrowingSynchronously()
    {
        using var shellComThread = new ShellComThread();

        Task<int> task = shellComThread.InvokeAsync<int>(() => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
    }

    [Fact]
    public async Task EveryInvocationRunsOnTheSamePhysicalThread()
    {
        using var shellComThread = new ShellComThread();

        int first = await shellComThread.InvokeAsync(() => Environment.CurrentManagedThreadId);
        int second = await shellComThread.InvokeAsync(() => Environment.CurrentManagedThreadId);

        Assert.Equal(first, second);
    }

    [Fact]
    public void DisposeJoinsTheThreadWithoutHanging()
    {
        var shellComThread = new ShellComThread();

        shellComThread.Dispose();
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var shellComThread = new ShellComThread();

        shellComThread.Dispose();
        shellComThread.Dispose();
    }

    [Fact]
    public async Task InvokeAsyncAfterDisposeFaultsRatherThanHanging()
    {
        var shellComThread = new ShellComThread();
        shellComThread.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => shellComThread.InvokeAsync(() => 1));
    }
}
