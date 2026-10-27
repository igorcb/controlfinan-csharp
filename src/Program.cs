using System.Text.Encodings.Web;
using System.Text.Unicode;
using ControlFinan.Models;
using ControlFinan.Web;
using Microsoft.Extensions.WebEncoders;
using Npgsql;

// ControlFinan in C#: same screens, rules and database as the Rails 8 + Hotwire app.
var builder = WebApplication.CreateBuilder(args);
string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

var secret = builder.Configuration["SECRET_KEY_BASE"] ?? throw new InvalidOperationException("SECRET_KEY_BASE is required");
var connection = new NpgsqlConnectionStringBuilder(builder.Configuration["DATABASE_URL"] ?? "Host=localhost;Database=controlfinan_csharp;Username=app;Password=app")
{
    MaxPoolSize = int.Parse(Env("DB_POOL", "10")),
};
var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

builder.Services.AddSingleton(dataSource);
builder.Services.AddSingleton<Repository>();
builder.Services.AddSingleton(new Clock(TimeZoneInfo.FindSystemTimeZoneById(Env("APP_TIMEZONE", "America/Sao_Paulo"))));
builder.Services.AddSingleton<Mailer>();
builder.Services.AddSingleton(sp => new Web(System.Text.Encoding.UTF8.GetBytes(secret), int.Parse(Env("BCRYPT_COST", "12")),
    sp.GetRequiredService<Clock>(), sp.GetRequiredService<Repository>()));
builder.Services.AddControllersWithViews();
// Keep accents as they are in the HTML (Razor would write "&#xE7;" for "ç").
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.WebHost.UseUrls($"http://0.0.0.0:{Env("PORT", "8000")}");

var app = builder.Build();
await Repository.Migrate(dataSource);

app.UseStaticFiles();
// The pipeline runs before routing, so "_method" can change the method before a route is picked.
app.UseMiddleware<RequestPipeline>();
app.UseRouting();
app.MapGet("/up", () => Results.Content("<!DOCTYPE html><html><body style=\"background-color: green\"></body></html>", "text/html"));
app.MapControllers();
app.Run();

public partial class Program;
