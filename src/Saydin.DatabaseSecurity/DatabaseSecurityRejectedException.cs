namespace Saydin.DatabaseSecurity;

public enum DatabaseSecurityFailureKind
{
    InvalidArguments,
    SecretRejected,
    TargetRejected,
}

public sealed class DatabaseSecurityRejectedException(
    string code,
    DatabaseSecurityFailureKind kind) : Exception(code)
{
    public string Code { get; } = code;
    public DatabaseSecurityFailureKind Kind { get; } = kind;
}
