using EPDeskExtractionSandbox.Configuration;
using Microsoft.Extensions.Options;

namespace EPDeskExtractionSandbox.Services;

public sealed class RequestConcurrencyGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public RequestConcurrencyGate(IOptions<SandboxOptions> options)
    {
        _semaphore = new SemaphoreSlim(
            options.Value.MaxConcurrentRequests,
            options.Value.MaxConcurrentRequests);
    }

    public bool TryEnter(out IDisposable? lease)
    {
        if (!_semaphore.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = new Lease(_semaphore);
        return true;
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
