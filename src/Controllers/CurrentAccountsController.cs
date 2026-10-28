using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ControlFinan.Models;
using Microsoft.AspNetCore.Mvc;

namespace ControlFinan.Controllers;

public class IndexPage : Page
{
    public List<Entry> Entries { get; set; } = [];
    public string BalanceText { get; set; } = "";
    public string BalanceClass { get; set; } = "";
    public string DateLabel { get; set; } = "";
    public string DateValue { get; set; } = "";
    public string DateParam { get; set; } = "";
    public int PageNumber { get; set; }
    public bool NextPage { get; set; }
}

public class EntryPage : Page
{
    public Entry Entry { get; set; } = new();
}

public partial class CurrentAccountsController(Web.Web web, Repository repository) : AppController(web, repository)
{
    private const int PerPage = 30;
    // Compact JSON like jbuilder: no escaping of accents or "/".
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DateRegex();

    public static DateTime? ParseDate(string? value) =>
        value is not null && DateRegex().IsMatch(value) && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    private static int ParsePage(string? value) => int.TryParse(value, out var page) ? Math.Max(page, 1) : 1;

    [HttpGet("/"), HttpGet("/current_accounts")]
    public async Task<IActionResult> Index([FromQuery] string? date, [FromQuery] string? page)
    {
        var day = ParseDate(date);
        var pageNumber = ParsePage(page);
        var entries = await Repository.ListEntries(CurrentUser.Id, day, PerPage + 1, (pageNumber - 1) * PerPage);
        var balance = await Repository.Balance(CurrentUser.Id, day);
        return Page("~/Views/CurrentAccounts/Index.cshtml", new IndexPage
        {
            Nav = "index",
            Entries = entries.Take(PerPage).ToList(),
            NextPage = entries.Count > PerPage,
            BalanceText = Format.Currency(balance),
            BalanceClass = Format.AmountClass(balance),
            DateLabel = day is { } d ? Format.BrDate(d) : "",
            DateValue = day is { } v ? Format.Iso(v) : "",
            DateParam = day is { } p ? $"date={Format.Iso(p)}&" : "",
            PageNumber = pageNumber,
        });
    }

    [HttpGet("/current_accounts.json")]
    public async Task<IActionResult> IndexJson([FromQuery] string? date, [FromQuery] string? page)
    {
        var entries = await Repository.ListEntries(CurrentUser.Id, ParseDate(date), PerPage, (ParsePage(page) - 1) * PerPage);
        return JsonText(entries.Select(AsJson).ToList());
    }

    [HttpGet("/current_accounts/{id}")]
    public async Task<IActionResult> Show(string id)
    {
        var isJson = id.EndsWith(".json");
        var entry = await Find(isJson ? id[..^5] : id);
        if (entry is null) return NotFound();
        if (isJson) return JsonText(AsJson(entry));
        return Page("~/Views/CurrentAccounts/Show.cshtml", new EntryPage { Entry = entry });
    }

    [HttpGet("/current_accounts/new")]
    public IActionResult New() =>
        Page("~/Views/CurrentAccounts/New.cshtml", new EntryPage { Entry = new Entry { DateOcurrence = Web.Clock.Today, TypeLaunche = Entry.Debit } });

    [HttpGet("/current_accounts/{id}/edit")]
    public async Task<IActionResult> Edit(string id) =>
        await Find(id) is { } entry ? Page("~/Views/CurrentAccounts/Edit.cshtml", new EntryPage { Entry = entry }) : NotFound();

    [HttpPost("/current_accounts")]
    public async Task<IActionResult> Create()
    {
        var entry = new Entry { UserId = CurrentUser.Id };
        var errors = Assign(entry);
        if (errors.Count > 0) return Page("~/Views/CurrentAccounts/New.cshtml", new EntryPage { Entry = entry, Errors = errors }, 422);
        await Repository.SaveEntry(entry);
        return RedirectWith("/current_accounts", "notice", "Lançamento cadastrado.");
    }

    [HttpPut("/current_accounts/{id}"), HttpPatch("/current_accounts/{id}")]
    public async Task<IActionResult> Update(string id)
    {
        if (await Find(id) is not { } entry) return NotFound();
        var errors = Assign(entry);
        if (errors.Count > 0) return Page("~/Views/CurrentAccounts/Edit.cshtml", new EntryPage { Entry = entry, Errors = errors }, 422);
        await Repository.SaveEntry(entry);
        return RedirectWith($"/current_accounts/{entry.Id}", "notice", "Lançamento atualizado.");
    }

