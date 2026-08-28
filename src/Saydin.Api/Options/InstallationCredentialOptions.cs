using System.ComponentModel.DataAnnotations;

namespace Saydin.Api.Options;

public sealed class InstallationCredentialOptions
{
    public const string SectionName = "InstallationCredentials";

    [Required]
    public string SecretFile { get; init; } = string.Empty;

    [Range(1, short.MaxValue)]
    public short ActiveKeyVersion { get; init; } = 1;
}
