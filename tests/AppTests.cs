using System.Net;
using System.Text.RegularExpressions;
using ControlFinan.Models;
using ControlFinan.Web;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace ControlFinan.Tests;

/// <summary>Keeps the sent e-mails instead of logging them.</summary>
public class RecordingMailer() : Mailer(Microsoft.Extensions.Logging.Abstractions.NullLogger<Mailer>.Instance)
{
    public List<string> Sent { get; } = [];
    public override void Send(string to, string subject, string text) => Sent.Add($"{to}|{subject}|{text}");
}

/// <summary>The real app (in memory) against a real Postgres database, emptied before each test.</summary>
public class AppFactory : WebApplicationFactory<Program>
{
    public static readonly string Database = Environment.GetEnvironmentVariable("TEST_DATABASE_URL") ?? "Host=localhost;Database=controlfinan_csharp_test;Username=app;Password=app";

    static AppFactory()
    {
        var builder = new NpgsqlConnectionStringBuilder(Database);
        var name = builder.Database;
        builder.Database = "postgres";
        using var admin = new NpgsqlConnection(builder.ConnectionString);
        admin.Open();
        if (admin.ExecuteScalar<int>("SELECT count(*) FROM pg_database WHERE datname = @name", new { name }) == 0)
            admin.Execute($"CREATE DATABASE \"{name}\"");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DATABASE_URL", Database);
        builder.UseSetting("SECRET_KEY_BASE", "test-secret");
        Environment.SetEnvironmentVariable("BCRYPT_COST", "4");
        builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<Mailer, RecordingMailer>()));
    }
}

[CollectionDefinition("app", DisableParallelization = true)]
public class AppCollection : ICollectionFixture<AppFactory>;

public record Response(int Status, string Body, string Location);

[Collection("app")]
public abstract class AppTest : IAsyncLifetime
{
    protected readonly AppFactory Factory;
    protected readonly HttpClient Client;
    protected Web.Web Web => Factory.Services.GetRequiredService<Web.Web>();
    protected Repository Repository => Factory.Services.GetRequiredService<Repository>();
    protected RecordingMailer Mailer => (RecordingMailer)Factory.Services.GetRequiredService<Mailer>();
    protected static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    protected AppTest(AppFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
    }

