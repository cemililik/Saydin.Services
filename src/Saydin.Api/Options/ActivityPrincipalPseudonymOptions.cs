using System.ComponentModel.DataAnnotations;

namespace Saydin.Api.Options;

public sealed class ActivityPrincipalPseudonymOptions
{
    public const string SectionName = "ActivityPrincipalPseudonym";

    [Required]
    public string SecretFile { get; init; } = string.Empty;
}
