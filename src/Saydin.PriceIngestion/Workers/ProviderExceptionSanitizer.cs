using System.Text.RegularExpressions;

namespace Saydin.PriceIngestion.Workers;

internal static partial class ProviderExceptionSanitizer
{
    private const int MaxDetailLength = 512;

    public static Exception ForLog(Exception exception) => new InvalidOperationException(
        $"{exception.GetType().Name}: {Detail(exception)}; stack={SafeStack(exception.StackTrace)}");

    public static string Detail(Exception exception)
    {
        var message = SecretValuePattern().Replace(exception.Message, "$1=[REDACTED]");
        message = QueryValuePattern().Replace(message, "$1=[REDACTED]");
        if (message.Length > MaxDetailLength)
            message = message[..MaxDetailLength];
        return $"{exception.GetType().Name}: {message}";
    }

    private static string SafeStack(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace)) return "unavailable";
        var firstLine = stackTrace.Split('\n', 2)[0].Trim();
        return firstLine.Length <= MaxDetailLength ? firstLine : firstLine[..MaxDetailLength];
    }

    [GeneratedRegex("(?im)\\b(api[-_]?key|key|appid|authorization|password|secret|token)\\s*(?:[:=]\\s*|\\s+)(?:(?:bearer|token|apikey)\\s+)?([^\\s&,;]+)")]
    private static partial Regex SecretValuePattern();

    [GeneratedRegex("(?i)([?&](?:api[-_]?key|key|token|secret|password))=[^&\\s]+")]
    private static partial Regex QueryValuePattern();
}
