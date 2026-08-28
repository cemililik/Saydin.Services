namespace Saydin.DatabaseRoleBootstrap;

internal static class BootstrapExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 64;
    public const int SecretRejected = 65;
    public const int TargetRejected = 66;
    public const int RoleCollision = 67;
    public const int TopologyRejected = 68;
    public const int AuthenticationRejected = 69;
    public const int Timeout = 70;
    public const int DatabaseFailure = 71;
}

internal sealed class BootstrapRejectedException(string code, int exitCode) : Exception(code)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
}
