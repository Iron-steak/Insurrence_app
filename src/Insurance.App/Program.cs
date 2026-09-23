using System.Text.Json.Serialization;
using Insurance.App;
using Insurance.Core;

// Точка входа: с --api — HTTP API, без него — консольное меню; данные общие.
Console.OutputEncoding = System.Text.Encoding.UTF8;
bool apiMode = args.Contains("--api");

// Конфигурация — из папки exe, чтобы файл был найден при любом запуске.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args.Where(a => a != "--api").ToArray(),
    ContentRootPath = AppContext.BaseDirectory
});

// Порт по умолчанию 5090 — GUI должен заранее знать адрес сервера.
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5090");
}

// Данные — у корня репо (по маркеру .sln), чтобы путь был одинаков для всех клиентов.
string directory = Path.GetFullPath(
    builder.Configuration["Insurance:DataDirectory"] ?? "data",
    FindRepositoryRoot(builder.Environment.ContentRootPath));
Directory.CreateDirectory(directory);

// Межпроцессная блокировка: консоль и API не пишут файл одновременно.
using var processLock = new FileStream(
    Path.Combine(directory, "instance.lock"),
    FileMode.OpenOrCreate,
    FileAccess.ReadWrite,
    FileShare.None);

// Composition root: здесь собираются зависимости; подмена хранилища — в этом блоке.
builder.Services.AddSingleton<IStateStore>(
    new JsonStateStore(Path.Combine(directory, "insurance.json")));

// Тарифы из appsettings.json (Insurance:Rules); нет секции — дефолты Rules.
builder.Services.AddSingleton(
    builder.Configuration.GetSection("Insurance:Rules").Get<Rules>() ?? new Rules());

builder.Services.AddSingleton<IInsuranceProduct, CarInsurance>();
builder.Services.AddSingleton<IInsuranceProduct, PropertyInsurance>();
builder.Services.AddSingleton<PricingEngine>();
builder.Services.AddSingleton<InsuranceService>();

// Логгер аудита — по Insurance:Logger: console, file или both.
string loggerKind = builder.Configuration["Insurance:Logger"] ?? "file";
builder.Services.AddSingleton<IInsuranceLogger>(_ =>
{
    string kind = loggerKind.ToLowerInvariant();

    switch (kind)
    {
        case "console":
            return new ConsoleInsuranceLogger();

        case "file":
            return new FileInsuranceLogger(Path.Combine(directory, "insurance.log"));

        case "both":
            return new CompositeInsuranceLogger(
                new ConsoleInsuranceLogger(),
                new FileInsuranceLogger(Path.Combine(directory, "insurance.log")));

        default:
            throw new InvalidOperationException("Insurance:Logger musí být console, file nebo both.");
    }
});

// Перечисления в JSON API сериализуются текстом ("Pending"), а не числами.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

await using var app = builder.Build();
var service = app.Services.GetRequiredService<InsuranceService>();

// Демо-клиент для пустой базы, чтобы с первого запуска было с чем работать.
if (service.Snapshot().Customers.Count == 0)
{
    service.AddCustomer("Jan Novák");
}

if (!apiMode)
{
    ConsoleMenu.Run(service);
    return;
}

// Middleware: BusinessException → HTTP 400 с сообщением для GUI.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BusinessException error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});

// --- Маршруты: сопоставление пути с функциями-обработчиками ---
app.MapGet("/api/health", () => GetHealth());
app.MapGet("/api/products", () => GetProducts());
app.MapGet("/api/state", (int? customerId) => GetState(customerId));

app.MapPost("/api/customers", (CustomerRequest request) => AddCustomer(request));
app.MapPost("/api/quote", (QuoteRequest request) => GetQuote(request));
app.MapPost("/api/policies", (NewPolicyRequest request) => CreatePolicy(request));
app.MapPost("/api/time", (TimeRequest request) => AdvanceTime(request));
app.MapPost("/api/policies/{id:int}/cancel", (int id, OwnerRequest request) => CancelPolicy(id, request));
app.MapPost("/api/offers/{id:int}/approve", (int id, ApprovalRequest request) => ApproveOffer(id, request));
app.MapPost("/api/offers/{id:int}/accept", (int id, OwnerRequest request) => AcceptOffer(id, request));
app.MapPost("/api/offers/{id:int}/decline", (int id, OwnerRequest request) => DeclineOffer(id, request));
app.MapPost("/api/claims", (ClaimRequest request) => SubmitClaim(request));
app.MapPost("/api/claims/{id:int}/decide", (int id, DecisionRequest request) => DecideClaim(id, request));

