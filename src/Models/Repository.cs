using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace ControlFinan.Models;

/// <summary>SQL for users, sessions and entries (same schema as the Rails app).</summary>
public partial class Repository(NpgsqlDataSource db)
{
    // Same regexp as Ruby's URI::MailTo::EMAIL_REGEXP.
    [GeneratedRegex(@"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$")]
    private static partial Regex EmailRegex();

    private const string EntryColumns =
        "id AS Id, user_id AS UserId, cash_account_id AS CashAccountId, cost_id AS CostId, type_launche AS TypeLaunche, " +
        "date_ocurrence AS DateOcurrence, price AS Price, historic AS Historic";
    private const string UserColumns = "u.id AS Id, u.email_address AS EmailAddress, u.password_digest AS PasswordDigest";

    public static async Task Migrate(NpgsqlDataSource db)
    {
        using var stream = typeof(Repository).Assembly.GetManifestResourceStream("ControlFinan.schema.sql")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var connection = await db.OpenConnectionAsync();
        await connection.ExecuteAsync(await reader.ReadToEndAsync());
    }

    private async Task<NpgsqlConnection> Open() => await db.OpenConnectionAsync();

    // Users

    public async Task<User?> FindUserByEmail(string? email)
    {
        await using var c = await Open();
        return await c.QuerySingleOrDefaultAsync<User>($"SELECT {UserColumns} FROM users u WHERE u.email_address = @email",
            new { email = User.NormalizeEmail(email) });
    }

    public async Task<User?> FindUser(long id)
    {
        await using var c = await Open();
        return await c.QuerySingleOrDefaultAsync<User>($"SELECT {UserColumns} FROM users u WHERE u.id = @id", new { id });
    }

    public async Task<User?> FindUserBySession(long sessionId)
    {
        await using var c = await Open();
        return await c.QuerySingleOrDefaultAsync<User>(
            $"SELECT {UserColumns} FROM sessions s JOIN users u ON u.id = s.user_id WHERE s.id = @sessionId", new { sessionId });
    }