    public async Task InitializeAsync()
    {
        Client.GetAsync("/up").Wait(); // boots the app (creates the tables)
        await using var c = new NpgsqlConnection(AppFactory.Database);
        await c.ExecuteAsync("TRUNCATE sessions, current_accounts, users RESTART IDENTITY CASCADE");
        Web.ClearRateLimits();
        Mailer.Sent.Clear();
        TravelTo(new DateTime(2026, 10, 27, 12, 0, 0));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected void TravelTo(DateTime local) => Web.Clock.Frozen = new DateTimeOffset(local, Zone.GetUtcOffset(local));

    protected static async Task<Response> Read(HttpResponseMessage response) =>
        new((int)response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers.Location?.ToString().Replace("http://localhost", "") ?? "");

    protected async Task<Response> Get(string path) => await Read(await Client.GetAsync(path));

    /// <summary>Sends a form like the browser does, with the CSRF token of the page.</summary>
    protected async Task<Response> Post(string path, params (string Key, string Value)[] form)
    {
        var page = (await Get("/passwords/new")).Body;
        var token = Regex.Match(page, "name=\"authenticity_token\" value=\"([^\"]+)\"").Groups[1].Value;
        var fields = form.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)).ToList();
        if (!fields.Any(f => f.Key == "authenticity_token")) fields.Add(new("authenticity_token", token));
        return await Read(await Client.PostAsync(path, new FormUrlEncodedContent(fields)));
    }

    protected async Task<User> CreateUser(string email, string password)
    {
        var user = new User { EmailAddress = email, PasswordDigest = Web.HashPassword(password) };
        await Repository.CreateUser(user);
        return user;
    }

    /// <summary>Creates a user and signs in through the real login form.</summary>
    protected async Task<User> SignIn()
    {
        var user = await CreateUser("driver@example.com", "secret123");
        Assert.Equal(302, (await Post("/session", ("email_address", "driver@example.com"), ("password", "secret123"))).Status);
        return user;
    }

    protected async Task<Entry> CreateEntry(User user, string date, int type, int cost, decimal price, string? historic = null)
    {
        var entry = new Entry { UserId = user.Id, CashAccountId = 5, CostId = cost, TypeLaunche = type, DateOcurrence = DateTime.Parse(date), Price = price, Historic = historic };
        await Repository.SaveEntry(entry);
        return entry;
    }

    protected Task<Entry> Credit(User user, string date, decimal price) => CreateEntry(user, date, Entry.Credit, Entry.Rides, price);

    protected static async Task<long> Count(string sql)
    {
        await using var c = new NpgsqlConnection(AppFactory.Database);
        return await c.ExecuteScalarAsync<long>(sql);
    }

    protected static void AssertContains(string body, params string[] texts)
    {
        foreach (var text in texts) Assert.True(body.Contains(text), $"expected body to contain: {text}");
    }

    protected static void AssertNotContains(string body, params string[] texts)
    {
        foreach (var text in texts) Assert.False(body.Contains(text), $"expected body not to contain: {text}");
    }

    protected static void AssertInOrder(string body, params string[] texts)
    {
        var from = 0;
        foreach (var text in texts)
        {
            var index = body.IndexOf(text, from, StringComparison.Ordinal);
            Assert.True(index >= 0, $"expected in order: {text}");
            from = index + text.Length;
        }
    }

    protected static (string, string)[] ValidForm(params (string, string)[] overrides)
    {
        var form = new Dictionary<string, string>
        {
            ["current_account[type_launche]"] = "credito", ["current_account[price]"] = "150", ["current_account[date_ocurrence]"] = "2026-10-27",
            ["current_account[cost_id]"] = "6", ["current_account[cash_account_id]"] = "5", ["current_account[historic]"] = "Corridas do dia",
        };
        foreach (var (key, value) in overrides) form[key] = value;
        return form.Select(f => (f.Key, f.Value)).ToArray();
    }
}

public class FormatTests
{
    [Fact]
    public void CurrencyFormatsLikeRailsInPortuguese()
    {
        Assert.Equal("R$ 1.234,50", Format.Currency(1234.5m));
        Assert.Equal("-R$ 12,50", Format.Currency(-12.5m));
        Assert.Equal("R$ 0,00", Format.Currency(-0.00m));
        Assert.Equal("R$ 1.234.567,89", Format.Currency(1234567.89m));
    }

    [Fact]
    public void DecimalTextPrintsLikeRubyBigDecimal()
    {
        Assert.Equal("150.0", Format.DecimalText(150.00m));
        Assert.Equal("12.5", Format.DecimalText(12.50m));
        Assert.Equal("0.05", Format.DecimalText(0.05m));
        Assert.Equal("0.0", Format.DecimalText(0.00m));
    }
}

public class ModelTests(AppFactory factory) : AppTest(factory)
{
    [Fact]
    public void PasswordUsesTheRailsBcryptFormatAndAcceptsARailsDigest()
    {
        var digest = Web.HashPassword("secret123");
        Assert.StartsWith("$2a$", digest);
        Assert.True(ControlFinan.Web.Web.CheckPassword(digest, "secret123"));
        Assert.False(ControlFinan.Web.Web.CheckPassword(digest, "wrong"));
        // Digest of "benchmark123" written by the Rails app (has_secure_password, bcrypt-ruby) for its benchmark account.
        const string rails = "$2a$12$xUBQqXOC4yO3ZUCU3U./FeJ/CU4pqmbNxFQ5iUUrWzLWmKSpUKChe";
        Assert.True(ControlFinan.Web.Web.CheckPassword(rails, "benchmark123"));
        Assert.False(ControlFinan.Web.Web.CheckPassword(rails, "wrong"));
    }

