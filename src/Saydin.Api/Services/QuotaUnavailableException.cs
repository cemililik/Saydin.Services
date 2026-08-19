namespace Saydin.Api.Services;

/// <summary>Fail-closed signal used when a finite quota cannot be decided safely.</summary>
public sealed class QuotaUnavailableException : Exception
{
    public const string ErrorCode = "quota_unavailable";

    public QuotaUnavailableException()
        : base("The quota service is temporarily unavailable.")
    {
    }
}
