namespace Saydin.PriceIngestion.Workers;

/// <summary>
/// Process termination intent. Injectable so supervision tests never mutate the
/// test runner's global <see cref="Environment.ExitCode"/> state.
/// </summary>
public interface IProcessExitCodeSink
{
    int ExitCode { get; set; }
}

public sealed class EnvironmentProcessExitCodeSink : IProcessExitCodeSink
{
    public int ExitCode
    {
        get => Environment.ExitCode;
        set => Environment.ExitCode = value;
    }
}
