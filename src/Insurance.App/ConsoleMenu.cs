using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Insurance.Core;

namespace Insurance.App;

public static class ConsoleMenu
{
    // JSON с отступами и текстовыми перечислениями.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Чтение строки; конец ввода (Ctrl+Z/pipe) = штатный выход через исключение.
    private static string AskForText(string prompt)
    {
        Console.Write(prompt + ": ");
        return Console.ReadLine() ?? throw new EndOfStreamException(); // <-- null означает конец потока ввода
    }

    // Ввод целого числа; несоответствие формату — бизнес-ошибка.
    private static int AskForInt(string prompt)
    {
        string input = AskForText(prompt);
        if (int.TryParse(input, out int value))
        {
            return value;
        }

        throw new BusinessException("Zadejte celé číslo.");
    }

    // Сумма: принимаются запятая и точка — ввод независим от локали.
    private static decimal AskForMoney(string prompt)
    {
        string input = AskForText(prompt).Replace(',', '.'); // <-- нормализация разделителя к точке
        if (decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return value;
        }

        throw new BusinessException("Zadejte částku.");
    }

    // Вывод объекта как JSON с отступами и текстовыми перечислениями.
    private static void PrintJson(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
    }

    // Главный цикл консоли: выбор роли (администратор/клиент/регистрация).
    public static void Run(InsuranceService service)
    {
        Console.WriteLine("POJIŠŤOVNA — C# konzolová aplikace\nLogger se volí v appsettings.json před spuštěním.");

        try
        {
            while (true)
            {
                Console.WriteLine($"\nDatum: {service.Snapshot().Today:yyyy-MM-dd}\n1 Administrátor\n2 Zákazník\n3 Registrace zákazníka\n0 Konec");

                try
                {
                    string choice = AskForText("Volba");

                    switch (choice)
                    {
                        case "1":
                            RunAdminMenu(service);
                            break;

                        case "2":
                            PrintJson(service.Snapshot().Customers);
                            int customerId = AskForInt("ID zákazníka");
                            RunClientMenu(service, customerId);
                            break;

                        case "3":
                            string name = AskForText("Jméno");
                            PrintJson(service.AddCustomer(name));
                            break;

                        case "0":
                            return;
                    }
                }
                catch (BusinessException error)
                {
                    // Бизнес-ошибка: печать сообщения, возврат в меню.
                    Console.WriteLine("Chyba: " + error.Message);
                }
            }
        }
        catch (EndOfStreamException)
        {
            // Конец ввода — штатное завершение приложения.
        }
    }

    // Меню администратора: отчёты, утверждение предложений, решения по заявкам, сдвиг времени.
    private static void RunAdminMenu(InsuranceService service)
    {
        while (true)
        {
            Console.WriteLine($"\nADMINISTRÁTOR | {service.Snapshot().Today:yyyy-MM-dd}\n1 Všechna pojištění\n2 Končící smlouvy a nabídky\n3 Potvrdit / změnit cenu nabídky\n4 Všechny žádosti o náhradu\n5 Rozhodnout o náhradě\n6 Posun času v měsících\n7 Audit\n0 Zpět");

            try
            {
                string choice = AskForText("Volba");

                switch (choice)
                {
                    case "1":
                        PrintPolicies(service.Snapshot());
                        break;

                    case "2":
                    {
                        var state = service.Snapshot();
                        var expiring = state.Policies
                            .Where(p => p.CancelledOn is null && p.EndsOn <= state.Today.AddMonths(1)); // <-- договоры, заканчивающиеся в ближайший месяц
                        PrintJson(expiring);
                        PrintJson(state.Offers);
                        break;
                    }

                    case "3":
                    {
                        int offerId = AskForInt("ID nabídky");
                        string input = AskForText("Nová roční cena (prázdné = vypočtená)");
                        decimal? amount = ParsePriceOrNull(input);
                        PrintJson(service.ApproveOffer(offerId, amount));
                        break;
                    }

                    case "4":
                        PrintJson(service.Snapshot().Claims);
                        break;

                    case "5":
                    {
                        int claimId = AskForInt("ID žádosti");
                        string answer = AskForText("Potvrdit? a/n");
                        bool approve = IsAffirmative(answer);
                        string note = AskForText("Odůvodnění");
                        PrintJson(service.DecideClaim(claimId, approve, note));
                        break;
                    }

                    case "6":
                        PrintJson(service.AdvanceMonths(AskForInt("Počet měsíců")));
                        break;

                    case "7":
                        PrintJson(service.Snapshot().Audit);
                        break;

                    case "0":
                        return;
                }
            }
            catch (BusinessException error)
            {
                Console.WriteLine("Chyba: " + error.Message);
            }
        }
    }

    // Цена из строки: пусто = расчётная (null), иначе число с запятой или точкой.
    private static decimal? ParsePriceOrNull(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string normalized = input.Replace(',', '.');
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
        {
            return value;
        }

        throw new BusinessException("Neplatná cena.");
    }