    [Fact]
    public async Task ValidateUserReturnsTheRailsMessages()
    {
        await CreateUser("taken@example.com", "secret123");
        Assert.Equal(["Senha não pode ficar em branco", "E-mail não pode ficar em branco"], await Repository.ValidateUser(new User(), "", "", true));
        Assert.Equal(["Confirmação da senha não é igual a Senha", "E-mail não é válido", "Senha é muito curto (mínimo: 6 caracteres)"],
            await Repository.ValidateUser(new User { EmailAddress = "bad" }, "123", "456", true));
        Assert.Equal(["E-mail já está em uso"], await Repository.ValidateUser(new User { EmailAddress = " TAKEN@example.com " }, "secret123", "secret123", true));
    }

    [Fact]
    public void EntryValidateReturnsTheRailsMessages()
    {
        Assert.Equal(["Tipo não está incluído na lista", "Conta não está incluído na lista", "Categoria não está incluído na lista",
            "Data não pode ficar em branco", "Valor não pode ficar em branco", "Valor não é um número"], new Entry().Validate());
        var zero = new Entry { CashAccountId = 0, CostId = 0, TypeLaunche = Entry.Debit, DateOcurrence = DateTime.Today, Price = 0 };
        Assert.Equal(["Valor deve ser maior que 0"], zero.Validate());
        var other = new Entry { CashAccountId = 1, CostId = 99, TypeLaunche = Entry.Debit, Price = 10 };
        Assert.Equal(("Itaú", "Não definido", "Débito", "-R$ 10,00"), (other.NameAccount, other.NameCost, other.TypeName, other.SignedCurrency));
    }

    [Fact]
    public async Task BalanceAndMonthSummaryFollowTheRailsRules()
    {
        var user = await CreateUser("driver@example.com", "secret123");
        await Credit(user, "2026-10-02", 200);
        await CreateEntry(user, "2026-10-02", Entry.Debit, 0, 50);
        await CreateEntry(user, "2026-10-05", Entry.Debit, 8, 20);
        await Credit(user, "2026-09-30", 999);
        Assert.Equal(1129m, await Repository.Balance(user.Id, null));
        var (daily, monthBalance, monthly, net) = await Repository.SummarizeMonth(user.Id, new DateTime(2026, 10, 1), new DateTime(2026, 10, 31));
        Assert.Equal(["05/10/2026 -R$ 20,00", "02/10/2026 R$ 150,00"], daily.Select(r => $"{r.Label} {r.Currency}"));
        Assert.Equal(["Corridas R$ 200,00", "Combustível -R$ 50,00", "Lavagem -R$ 20,00", "Telefone R$ 0,00"], monthly.Select(r => $"{r.Label} {r.Currency}"));
        Assert.Equal((130m, 130m), (monthBalance, net));
    }
}

public class RequestTests(AppFactory factory) : AppTest(factory)
{
    [Fact]
    public async Task GuestIsRedirectedToLoginAndBackAfterSigningIn()
    {
        AssertContains((await Get("/session/new")).Body, "Esqueceu a senha?");
        await CreateUser("driver@example.com", "secret123");
        Assert.Equal("/session/new", (await Get("/dashboard")).Location);
        var response = await Post("/session", ("email_address", " Driver@Example.com "), ("password", "secret123"));
        Assert.Equal("/dashboard", response.Location);
        Assert.Equal(1, await Count("SELECT count(*) FROM sessions"));
        Assert.Equal(200, (await Get("/dashboard")).Status);
    }

    [Fact]
    public async Task WrongPasswordIsRejected()
    {
        await CreateUser("driver@example.com", "secret123");
        Assert.Equal("/session/new", (await Post("/session", ("email_address", "driver@example.com"), ("password", "wrong"))).Location);
        AssertContains((await Get("/session/new")).Body, "E-mail ou senha inválidos.");
    }

    [Fact]
    public async Task LoginAttemptsAreLimitedToTenEveryThreeMinutes()
    {
        for (var i = 0; i < 10; i++) await Post("/session", ("email_address", "x@example.com"), ("password", "x"));
        await Post("/session", ("email_address", "x@example.com"), ("password", "x"));
        AssertContains((await Get("/session/new")).Body, "Tente novamente mais tarde.");
        Web.Clock.Frozen = Web.Clock.Now.AddMinutes(4);
        await Post("/session", ("email_address", "x@example.com"), ("password", "x"));
        AssertContains((await Get("/session/new")).Body, "E-mail ou senha inválidos.");
    }