    [HttpDelete("/current_accounts/{id}")]
    public async Task<IActionResult> Destroy(string id)
    {
        if (await Find(id) is not { } entry) return NotFound();
        await Repository.DeleteEntry(entry);
        return RedirectWith("/current_accounts", "notice", "Lançamento removido.", 303);
    }

    /// <summary>Legacy daily balance search, now a filter on the listing.</summary>
    [HttpGet("/search_account")]
    public IActionResult SearchAccount([FromQuery(Name = "date_calendar_now")] string? date) => RedirectStatus($"/current_accounts?date={date}", 301);

    /// <summary>The signed in user's entry; other users' entries are "not found".</summary>
    private async Task<Entry?> Find(string id) => long.TryParse(id, out var value) ? await Repository.FindEntry(CurrentUser.Id, value) : null;

    /// <summary>
    /// Copies the form fields present in the request into the entry and returns the validation messages.
    /// Like Rails' strong parameters + update, keys missing from the form keep the stored value;
    /// a key that is present (even empty) is assigned.
    /// </summary>
    private List<string> Assign(Entry entry)
    {
        bool Has(string name) => Request.HasFormContentType && Request.Form.ContainsKey($"current_account[{name}]");
        int? Integer(string name)
        {
            var value = Field($"current_account[{name}]").Trim();
            return IntegerRegex().IsMatch(value) && int.TryParse(value, out var n) ? n : null;
        }
        if (Has("cash_account_id")) entry.CashAccountId = Integer("cash_account_id");
        if (Has("cost_id")) entry.CostId = Integer("cost_id");
        if (Has("type_launche"))
            entry.TypeLaunche = Field("current_account[type_launche]") switch { "debito" => Entry.Debit, "credito" => Entry.Credit, _ => null };
        if (Has("date_ocurrence")) entry.DateOcurrence = ParseDate(Field("current_account[date_ocurrence]"));
        if (Has("price"))
        {
            entry.PriceText = Field("current_account[price]").Trim();
            entry.Price = decimal.TryParse(entry.PriceText, NumberStyles.Float, CultureInfo.InvariantCulture, out var price)
                ? Math.Round(price, 2, MidpointRounding.AwayFromZero) : null;
        }
        if (Has("historic"))
        {
            var historic = Field("current_account[historic]");
            entry.Historic = historic.Length == 0 ? null : historic;
        }
        return entry.Validate();
    }

    private object AsJson(Entry e) => new
    {
        id = e.Id,
        cash_account_id = e.CashAccountId,
        cost_id = e.CostId,
        date_ocurrence = e.DateIso,
        price = Format.DecimalText(e.Price ?? 0),
        historic = e.Historic,
        user_id = e.UserId,
        type_launche = e.TypeLaunche,
        url = AbsoluteUrl($"/current_accounts/{e.Id}.json"),
    };

    private ContentResult JsonText(object value) => Content(JsonSerializer.Serialize(value, JsonOptions), "application/json; charset=utf-8");
}

public class DashboardPage : Page
{
    public string MonthLabel { get; set; } = "";
    public string PreviousMonth { get; set; } = "";
    public string NextMonth { get; set; } = "";
    public string Today { get; set; } = "";
    public Table Daily { get; set; } = null!;
    public Table Monthly { get; set; } = null!;
}

public partial class DashboardsController(Web.Web web, Repository repository) : AppController(web, repository)
{
    [GeneratedRegex(@"^(\d{4})-(\d{1,2})$")]
    private static partial Regex MonthRegex();

    [HttpGet("/dashboard")]
    public async Task<IActionResult> Show([FromQuery] string? month)
    {
        var first = ParseMonth(month);
        var (daily, monthBalance, monthly, ridesNet) = await Repository.SummarizeMonth(CurrentUser.Id, first, first.AddMonths(1).AddDays(-1));
        var label = $"{Format.Months[first.Month - 1]} de {first.Year}";
        return Page("~/Views/Dashboards/Show.cshtml", new DashboardPage
        {
            Nav = "dashboard",
            MonthLabel = char.ToUpperInvariant(label[0]) + label[1..],
            PreviousMonth = first.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            NextMonth = first.AddMonths(1).ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Today = Format.Iso(Web.Clock.Today),
            Daily = new Table("Data", daily, "Total do mês", monthBalance),
            Monthly = new Table("Item", monthly, "Saldo do mês", ridesNet),
        });
    }

    private DateTime ParseMonth(string? value)
    {
        var match = value is null ? null : MonthRegex().Match(value);
        if (match is { Success: true } && int.TryParse(match.Groups[2].Value, out var m) && m is >= 1 and <= 12)
            return new DateTime(int.Parse(match.Groups[1].Value), m, 1);
        var today = Web.Clock.Today;
        return new DateTime(today.Year, today.Month, 1);
    }
}
