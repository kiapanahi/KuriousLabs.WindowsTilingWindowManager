namespace Bastion.Win32.Tests;

/// <summary>
/// In-process <see cref="IHwndJournalLock"/> fake, backed by a plain <see cref="SemaphoreSlim"/>
/// rather than a real named OS <see cref="Semaphore"/> — avoids touching real cross-process
/// synchronization objects in unit tests (which could otherwise contend with parallel test runs
/// sharing the same well-known lock name) while still providing genuine mutual exclusion, so a test
/// can share one instance between an <see cref="HwndJournalWriter"/> and an
/// <see cref="HwndJournalRestorer"/> to prove they actually serialize against each other.
/// </summary>
internal sealed class FakeHwndJournalLock : IHwndJournalLock, IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(initialCount: 1, maxCount: 1);

    /// <summary>Test-observable: how many times <see cref="AcquireAsync"/> has successfully acquired the lock.</summary>
    public int AcquireCount { get; private set; }

    /// <inheritdoc/>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        AcquireCount++;
        return new Releaser(_semaphore);
    }

    /// <inheritdoc/>
    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }
}