    // Меню клиента: договоры, новое страхование, предложения, заявки, расторжения.
    private static void RunClientMenu(InsuranceService service, int customerId)
    {
        // Нет такого клиента → нет доступа к чужим данным.
        if (!service.Snapshot().Customers.Any(c => c.Id == customerId))
        {
            throw new BusinessException("Zákazník neexistuje.");
        }

        while (true)
        {
            Console.WriteLine($"\nZÁKAZNÍK #{customerId} | {service.Snapshot().Today:yyyy-MM-dd}\n1 Moje smlouvy\n2 Nové pojištění / kalkulace\n3 Nabídky prodloužení\n4 Přijmout nabídku\n5 Odmítnout nabídku\n6 Ukončit smlouvu\n7 Nahlásit škodu\n8 Moje žádosti\n9 Posun času v měsících\n0 Zpět");

            try
            {
                var state = service.Snapshot();

                // Id договоров клиента — один раз, для фильтров предложений и заявок.
                var policyIds = state.Policies
                    .Where(p => p.CustomerId == customerId)
                    .Select(p => p.Id)
                    .ToHashSet();

                string choice = AskForText("Volba");

                switch (choice)
                {
                    case "1":
                        PrintPolicies(state, customerId);
                        break;

                    case "2":
                    {
                        PrintJson(service.Products());

                        string product = AskForText("ID druhu (car/property)");
                        string variant = AskForText("ID varianty");
                        string subject = AskForText("Předmět (SPZ/adresa)");
                        int years = AskForInt("Počet let 1–10");

                        decimal insured = AskForMoney("Hodnota v Kč");
                        int age = AskForInt("Věk žadatele");
                        int kilometers = AskForInt("Km/rok (majetek: 0)");
                        int accidentFree = AskForInt("Roky bez nehody (majetek: 0)");
                        int propertyAge = AskForInt("Stáří budovy (auto: 0)");
                        bool secured = IsAffirmative(AskForText("Zabezpečeno? a/n"));

                        var risk = new RiskData(insured, age, kilometers, accidentFree, propertyAge, secured);

                        var quoteRequest = new QuoteRequest(product, variant, years, risk);
                        PrintJson(service.Quote(quoteRequest));

                        // Сначала расчёт, затем предложение заключить — клиент видит цену.
                        if (IsAffirmative(AskForText("Uzavřít? a/n")))
                        {
                            var request = new NewPolicyRequest(customerId, product, variant, subject, years, risk);
                            PrintJson(service.CreatePolicy(request));
                        }
                        break;
                    }

                    case "3":
                    {
                        // Предложения по договорам клиента (кроме ожидающих одобрения админа).
                        var offers = state.Offers
                            .Where(o => policyIds.Contains(o.PolicyId) && o.Status != OfferStatus.AwaitingAdmin);
                        PrintJson(offers);
                        break;
                    }

                    case "4":
                        PrintJson(service.AcceptOffer(AskForInt("ID nabídky"), customerId));
                        break;

                    case "5":
                        PrintJson(service.DeclineOffer(AskForInt("ID nabídky"), customerId));
                        break;

                    case "6":
                        PrintJson(service.CancelPolicy(AskForInt("ID smlouvy"), customerId));
                        break;

                    case "7":
                    {
                        int policyId = AskForInt("ID smlouvy");
                        string dateText = AskForText("Datum škody yyyy-MM-dd");

                        // Дата строго yyyy-MM-dd — формат не зависит от локали.
                        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var occurredOn))
                        {
                            throw new BusinessException("Neplatné datum.");
                        }

                        string description = AskForText("Popis škody");
                        decimal damage = AskForMoney("Výše škody Kč");

                        var request = new ClaimRequest(customerId, policyId, occurredOn, description, damage);
                        PrintJson(service.SubmitClaim(request));
                        break;
                    }

                    case "8":
                        PrintJson(state.Claims.Where(c => policyIds.Contains(c.PolicyId)));
                        break;

                    case "9":
                        PrintJson(service.AdvanceMonths(AskForInt("Počet měsíců")));
                        break;

                    case "0":
                        return;
                }
            }
            catch (BusinessException error)
            {
                Console.WriteLine("Chyba: " + error.Message);
            }
        }
    }

    // "a" (любой регистр) = да, всё остальное — нет.
    private static bool IsAffirmative(string value)
    {
        return value.Equals("a", StringComparison.OrdinalIgnoreCase);
    }

    // Договоры со статусом; customerId ограничивает выборку одним клиентом.
    private static void PrintPolicies(InsuranceState state, int? customerId = null)
    {
        var policies = state.Policies.Where(p => customerId is null || p.CustomerId == customerId);
        var rows = policies.Select(p => new { Policy = p, Status = p.Status(state.Today) });
        PrintJson(rows);
    }
}