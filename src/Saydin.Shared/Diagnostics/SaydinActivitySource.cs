using System.Diagnostics;

namespace Saydin.Shared.Diagnostics;

public static class SaydinActivitySource
{
    public static readonly ActivitySource Instance = new("Saydin", "1.0.0");
}
