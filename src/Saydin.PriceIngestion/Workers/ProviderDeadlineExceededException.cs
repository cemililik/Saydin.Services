namespace Saydin.PriceIngestion.Workers;

internal sealed class ProviderDeadlineExceededException(Guid windowId)
    : TimeoutException($"Provider absolute deadline exceeded for window {windowId:D}.");