    public async Task CreateUser(User user)
    {
        await using var c = await Open();
        user.Id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO users (email_address, password_digest, created_at, updated_at) VALUES (@EmailAddress, @PasswordDigest, now(), now()) RETURNING id",
            user);
    }

    public async Task UpdatePassword(User user)
    {
        await using var c = await Open();
        await c.ExecuteAsync("UPDATE users SET password_digest = @PasswordDigest, updated_at = now() WHERE id = @Id", user);
    }

    /// <summary>The Rails messages (pt-BR) in the same order.</summary>
    public async Task<List<string>> ValidateUser(User user, string password, string confirmation, bool passwordRequired)
    {
        var errors = new List<string>();
        if (passwordRequired && password.Length == 0) errors.Add("Senha não pode ficar em branco");
        if (password != confirmation) errors.Add("Confirmação da senha não é igual a Senha");
        user.EmailAddress = User.NormalizeEmail(user.EmailAddress);
        if (user.EmailAddress.Length == 0) errors.Add("E-mail não pode ficar em branco");
        else if (!EmailRegex().IsMatch(user.EmailAddress)) errors.Add("E-mail não é válido");
        else
        {
            await using var c = await Open();
            if (await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM users WHERE email_address = @EmailAddress AND id <> @Id)", user))
                errors.Add("E-mail já está em uso");
        }
        if (password.Length > 0 && password.EnumerateRunes().Count() < 6) errors.Add("Senha é muito curto (mínimo: 6 caracteres)");
        return errors;
    }

    // Sessions

    public async Task<long> CreateSession(long userId, string userAgent, string ip)
    {
        await using var c = await Open();
        return await c.ExecuteScalarAsync<long>(
            "INSERT INTO sessions (user_id, user_agent, ip_address, created_at, updated_at) VALUES (@userId, @userAgent, @ip, now(), now()) RETURNING id",
            new { userId, userAgent, ip });
    }

    public async Task DeleteSession(long id)
    {
        await using var c = await Open();
        await c.ExecuteAsync("DELETE FROM sessions WHERE id = @id", new { id });
    }

    public async Task DeleteSessionsOf(long userId)
    {
        await using var c = await Open();
        await c.ExecuteAsync("DELETE FROM sessions WHERE user_id = @userId", new { userId });
    }

    // Entries

    /// <summary>Up to <paramref name="limit"/> entries, most recent first, optionally on one day.</summary>
    public async Task<List<Entry>> ListEntries(long userId, DateTime? day, int limit, int offset)
    {
        await using var c = await Open();
        var filter = day is null ? "" : " AND date_ocurrence = @day";
        var rows = await c.QueryAsync<Entry>(
            $"SELECT {EntryColumns} FROM current_accounts WHERE user_id = @userId{filter} ORDER BY date_ocurrence DESC, id DESC LIMIT @limit OFFSET @offset",
            new { userId, day, limit, offset });
        return rows.ToList();
    }

    /// <summary>Credits minus debits: the sign comes from type_launche (-1 or 1).</summary>
    public async Task<decimal> Balance(long userId, DateTime? day)
    {
        await using var c = await Open();
        var filter = day is null ? "" : " AND date_ocurrence = @day";
        return await c.ExecuteScalarAsync<decimal>(
            $"SELECT COALESCE(SUM(price * type_launche), 0) FROM current_accounts WHERE user_id = @userId{filter}", new { userId, day });
    }

    public async Task<Entry?> FindEntry(long userId, long id)
    {
        await using var c = await Open();
        return await c.QuerySingleOrDefaultAsync<Entry>(
            $"SELECT {EntryColumns} FROM current_accounts WHERE user_id = @userId AND id = @id", new { userId, id });
    }

    public async Task SaveEntry(Entry entry)
    {
        await using var c = await Open();
        if (entry.Persisted)
        {
            await c.ExecuteAsync(
                "UPDATE current_accounts SET cash_account_id = @CashAccountId, cost_id = @CostId, type_launche = @TypeLaunche, " +
                "date_ocurrence = @DateOcurrence, price = @Price, historic = @Historic, updated_at = now() WHERE id = @Id AND user_id = @UserId",
                entry);
            return;
        }
        entry.Id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO current_accounts (user_id, cash_account_id, cost_id, type_launche, date_ocurrence, price, historic, created_at, updated_at) " +
            "VALUES (@UserId, @CashAccountId, @CostId, @TypeLaunche, @DateOcurrence, @Price, @Historic, now(), now()) RETURNING id", entry);
    }

    public async Task DeleteEntry(Entry entry)
    {
        await using var c = await Open();
        await c.ExecuteAsync("DELETE FROM current_accounts WHERE id = @Id AND user_id = @UserId", entry);
    }

    /// <summary>The dashboard numbers of one month.</summary>
    public async Task<(List<Row> Daily, decimal MonthBalance, List<Row> Monthly, decimal RidesNet)> SummarizeMonth(long userId, DateTime first, DateTime last)
    {
        await using var c = await Open();
        var days = await c.QueryAsync<(DateTime Day, decimal Total)>(
            "SELECT date_ocurrence, SUM(price * type_launche) FROM current_accounts WHERE user_id = @userId " +
            "AND date_ocurrence BETWEEN @first AND @last GROUP BY date_ocurrence ORDER BY date_ocurrence DESC", new { userId, first, last });
        var daily = days.Select(d => new Row(Format.BrDate(d.Day), d.Total)).ToList();

        var ids = Entry.RideExpenses.Select(o => o.Id).ToArray();
        var expenses = (await c.QueryAsync<(int CostId, decimal Total)>(
            "SELECT cost_id, SUM(price) FROM current_accounts WHERE user_id = @userId AND date_ocurrence BETWEEN @first AND @last " +
            "AND cost_id = ANY(@ids) GROUP BY cost_id", new { userId, first, last, ids })).ToDictionary(e => e.CostId, e => e.Total);
        var rides = await c.ExecuteScalarAsync<decimal>(
            "SELECT COALESCE(SUM(price * type_launche), 0) FROM current_accounts WHERE user_id = @userId " +
            "AND date_ocurrence BETWEEN @first AND @last AND cost_id = @rides", new { userId, first, last, rides = Entry.Rides });

        var monthly = new List<Row> { new("Corridas", rides) };
        var net = rides;
        foreach (var expense in Entry.RideExpenses)
        {
            var amount = expenses.GetValueOrDefault(expense.Id);
            monthly.Add(new Row(expense.Name, -amount));
            net -= amount;
        }
        return (daily, daily.Sum(d => d.Amount), monthly, net);
    }
}