await app.RunAsync();

// --- Обработчики конечных точек ---

// Функция здоровья API: имя сервиса и текущая дата симуляции.
object GetHealth()
{
    return new
    {
        name = "Insurance C# API",
        today = service.Snapshot().Today
    };
}

// Возвращает список продуктов и вариантов.
IReadOnlyList<ProductDescription> GetProducts()
{
    return service.Products();
}

// Снимок для GUI; customerId ограничивает данные одного клиента, аудит — админу.
object GetState(int? customerId)
{
    var state = service.Snapshot();

    var policies = state.Policies
        .Where(p => customerId is null || p.CustomerId == customerId)
        .ToArray();

    var ids = policies.Select(p => p.Id).ToHashSet();

    var offers = state.Offers.Where(o =>
        ids.Contains(o.PolicyId) && (customerId is null || o.Status != OfferStatus.AwaitingAdmin));

    var claims = state.Claims.Where(c => ids.Contains(c.PolicyId));

    // Аудит — только администратору (customerId == null).
    IEnumerable<AuditEntry> audit;
    if (customerId is null)
    {
        audit = state.Audit.TakeLast(200); // <-- последние 200 записей, чтобы ограничить объём ответа
    }
    else
    {
        audit = Enumerable.Empty<AuditEntry>();
    }

    var policyRows = policies.Select(p => new { contract = p, status = p.Status(state.Today) });

    return new
    {
        state.Today,
        state.Customers,
        policies = policyRows,
        offers,
        claims,
        audit
    };
}

// Регистрирует клиента по имени.
Customer AddCustomer(CustomerRequest request)
{
    return service.AddCustomer(request.Name);
}

// Рассчитывает цену по запросу.
Quote GetQuote(QuoteRequest request)
{
    return service.Quote(request);
}

// Заключает новый договор.
Policy CreatePolicy(NewPolicyRequest request)
{
    return service.CreatePolicy(request);
}

// Сдвигает симулируемое время на указанное число месяцев.
object AdvanceTime(TimeRequest request)
{
    return new { today = service.AdvanceMonths(request.Months) };
}

// Расторгает договор клиентом.
object CancelPolicy(int id, OwnerRequest request)
{
    return new { cancelled = service.CancelPolicy(id, request.CustomerId) };
}

// Утверждает цену предложения администратором.
RenewalOffer ApproveOffer(int id, ApprovalRequest request)
{
    return service.ApproveOffer(id, request.AnnualPremium);
}

// Принимает предложение клиентом (создаёт новый договор).
Policy AcceptOffer(int id, OwnerRequest request)
{
    return service.AcceptOffer(id, request.CustomerId);
}

// Отклоняет предложение клиентом.
object DeclineOffer(int id, OwnerRequest request)
{
    return new { declined = service.DeclineOffer(id, request.CustomerId) };
}

// Подаёт заявление о страховом случае.
Claim SubmitClaim(ClaimRequest request)
{
    return service.SubmitClaim(request);
}

// Выносит решение по заявке (одобрить/отклонить).
Claim DecideClaim(int id, DecisionRequest request)
{
    return service.DecideClaim(id, request.Approve, request.Note);
}

// Поиск корня репо по маркеру Insurance.sln (вверх по каталогам).
static string FindRepositoryRoot(string startDirectory)
{
    var dir = new DirectoryInfo(startDirectory);

    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Insurance.sln")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return startDirectory;
}

// DTO маршрутов API: минимум полей на каждое действие.
public sealed record CustomerRequest(string Name);
public sealed record TimeRequest(int Months);
public sealed record OwnerRequest(int CustomerId);
public sealed record ApprovalRequest(decimal? AnnualPremium);
public sealed record DecisionRequest(bool Approve, string Note);