using FluentAssertions;
using Saydin.Api.Services;

namespace Saydin.Api.Tests.Services;

public class InstallationPrincipalContextTests
{
    private static readonly InstallationPrincipal Principal = new(
        Guid.Parse("11111111-1111-4111-8111-111111111111"),
        Guid.Parse("22222222-2222-4222-8222-222222222222"),
        1,
        "free",
        "active",
        "active");

    [Fact]
    public void Principal_BeforeResolution_ThrowsFailClosed()
    {
        var context = new InstallationPrincipalContext();

        context.IsResolved.Should().BeFalse();
        var act = () => context.PrincipalId;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Installation principal was not resolved for this request.");
    }

    [Fact]
    public void Set_ValidPrincipal_ExposesImmutableIdentity()
    {
        var context = new InstallationPrincipalContext();

        context.Set(Principal);

        context.IsResolved.Should().BeTrue();
        context.Principal.Should().BeSameAs(Principal);
        context.PrincipalId.Should().Be(Principal.PrincipalId);
        context.Tier.Should().Be("free");
    }

    [Fact]
    public void Set_Twice_RejectsPrincipalReplacement()
    {
        var context = new InstallationPrincipalContext();
        context.Set(Principal);

        var act = () => context.Set(Principal with
        {
            PrincipalId = Guid.Parse("33333333-3333-4333-8333-333333333333")
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Installation principal cannot be replaced within one request.");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Set_EmptyIdentity_RejectsResolution(bool emptyPrincipal, bool emptyCredential)
    {
        var context = new InstallationPrincipalContext();
        var candidate = Principal with
        {
            PrincipalId = emptyPrincipal ? Guid.Empty : Principal.PrincipalId,
            CredentialId = emptyCredential ? Guid.Empty : Principal.CredentialId
        };

        var act = () => context.Set(candidate);

        act.Should().Throw<ArgumentException>();
        context.IsResolved.Should().BeFalse();
    }
}
