using ControlFinan.Models;
using ControlFinan.Web;
using Microsoft.AspNetCore.Mvc;

namespace ControlFinan.Controllers;

public class EmailPage : Page
{
    public string EmailAddress { get; set; } = "";
}

public class TokenPage : Page
{
    public string Token { get; set; } = "";
}

public class SessionsController(Web.Web web, Repository repository) : AppController(web, repository)
{
    [HttpGet("/session/new")]
    public IActionResult New([FromQuery(Name = "email_address")] string? email) =>
        Page("~/Views/Sessions/New.cshtml", new EmailPage { EmailAddress = email ?? "" });

    [HttpPost("/session")]
    public async Task<IActionResult> Create()
    {
        if (!Web.Allow("sessions:" + Ip, 10, TimeSpan.FromMinutes(3)))
            return RedirectWith("/session/new", "alert", "Tente novamente mais tarde.");
        var user = await Repository.FindUserByEmail(Field("email_address"));
        if (user is null) Web.CheckDummyPassword();
        if (user is null || !ControlFinan.Web.Web.CheckPassword(user.PasswordDigest, Field("password")))
            return RedirectWith("/session/new", "alert", "E-mail ou senha inválidos.");
        var sessionId = await Repository.CreateSession(user.Id, UserAgent(), Ip);
        Web.StartSessionCookie(Response, sessionId);
        var returnTo = Web.ReadSigned(Request, ControlFinan.Web.Web.ReturnToCookie);
        if (returnTo is not null) ControlFinan.Web.Web.DeleteCookie(Response, ControlFinan.Web.Web.ReturnToCookie);
        return RedirectStatus(returnTo ?? AbsoluteUrl("/"), 302);
    }

    [HttpDelete("/session")]
    public async Task<IActionResult> Destroy()
    {
        if (HttpContext.Current() is { } current) await Repository.DeleteSession(current.SessionId);
        ControlFinan.Web.Web.DeleteCookie(Response, ControlFinan.Web.Web.SessionCookie);
        return RedirectStatus("/session/new", 303);
    }

    private string UserAgent()
    {
        var agent = Request.Headers.UserAgent.ToString();
        return agent.Length > 255 ? agent[..255] : agent;
    }
}

public class RegistrationsController(Web.Web web, Repository repository) : AppController(web, repository)
{
    [HttpGet("/registration/new")]
    public IActionResult New() => Page("~/Views/Registrations/New.cshtml", new EmailPage());

    [HttpPost("/registration")]
    public async Task<IActionResult> Create()
    {
        if (!Web.Allow("registrations:" + Ip, 10, TimeSpan.FromMinutes(3)))
            return RedirectWith("/registration/new", "alert", "Tente novamente mais tarde.");
        var password = Field("user[password]");
        var user = new User { EmailAddress = Field("user[email_address]") };
        var errors = await Repository.ValidateUser(user, password, Field("user[password_confirmation]"), true);
        if (errors.Count > 0)
            return Page("~/Views/Registrations/New.cshtml", new EmailPage { EmailAddress = user.EmailAddress, Errors = errors }, 422);
        user.PasswordDigest = Web.HashPassword(password);
        await Repository.CreateUser(user);
        var agent = Request.Headers.UserAgent.ToString();
        Web.StartSessionCookie(Response, await Repository.CreateSession(user.Id, agent.Length > 255 ? agent[..255] : agent, Ip));
        return RedirectWith("/", "notice", "Conta criada.");
    }
}

public class PasswordsController(Web.Web web, Repository repository, Web.Mailer mailer) : AppController(web, repository)
{
    private const string InvalidToken = "O link de redefinição é inválido ou expirou.";

    [HttpGet("/passwords/new")]
    public IActionResult New([FromQuery(Name = "email_address")] string? email) =>
        Page("~/Views/Passwords/New.cshtml", new EmailPage { EmailAddress = email ?? "" });

    [HttpPost("/passwords")]
    public async Task<IActionResult> Create()
    {
        if (!Web.Allow("passwords:" + Ip, 10, TimeSpan.FromMinutes(3)))
            return RedirectWith("/passwords/new", "alert", "Tente novamente mais tarde.");
        if (await Repository.FindUserByEmail(Field("email_address")) is { } user)
        {
            var url = AbsoluteUrl($"/passwords/{Web.PasswordResetToken(user)}/edit");
            mailer.Send(user.EmailAddress, "Redefinição de senha", $"Você pode redefinir sua senha em\n{url}\n\nEste link expira em 15 minutos.\n");
        }
        return RedirectWith("/session/new", "notice", "Se o e-mail estiver cadastrado, enviamos as instruções para redefinir a senha.");
    }

    [HttpGet("/passwords/{token}/edit")]
    public async Task<IActionResult> Edit(string token)
    {
        if (await Web.FindByPasswordResetToken(token) is null) return RedirectWith("/passwords/new", "alert", InvalidToken);
        return Page("~/Views/Passwords/Edit.cshtml", new TokenPage { Token = token });
    }

    [HttpPut("/passwords/{token}"), HttpPatch("/passwords/{token}")]
    public async Task<IActionResult> Update(string token)
    {
        var user = await Web.FindByPasswordResetToken(token);
        if (user is null) return RedirectWith("/passwords/new", "alert", InvalidToken);
        var password = Field("password");
        if ((await Repository.ValidateUser(user, password, Field("password_confirmation"), true)).Count > 0)
            return RedirectWith($"/passwords/{token}/edit", "alert", "As senhas não conferem ou têm menos de 6 caracteres.");
        user.PasswordDigest = Web.HashPassword(password);
        await Repository.UpdatePassword(user);
        await Repository.DeleteSessionsOf(user.Id);
        return RedirectWith("/session/new", "notice", "Senha redefinida.");
    }
}
