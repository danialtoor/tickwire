using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Tickwire.Api.Services;

namespace Tickwire.Api.Endpoints;

/// <summary>Who is calling: a guest (bearer token from POST /api/guest), an admin (X-Admin-Key), or anonymous.</summary>
public sealed record Caller(string? ClientId, bool IsAdmin)
{
    public bool CanManage(string clientId) => IsAdmin || ClientId == clientId;
}

public static class Auth
{
    public static async Task<Caller> CallerAsync(HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<TickwireOptions>>().Value;
        var isAdmin = false;
        if (!string.IsNullOrEmpty(options.AdminKey) && http.Request.Headers.TryGetValue("X-Admin-Key", out var key))
        {
            isAdmin = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key.ToString()),
                Encoding.UTF8.GetBytes(options.AdminKey));
        }

        string? clientId = null;
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var sessions = http.RequestServices.GetRequiredService<SessionManager>();
            clientId = await sessions.ClientIdForTokenAsync(header["Bearer ".Length..].Trim(), http.RequestAborted);
        }

        return new Caller(clientId, isAdmin);
    }

    public static IResult Unauthorized(string detail = "Send the guest token from POST /api/guest as a Bearer token") =>
        Results.Problem(detail, statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

    public static IResult Forbidden(string detail = "You can only manage your own client") =>
        Results.Problem(detail, statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
}