    [Fact]
    public async Task SignOutDeletesTheSession()
    {
        await SignIn();
        var response = await Post("/session", ("_method", "delete"));
        Assert.Equal((303, "/session/new"), (response.Status, response.Location));
        Assert.Equal(0, await Count("SELECT count(*) FROM sessions"));
        Assert.Equal("/session/new", (await Get("/")).Location);
    }

    [Fact]
    public async Task FormsWithoutAValidCsrfTokenAreRejected()
    {
        await CreateUser("driver@example.com", "secret123");
        var response = await Post("/session", ("authenticity_token", "forged"), ("email_address", "driver@example.com"), ("password", "secret123"));
        Assert.Equal(422, response.Status);
    }

    [Fact]
    public async Task RegistrationCreatesAnAccountOrShowsTheValidationMessages()
    {
        var invalid = await Post("/registration", ("user[email_address]", "bad"), ("user[password]", "1"), ("user[password_confirmation]", "2"));
        Assert.Equal(422, invalid.Status);
        AssertContains(invalid.Body, "Confirmação da senha não é igual a Senha", "E-mail não é válido");
        var response = await Post("/registration", ("user[email_address]", " New@Example.com "), ("user[password]", "secret123"), ("user[password_confirmation]", "secret123"));
        Assert.Equal("/", response.Location);
        Assert.Equal(1, await Count("SELECT count(*) FROM users WHERE email_address = 'new@example.com'"));
        AssertContains((await Get("/")).Body, "Conta criada.");
    }

    [Fact]
    public async Task PasswordResetEmailDoesNotRevealWhetherTheAddressExists()
    {
        await CreateUser("driver@example.com", "secret123");
        const string notice = "Se o e-mail estiver cadastrado, enviamos as instruções para redefinir a senha.";
        Assert.Equal("/session/new", (await Post("/passwords", ("email_address", "driver@example.com"))).Location);
        AssertContains((await Get("/session/new")).Body, notice);
        await Post("/passwords", ("email_address", "missing@example.com"));
        AssertContains((await Get("/session/new")).Body, notice);
        Assert.Single(Mailer.Sent);
        AssertContains(Mailer.Sent[0], "driver@example.com|Redefinição de senha|", "/passwords/", "/edit", "15 minutos");
    }

    [Fact]
    public async Task PasswordResetChangesThePasswordAndEndsEverySession()
    {
        var user = await SignIn();
        var token = Web.PasswordResetToken(user);
        AssertContains((await Get($"/passwords/{token}/edit")).Body, "Defina sua nova senha");
        var mismatch = await Post($"/passwords/{token}", ("_method", "put"), ("password", "new-secret"), ("password_confirmation", "other"));
        Assert.Equal($"/passwords/{token}/edit", mismatch.Location);
        AssertContains((await Get(mismatch.Location)).Body, "As senhas não conferem ou têm menos de 6 caracteres.");
        var response = await Post($"/passwords/{token}", ("_method", "put"), ("password", "new-secret"), ("password_confirmation", "new-secret"));
        Assert.Equal("/session/new", response.Location);
        AssertContains((await Get("/session/new")).Body, "Senha redefinida.");
        Assert.Equal(0, await Count("SELECT count(*) FROM sessions"));
        Assert.True(ControlFinan.Web.Web.CheckPassword((await Repository.FindUser(user.Id))!.PasswordDigest, "new-secret"));
    }

    [Fact]
    public async Task PasswordResetTokenExpiresAfterFifteenMinutesAndAfterAPasswordChange()
    {
        var user = await CreateUser("driver@example.com", "secret123");
        var token = Web.PasswordResetToken(user);
        var start = Web.Clock.Now;
        Web.Clock.Frozen = start.AddMinutes(16);
        Assert.Equal("/passwords/new", (await Get($"/passwords/{token}/edit")).Location);
        AssertContains((await Get("/passwords/new")).Body, "O link de redefinição é inválido ou expirou.");
        Web.Clock.Frozen = start;
        user.PasswordDigest = Web.HashPassword("another123");
        await Repository.UpdatePassword(user);
        Assert.Equal("/passwords/new", (await Get($"/passwords/{token}/edit")).Location);
        Assert.Equal("/passwords/new", (await Get("/passwords/invalid/edit")).Location);
    }

