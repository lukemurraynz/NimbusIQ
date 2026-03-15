using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.ControlPlane.Api.Endpoints;

[ApiController]
[Route("api/v1/me")]
public class MeController : ControllerBase
{
    private static readonly string[] AnonymousRoles = ["Atlas.Admin"];

    [HttpGet]
    [AllowAnonymous]
    public IActionResult GetMe()
    {
        // When anonymous bypass is active the request is unauthenticated.
        // Return a static identity so the frontend renders normally.
        if (IsAnonymousBypassActive())
        {
            return Ok(new { name = "Anonymous", roles = AnonymousRoles });
        }

        var name =
            User.FindFirstValue("preferred_username") ??
            User.FindFirstValue("name") ??
            User.Identity?.Name;

        // Collect roles from the WS-Federation role claim URI and the short "roles" claim.
        const string wsFedRoleClaim = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
        var roles = User.FindAll(wsFedRoleClaim)
            .Concat(User.FindAll("roles"))
            .Select(c => c.Value)
            .Distinct()
            .ToArray();

        return Ok(new { name, roles });
    }

    private static bool IsAnonymousBypassActive() =>
        string.Equals(
            Environment.GetEnvironmentVariable("NIMBUSIQ_ALLOW_ANONYMOUS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
}

