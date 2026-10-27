using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ControlFinan.Models;

namespace ControlFinan.Web;

/// <summary>Sends the password reset e-mail. This one writes it to the log; tests replace it.</summary>
public class Mailer(ILogger<Mailer> logger)
{
    public virtual void Send(string to, string subject, string text) => logger.LogInformation("mail to={To} subject={Subject}\n{Text}", to, subject, text);
}

/// <summary>The current time; tests freeze it.</summary>
public class Clock(TimeZoneInfo zone)
{
    public DateTimeOffset? Frozen { get; set; }
    public DateTimeOffset Now => Frozen ?? DateTimeOffset.UtcNow;
    public DateTime Today => TimeZoneInfo.ConvertTime(Now, zone).Date;
}

/// <summary>Rails-like helpers: signed values, cookies, rate limit, passwords and reset tokens.</summary>
public class Web(byte[] secret, int bcryptCost, Clock clock, Repository repository)
{
    public const string SessionCookie = "session_id";
    public const string CsrfCookie = "_csrf_token";
    public const string FlashCookie = "_flash";
    public const string ReturnToCookie = "return_to";
    private const int Permanent = 20 * 365 * 24 * 60 * 60; // cookies.permanent

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset ResetAt)> rateLimits = new();
    private readonly string dummyDigest = BCrypt.Net.BCrypt.HashPassword("dummy-password", BCrypt.Net.BCrypt.GenerateSalt(bcryptCost, 'a'));

    public Clock Clock => clock;

    // Passwords: bcrypt in the "$2a$" format Rails writes, using only the first 72 bytes like Rails.

    private static string Truncate72(string raw)
    {
        var bytes = Encoding.UTF8.GetBytes(raw);
        return bytes.Length <= 72 ? raw : Encoding.UTF8.GetString(bytes, 0, 72);
    }

    public string HashPassword(string raw) => BCrypt.Net.BCrypt.HashPassword(Truncate72(raw), BCrypt.Net.BCrypt.GenerateSalt(bcryptCost, 'a'));

    public static bool CheckPassword(string digest, string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        try { return BCrypt.Net.BCrypt.Verify(Truncate72(raw), digest); }
        catch (Exception) { return false; }
    }

    /// <summary>Burns the same time as a real check, so a missing e-mail cannot be told apart by timing.</summary>
    public void CheckDummyPassword() => CheckPassword(dummyDigest, "x");

    // Signed values ("value--signature")

    private byte[] Hmac(string data) => HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(data));

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromB64(string text)
    {
        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
        }
        catch (FormatException) { return null; }
    }

    public string Sign(string purpose, string value) => B64(Encoding.UTF8.GetBytes(value)) + "--" + B64(Hmac(purpose + ":" + value));

    public string? Verify(string purpose, string? signed)
    {
        if (signed is null) return null;
        var separator = signed.IndexOf("--", StringComparison.Ordinal);
        if (separator < 0) return null;
        var bytes = FromB64(signed[..separator]);
        if (bytes is null) return null;
        var value = Encoding.UTF8.GetString(bytes);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(purpose, value)), Encoding.UTF8.GetBytes(signed)) ? value : null;
    }

    // Cookies

    public static void SetCookie(HttpResponse response, string name, string value, int? maxAge = null) =>
        response.Headers.Append("Set-Cookie", $"{name}={value}; Path=/{(maxAge is { } age ? $"; Max-Age={age}" : "")}; HttpOnly; SameSite=Lax");

    public static void DeleteCookie(HttpResponse response, string name) => SetCookie(response, name, "", 0);

    public string? ReadSigned(HttpRequest request, string name) => Verify(name, request.Cookies[name]);

    public void SetSigned(HttpResponse response, string name, string value, int? maxAge = null) => SetCookie(response, name, Sign(name, value), maxAge);

    public void StartSessionCookie(HttpResponse response, long sessionId) => SetSigned(response, SessionCookie, sessionId.ToString(), Permanent);

    /// <summary>Sets a flash message ("notice" or "alert") that survives one request, like Rails' flash.</summary>
    public void Flash(HttpResponse response, string kind, string message) => SetSigned(response, FlashCookie, kind + ":" + message);

    // Rate limit like Rails `rate_limit to: 10, within: 3.minutes`: a counter per action and IP.

    public void ClearRateLimits() => rateLimits.Clear();

    public bool Allow(string key, int to, TimeSpan within)
    {
        var now = clock.Now;
        var entry = rateLimits.AddOrUpdate(key, _ => (1, now + within), (_, current) => now > current.ResetAt ? (1, now + within) : (current.Count + 1, current.ResetAt));
        return entry.Count <= to;
    }

    // Password reset token: signed, expires in 15 minutes and stops working once the password changes.

    public string PasswordResetToken(User user)
    {
        var digest = user.PasswordDigest;
        var payload = $"{user.Id}:{digest[^10..]}:{clock.Now.AddMinutes(15).ToUnixTimeSeconds()}";
        return B64(Encoding.UTF8.GetBytes(payload)) + "." + B64(Hmac("password_reset:" + payload));
    }

    public async Task<User?> FindByPasswordResetToken(string token)
    {
        var separator = token.IndexOf('.');
        if (separator < 0) return null;
        var bytes = FromB64(token[..separator]);
        if (bytes is null) return null;
        var payload = Encoding.UTF8.GetString(bytes);
        var expected = Encoding.UTF8.GetBytes(B64(Hmac("password_reset:" + payload)));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(token[(separator + 1)..]))) return null;
        var parts = payload.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var id) || !long.TryParse(parts[2], out var expires)) return null;
        if (clock.Now.ToUnixTimeSeconds() > expires) return null;
        var user = await repository.FindUser(id);
        return user is not null && user.PasswordDigest[^10..] == parts[1] ? user : null;
    }
}