    [Fact]
    public async Task ListShowsEntriesTheBalanceAndFiltersByDay()
    {
        var user = await SignIn();
        await Credit(user, "2026-10-26", 150);
        await CreateEntry(user, "2026-10-27", Entry.Debit, 0, 40);
        AssertContains((await Get("/")).Body, "Saldo total", "R$ 110,00", "-R$ 40,00");
        var body = (await Get("/current_accounts?date=2026-10-26")).Body;
        AssertContains(body, "Saldo do dia 26/10/2026", "R$ 150,00");
        AssertNotContains(body, "R$ 40,00");
    }

    [Fact]
    public async Task ListPaginatesThirtyEntriesPerPage()
    {
        var user = await SignIn();
        for (var n = 1; n <= 31; n++) await CreateEntry(user, "2026-10-27", Entry.Credit, Entry.Rides, 150, $"entry {n}.");
        var first = (await Get("/current_accounts")).Body;
        AssertContains(first, "Mais antigos →");
        AssertNotContains(first, "← Mais recentes", "entry 1.");
        var second = (await Get("/current_accounts?page=2")).Body;
        AssertContains(second, "← Mais recentes", "entry 1.");
        AssertNotContains(second, "Mais antigos →");
    }

    [Fact]
    public async Task JsonHasTheJbuilderFields()
    {
        var user = await SignIn();
        var entry = await CreateEntry(user, "2026-10-27", Entry.Credit, Entry.Rides, 150.00m, "Café/Almoço");
        var expected = $"{{\"id\":{entry.Id},\"cash_account_id\":5,\"cost_id\":6,\"date_ocurrence\":\"2026-10-27\",\"price\":\"150.0\",\"historic\":\"Café/Almoço\",\"user_id\":{user.Id},\"type_launche\":1,\"url\":\"http://localhost/current_accounts/{entry.Id}.json\"}}";
        Assert.Equal($"[{expected}]", (await Get("/current_accounts.json")).Body);
        Assert.Equal(expected, (await Get($"/current_accounts/{entry.Id}.json")).Body);
    }

    [Fact]
    public async Task OtherUsersEntriesAreNotFound()
    {
        await SignIn();
        var other = await Credit(await CreateUser("other@example.com", "secret123"), "2026-10-27", 150);
        Assert.Equal(404, (await Get($"/current_accounts/{other.Id}")).Status);
        Assert.Equal(404, (await Get($"/current_accounts/{other.Id}/edit")).Status);
        Assert.Equal(404, (await Post($"/current_accounts/{other.Id}", ("_method", "delete"))).Status);
        Assert.Equal(1, await Count("SELECT count(*) FROM current_accounts"));
    }

    [Fact]
    public async Task CreateUpdateAndDeleteEntries()
    {
        var user = await SignIn();
        var other = await CreateUser("other@example.com", "secret123");
        Assert.Equal("/current_accounts", (await Post("/current_accounts", ValidForm(("current_account[user_id]", other.Id.ToString())))).Location);
        AssertContains((await Get("/current_accounts")).Body, "Lançamento cadastrado.");
        Assert.Equal(1, await Count($"SELECT count(*) FROM current_accounts WHERE user_id = {user.Id} AND price = 150 AND type_launche = 1"));

        var invalid = await Post("/current_accounts", ("current_account[price]", ""));
        Assert.Equal(422, invalid.Status);
        AssertContains(invalid.Body, "Valor não pode ficar em branco", "Tipo não está incluído na lista");

        var id = await Count("SELECT max(id) FROM current_accounts");
        var updated = await Post($"/current_accounts/{id}", ValidForm(("_method", "patch"), ("current_account[price]", "12.5")));
        Assert.Equal($"/current_accounts/{id}", updated.Location);
        AssertContains((await Get(updated.Location)).Body, "Lançamento atualizado.", "R$ 12,50");

        var deleted = await Post($"/current_accounts/{id}", ("_method", "delete"));
        Assert.Equal(303, deleted.Status);
        AssertContains((await Get("/current_accounts")).Body, "Lançamento removido.");
        Assert.Equal(0, await Count("SELECT count(*) FROM current_accounts"));
    }

