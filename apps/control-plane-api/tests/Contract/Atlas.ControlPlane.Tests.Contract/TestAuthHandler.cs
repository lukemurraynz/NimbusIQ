using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Atlas.ControlPlane.Tests.Contract;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "contract-test-user"),
            new Claim(ClaimTypes.Name, "contract-test-user"),

            // Policies in Program.cs check the "roles" claim.
            new Claim("roles", "Atlas.Admin"),
            new Claim("roles", "Atlas.Analysis.Read"),
            new Claim("roles", "Atlas.Analysis.Write"),
            new Claim("roles", "Atlas.Recommendation.Read"),
            new Claim("roles", "Atlas.Recommendation.Approve")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
