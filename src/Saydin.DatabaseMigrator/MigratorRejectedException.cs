namespace Saydin.DatabaseMigrator;

internal sealed class MigratorRejectedException : Exception
{
    public MigratorRejectedException(string code, string? detail = null, Exception? innerException = null)
        : base(detail is null ? code : $"{code}: {detail}", innerException)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }
    public string? Detail { get; }
}
