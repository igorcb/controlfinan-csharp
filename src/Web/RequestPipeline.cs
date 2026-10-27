using System.Security.Cryptography;
using System.Text;
using ControlFinan.Models;

namespace ControlFinan.Web;

/// <summary>What the request pipeline found: the current user and its session.</summary>
public record CurrentUser(User User, long SessionId);

/// <summary>
/// Runs before routing: checks the CSRF token of every POST, applies "_method", loads the session and
/// sends guests to the login (Rails' require_authentication), except on the public pages.
/// </summary>
public class RequestPipeline(RequestDelegate next)
{
    private static readonly HashSet<string> Public =
        ["/session/new", "/session", "/registration/new", "/registration", "/passwords/new", "/passwords", "/up", "/search_account", "/icon.png", "/icon.svg"];

    public async Task InvokeAsync(HttpContext context, Web web, Repository repository)
    {
        var request = context.Request;
        if (HttpMethods.IsPost(request.Method))
        {
            var form = request.HasFormContentType ? await request.ReadFormAsync() : null;
            var cookie = request.Cookies[Web.CsrfCookie] ?? "";
            var token = form?["authenticity_token"].ToString() ?? "";
            if (cookie.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(token)))
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                return;
            }
            var method = form?["_method"].ToString().ToUpperInvariant();
            if (method is "DELETE" or "PATCH" or "PUT") request.Method = method;
        }

        var sessionValue = web.ReadSigned(request, Web.SessionCookie);
        if (long.TryParse(sessionValue, out var sessionId) && await repository.FindUserBySession(sessionId) is { } user)
            context.Items[nameof(CurrentUser)] = new CurrentUser(user, sessionId);

        var path = request.Path.Value ?? "/";
        var signingOut = path == "/session" && HttpMethods.IsDelete(request.Method);
        var isPublic = Public.Contains(path) || path.StartsWith("/passwords/") || path.StartsWith("/css/") || path.StartsWith("/js/");
        if (!context.Items.ContainsKey(nameof(CurrentUser)) && (signingOut || !isPublic))
        {
            web.SetSigned(context.Response, Web.ReturnToCookie, $"http://{request.Host}{request.Path}{request.QueryString}");
            context.Response.Redirect("/session/new");
            return;
        }
        await next(context);
    }
}

public static class HttpContextExtensions
{
    public static CurrentUser? Current(this HttpContext context) => context.Items[nameof(CurrentUser)] as CurrentUser;
}
