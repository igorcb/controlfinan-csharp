using System.Security.Cryptography;
using ControlFinan.Models;
using ControlFinan.Web;
using Microsoft.AspNetCore.Mvc;

namespace ControlFinan.Controllers;

/// <summary>What every page needs: current user, flash messages, CSRF token and the active tab.</summary>
public class Page
{
    public string? Email { get; set; }
    public string Notice { get; set; } = "";
    public string Alert { get; set; } = "";
    public string Csrf { get; set; } = "";
    public string Nav { get; set; } = "";
    public List<string> Errors { get; set; } = [];
    public string MainClass => Email is null ? "mx-auto max-w-5xl px-4 py-6 " : "mx-auto max-w-5xl px-4 py-6 pb-28 sm:pb-6";
}

public abstract class AppController(Web.Web web, Repository repository) : Controller
{
    protected Web.Web Web => web;
    protected Repository Repository => repository;
    protected User CurrentUser => HttpContext.Current()!.User;
    protected string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";
    protected string Field(string name) => Request.HasFormContentType ? Request.Form[name].ToString() : "";
    protected string AbsoluteUrl(string path) => $"http://{Request.Host}{path}";

    /// <summary>Renders a view with the page data (flash is read once, the CSRF cookie is created when missing).</summary>
    protected IActionResult Page<T>(string view, T model, int status = 200) where T : Page
    {
        var flash = web.ReadSigned(Request, ControlFinan.Web.Web.FlashCookie);
        if (flash is not null)
        {
            ControlFinan.Web.Web.DeleteCookie(Response, ControlFinan.Web.Web.FlashCookie);
            var parts = flash.Split(':', 2);
            if (parts[0] == "notice") model.Notice = parts[1];
            if (parts[0] == "alert") model.Alert = parts[1];
        }
        var token = Request.Cookies[ControlFinan.Web.Web.CsrfCookie];
        if (string.IsNullOrEmpty(token))
        {
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            ControlFinan.Web.Web.SetCookie(Response, ControlFinan.Web.Web.CsrfCookie, token);
        }
        model.Csrf = token;
        model.Email = HttpContext.Current()?.User.EmailAddress;
        Response.StatusCode = status;
        return View(view, model);
    }

    /// <summary>Redirect with a flash message, like Rails' redirect_to ..., notice: ...</summary>
    protected IActionResult RedirectWith(string url, string kind, string message, int status = 302)
    {
        web.Flash(Response, kind, message);
        return RedirectStatus(url, status);
    }

    protected IActionResult RedirectStatus(string url, int status)
    {
        Response.Headers.Location = url;
        return StatusCode(status);
    }
}