    [Fact]
    public async Task UpdateKeepsTheStoredValueOfFieldsMissingFromTheForm()
    {
        var user = await SignIn();
        var entry = await CreateEntry(user, "2026-10-20", Entry.Debit, 7, 40m, "Almoço");

        var response = await Post($"/current_accounts/{entry.Id}", ("_method", "patch"), ("current_account[price]", "55.5"),
            ("current_account[type_launche]", "debito"), ("current_account[date_ocurrence]", "2026-10-21"), ("current_account[cash_account_id]", "5"));
        Assert.Equal($"/current_accounts/{entry.Id}", response.Location);
        Assert.Equal(1, await Count($"SELECT count(*) FROM current_accounts WHERE id = {entry.Id} AND historic = 'Almoço' AND cost_id = 7 AND price = 55.5 AND date_ocurrence = '2026-10-21'"));
    }

    [Fact]
    public async Task UpdateWithOnlyThePriceKeepsEveryOtherField()
    {
        var user = await SignIn();
        var entry = await CreateEntry(user, "2026-10-20", Entry.Credit, 6, 100m, "Corridas");

        var response = await Post($"/current_accounts/{entry.Id}", ("_method", "patch"), ("current_account[price]", "120"));
        Assert.Equal($"/current_accounts/{entry.Id}", response.Location);
        Assert.Equal(1, await Count($"SELECT count(*) FROM current_accounts WHERE id = {entry.Id} AND price = 120 AND historic = 'Corridas' AND cost_id = 6 AND cash_account_id = 5 AND type_launche = {Entry.Credit} AND date_ocurrence = '2026-10-20'"));
    }

    [Fact]
    public async Task UpdateWithAnEmptyRequiredFieldIsStillRejected()
    {
        var user = await SignIn();
        var entry = await CreateEntry(user, "2026-10-20", Entry.Credit, 6, 100m, "Corridas");

        var response = await Post($"/current_accounts/{entry.Id}", ("_method", "patch"), ("current_account[price]", ""));
        Assert.Equal(422, response.Status);
        AssertContains(response.Body, "Valor não pode ficar em branco");
        Assert.Equal(1, await Count($"SELECT count(*) FROM current_accounts WHERE id = {entry.Id} AND price = 100"));
    }

    [Fact]
    public async Task LegacyDailySearchRedirectsToTheFilter()
    {
        var response = await Get("/search_account?date_calendar_now=2026-10-27");
        Assert.Equal((301, "/current_accounts?date=2026-10-27"), (response.Status, response.Location));
    }

    [Fact]
    public async Task DashboardShowsTheBalancesNavigatesAndFallsBackToTheCurrentMonth()
    {
        var user = await SignIn();
        await Credit(user, "2026-10-02", 200);
        await CreateEntry(user, "2026-10-02", Entry.Debit, 0, 50);
        await CreateEntry(user, "2026-10-05", Entry.Debit, 8, 20);
        await Credit(user, "2026-09-30", 999);
        var body = (await Get("/dashboard?month=2026-10")).Body;
        AssertContains(body, "Outubro de 2026", "Saldo Diário", "Saldo Mensal");
        AssertInOrder(body, "05/10/2026", "-R$ 20,00", "02/10/2026", "R$ 150,00", "Total do mês", "R$ 130,00");
        AssertInOrder(body, "Corridas", "R$ 200,00", "Combustível", "-R$ 50,00", "Lavagem", "-R$ 20,00", "Telefone", "R$ 0,00", "Saldo do mês", "R$ 130,00");
        AssertNotContains(body, "999");
        AssertContains((await Get("/dashboard?month=2026-01")).Body, "/dashboard?month=2025-12", "/dashboard?month=2026-02");
        TravelTo(new DateTime(2026, 3, 15, 12, 0, 0));
        AssertContains((await Get("/dashboard?month=bad")).Body, "Março de 2026");
    }
}
