namespace ControlFinan.Models;

public record Option(int Id, string Name);

/// <summary>A row of current_accounts (a credit or a debit). Form fields that are blank or invalid are null.</summary>
public class Entry
{
    public static readonly Option[] CashAccounts =
    [
        new(0, "Conta Dinheiro"), new(1, "Itaú"), new(2, "Santander"), new(3, "CEF Poupança"), new(4, "PagSeguro"),
        new(5, "Uber"), new(6, "Platinum Itaú"), new(7, "Banco do Brasil"), new(8, "99Pop"),
    ];

    public static readonly Option[] Costs =
    [
        new(0, "Combustível"), new(1, "Central"), new(2, "Aluguel Carro"), new(3, "Almoço"), new(4, "Lanche"), new(5, "Diversos"),
        new(6, "Corridas"), new(7, "Telefone"), new(8, "Lavagem"), new(9, "Transferência"), new(10, "Depósitos"),
        new(11, "Estacionamento"), new(12, "Impostos"), new(13, "Multas"), new(14, "Manutenção"), new(15, "Viagens Uber"),
    ];

    public const int Debit = -1;
    public const int Credit = 1;
    public const int Rides = 6;

    /// <summary>Ride expenses discounted from the rides in the monthly balance (legacy dashboard rule), in this order.</summary>
    public static readonly Option[] RideExpenses = [new(0, "Combustível"), new(8, "Lavagem"), new(7, "Telefone")];

    public long? Id { get; set; }
    public long UserId { get; set; }
    public int? CashAccountId { get; set; }
    public int? CostId { get; set; }
    public int? TypeLaunche { get; set; }
    public DateTime? DateOcurrence { get; set; }
    public decimal? Price { get; set; }
    /// <summary>Raw price typed in the form, used by the validation like Rails numericality.</summary>
    public string PriceText { get; set; } = "";
    public string? Historic { get; set; }

    public bool Persisted => Id.HasValue;
    public bool IsCredit => TypeLaunche == Credit;
    public string NameAccount => Name(CashAccounts, CashAccountId);
    public string NameCost => Name(Costs, CostId);
    public string TypeName => IsCredit ? "Crédito" : "Débito";
    public decimal SignedPrice => IsCredit ? Price ?? 0 : -(Price ?? 0);
    public string SignedCurrency => Format.Currency(SignedPrice);
    public string Color => IsCredit ? "text-emerald-700" : "text-red-700";
    public string DateBr => DateOcurrence is { } date ? Format.BrDate(date) : "";
    public string DayMonth => DateOcurrence is { } date ? Format.DayMonth(date) : "";
    public string DateIso => DateOcurrence is { } date ? Format.Iso(date) : "";
    public string HistoricText => Historic ?? "";
    /// <summary>The value shown in the form: the decimal like Rails, or what the user typed.</summary>
    public string PriceField => Price is { } price ? Format.DecimalText(price) : PriceText;

    /// <summary>The Rails messages (pt-BR) in the same order as the model validations.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (TypeLaunche is not (Debit or Credit)) errors.Add("Tipo não está incluído na lista");
        if (!CashAccounts.Any(o => o.Id == CashAccountId)) errors.Add("Conta não está incluído na lista");
        if (!Costs.Any(o => o.Id == CostId)) errors.Add("Categoria não está incluído na lista");
        if (DateOcurrence is null) errors.Add("Data não pode ficar em branco");
        // Like Rails presence + numericality: a blank value is also "not a number".
        if (Price is null && PriceText.Length == 0)
        {
            errors.Add("Valor não pode ficar em branco");
            errors.Add("Valor não é um número");
        }
        else if (Price is null) errors.Add("Valor não é um número");
        else if (Price <= 0) errors.Add("Valor deve ser maior que 0");
        return errors;
    }

    private static string Name(Option[] options, int? id) => options.FirstOrDefault(o => o.Id == id)?.Name ?? "Não definido";
}

/// <summary>Mirrors the Rails users table (has_secure_password).</summary>
public class User
{
    public long Id { get; set; }
    public string EmailAddress { get; set; } = "";
    public string PasswordDigest { get; set; } = "";

    public static string NormalizeEmail(string? value) => (value ?? "").Trim().ToLowerInvariant();
}

/// <summary>A label and an amount in the dashboard tables.</summary>
public record Row(string Label, decimal Amount)
{
    public string Currency => Format.Currency(Amount);
    public string Class => Format.AmountClass(Amount);
}

/// <summary>The data of one dashboard table.</summary>
public record Table(string Header, List<Row> Rows, string TotalLabel, decimal Total)
{
    public string TotalCurrency => Format.Currency(Total);
    public string TotalClass => Format.AmountClass(Total);
}
