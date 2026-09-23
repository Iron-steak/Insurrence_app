namespace Insurance.Core;

// RiskData — единые данные риска всех видов; неиспользуемые поля = 0.
public sealed record RiskData(
    decimal InsuredValue,
    int Age,
    int AnnualKilometers,
    int AccidentFreeYears,
    int PropertyAge,
    bool Secured);

// Customer — отдельный тип: в сигнатурах видно, где нужен клиент.
public sealed record Customer(
    int Id,
    string Name);

// Policy — договор: Risk фиксируется при заключении; PreviousPolicyId — цепочка продлений; CancelledOn = расторжение.
public sealed record Policy(
    int Id,
    int CustomerId,
    string ProductId,
    string VariantId,
    string Subject,
    RiskData Risk,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int Years,
    decimal AnnualPremium,
    DateOnly? CancelledOn = null,
    int? PreviousPolicyId = null)
{
    // Итоговая премия: годовая × годы (вычисляется, не хранится).
    public decimal TotalPremium
    {
        get { return AnnualPremium * Years; }
    }

    // Статус из даты: Cancelled > Scheduled > Expired > Active.
    public string Status(DateOnly today)
    {
        if (CancelledOn is not null)
        {
            return "Cancelled"; // расторгнут — проверка имеет приоритет
        }

        if (today < StartsOn)
        {
            return "Scheduled"; // симулируемая дата раньше начала действия
        }

        if (today >= EndsOn)
        {
            return "Expired"; // срок действия истёк
        }

        return "Active"; // покрытие действует
    }

    // Покрытие даты: началось, не истекло и не расторгнуто до неё.
    public bool Covers(DateOnly date)
    {
        bool started = date >= StartsOn;
        bool notExpired = date < EndsOn;
        bool notCancelledBefore = CancelledOn is null || date < CancelledOn; // <-- расторжение действует только с даты расторжения, события до неё покрываются
        return started && notExpired && notCancelledBefore;
    }
}

// Жизненный цикл предложения продления: админ утверждает, клиент решает.
public enum OfferStatus
{
    AwaitingAdmin,     // создано, ждёт одобрения администратора
    AwaitingCustomer,  // цена утверждена, ожидает решения клиента
    Accepted,          // принято, по нему создан новый договор
    Declined,          // отклонено клиентом
    Withdrawn          // снято (договор расторгнут или срок истёк)
}

// RenewalOffer — предложение продления; UpdatedRisk = устаревший риск (AdvanceMonths).
public sealed record RenewalOffer(
    int Id,
    int PolicyId,
    decimal CalculatedAnnualPremium,
    decimal? ApprovedAnnualPremium,
    RiskData UpdatedRisk,
    OfferStatus Status = OfferStatus.AwaitingAdmin,
    int? NewPolicyId = null);

// Жизненный цикл заявки: ожидает решения, затем Paid или Rejected.
public enum ClaimStatus
{
    Pending,   // ожидает решения администратора
    Paid,      // одобрено и выплачено
    Rejected   // отклонено с обоснованием
}

// Claim — заявка на возмещение; расчёт ≤ заявленного (франшиза/лимит).
public sealed record Claim(
    int Id,
    int PolicyId,
    DateOnly OccurredOn,
    string Description,
    decimal Damage,
    decimal CalculatedCompensation,
    ClaimStatus Status = ClaimStatus.Pending,
    decimal PaidAmount = 0,
    string DecisionNote = "");

// AuditEntry — запись аудита; история не очищается.
public sealed record AuditEntry(
    DateOnly Date,
    string Action,
    string Message);

// InsuranceState — единственный изменяемый тип: списки, счётчики id, часы Today.
public sealed class InsuranceState
{
    public DateOnly Today { get; set; } = new(2026, 1, 1);
    public int NextCustomerId { get; set; } = 1;
    public int NextPolicyId { get; set; } = 1;
    public int NextOfferId { get; set; } = 1;
    public int NextClaimId { get; set; } = 1;
    public List<Customer> Customers { get; set; } = new List<Customer>();
    public List<Policy> Policies { get; set; } = new List<Policy>();
    public List<RenewalOffer> Offers { get; set; } = new List<RenewalOffer>();
    public List<Claim> Claims { get; set; } = new List<Claim>();
    public List<AuditEntry> Audit { get; set; } = new List<AuditEntry>();
}

// DTO нового договора (API и консоль); Risk заполняет клиент, остальное — служба.
public sealed record NewPolicyRequest(
    int CustomerId,
    string ProductId,
    string VariantId,
    string Subject,
    int Years,
    RiskData Risk);

// DTO предварительного расчёта; состояние не изменяет.
public sealed record QuoteRequest(
    string ProductId,
    string VariantId,
    int Years,
    RiskData Risk);

// Результат расчёта: годовая и итоговая премия (годы × годовая).
public sealed record Quote(
    decimal AnnualPremium,
    decimal TotalPremium);

// DTO заявки на возмещение; проверки даты/покрытия — в службе.
public sealed record ClaimRequest(
    int CustomerId,
    int PolicyId,
    DateOnly OccurredOn,
    string Description,
    decimal Damage);

// Описание варианта для GUI (интерфейс не знает конкретных классов).
public sealed record VariantDescription(
    string Id,
    string Name,
    string Description);

// Описание продукта и вариантов для выпадающих списков GUI.
public sealed record ProductDescription(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<VariantDescription> Variants);

// Бизнес-ошибка (≠техническая): сообщение — пользователю (меню/HTTP 400).
public sealed class BusinessException : Exception
{
    public BusinessException(string message) : base(message)
    {
    }
}