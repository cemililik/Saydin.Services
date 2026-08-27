namespace Saydin.DatabaseSecurity;

/// <summary>
/// Shared backtracking budget for every <see cref="System.Text.RegularExpressions.Regex"/>
/// built by the control-plane assemblies.
/// </summary>
/// <remarks>
/// The patterns guarded by this budget are anchored and linear, so the timeout is a
/// fail-closed backstop rather than an expected code path: a future pattern edit that
/// introduces catastrophic backtracking surfaces as a bounded
/// <see cref="System.Text.RegularExpressions.RegexMatchTimeoutException"/> instead of
/// pinning a CPU inside a one-shot control-plane process.
/// </remarks>
public static class RegexTimeouts
{
    /// <summary>One second — orders of magnitude above any linear match on bounded input.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromSeconds(1);
}
