using Polly.CircuitBreaker;
using Polly.Timeout;
using Saydin.Shared.Exceptions;

namespace Saydin.PriceIngestion.Adapters;

internal static class ProviderFailureClassifier
{
    public static bool IsRetryable(Exception exception) => exception is
        HttpRequestException or TimeoutRejectedException or BrokenCircuitException
        or TimeoutException or TaskCanceledException or ExternalApiException;
}
