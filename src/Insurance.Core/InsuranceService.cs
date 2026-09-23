namespace Insurance.Core;

// InsuranceService — фасад всех сценариев: координирует хранилище, движок и логгер.
public sealed class InsuranceService
{
    private readonly IStateStore _store;
    private readonly PricingEngine _pricing;
    private readonly IInsuranceLogger _logger;
    private readonly Rules _rules;
    private readonly object _gate = new();

    // Конструктор: сохраняет зависимости (хранилище, движок, логгер, тарифы).
    public InsuranceService(IStateStore store, PricingEngine pricing, IInsuranceLogger logger, Rules rules)
    {
        _store = store;
        _pricing = pricing;
        _logger = logger;
        _rules = rules;
    }

    // Событие: наблюдатели (GUI) узнают об изменениях без опроса.
    public event Action<AuditEntry>? Changed;

    // Возвращает копию всего состояния (для GUI, консоли, отчётов).
    public InsuranceState Snapshot()
    {
        return _store.Read();
    }

    // Возвращает список продуктов с вариантами для построения форм ввода.
    public IReadOnlyList<ProductDescription> Products()
    {
        return _pricing.Describe();
    }

    // Рассчитывает цену без изменения состояния.
    public Quote Quote(QuoteRequest request)
    {
        return _pricing.Quote(request);
    }

    // Шаблон изменяющих операций: состояние + аудит транзакцией; сбой лога — не откат.
    private T RunTransaction<T>(string action, Func<InsuranceState, (T Result, string Message)> operation)
    {
        lock (_gate)
        {
            AuditEntry? entry = null;

            // Копия состояния и запись аудита меняются как одна атомарная операция.
            T result = _store.Update(s =>
            {
                var change = operation(s);
                entry = new AuditEntry(s.Today, action, change.Message);
                s.Audit.Add(entry);
                return change.Result;
            });

            // Внешний лог — после сохранения; его сбой лишь печатается.
            try
            {
                _logger.Log(entry!);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Zápis do externího logu selhal; audit v datech zůstává: {error.Message}");
            }

            Changed?.Invoke(entry!);
            return result;
        }
    }

    // Поиск договора по id; не найден — бизнес-ошибка.
    private static Policy FindPolicyOrThrow(InsuranceState state, int id)
    {
        Policy? policy = state.Policies.SingleOrDefault(p => p.Id == id);
        if (policy is null)
        {
            throw new BusinessException("Smlouva neexistuje.");
        }

        return policy;
    }

    private static RenewalOffer FindOfferOrThrow(InsuranceState state, int id)
    {
        RenewalOffer? offer = state.Offers.SingleOrDefault(o => o.Id == id);
        if (offer is null)
        {
            throw new BusinessException("Nabídka neexistuje.");
        }

        return offer;
    }

    // Клиент работает только со своими договорами; чужой — ошибка.
    private static void EnsureOwner(Policy policy, int customerId)
    {
        if (policy.CustomerId != customerId)
        {
            throw new BusinessException("Smlouva patří jinému zákazníkovi.");
        }
    }

    // Обязательный текст: не пуст, ≤ MaxTextLength; возврат без крайних пробелов.
    private string ValidateRequiredText(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > _rules.Common.MaxTextLength)
        {
            throw new BusinessException($"{label}: zadejte 1–{_rules.Common.MaxTextLength} znaků.");
        }

        return value.Trim();
    }

    // Records неизменяемы: «изменение» = замена записи на том же индексе.
    private static void Replace<T>(List<T> list, T oldItem, T newItem)
    {
        list[list.IndexOf(oldItem)] = newItem; // <-- поиск индекса по ссылке старой записи и подстановка новой
    }

    // Регистрирует нового клиента и возвращает его запись.
    public Customer AddCustomer(string name)
    {
        return RunTransaction("CustomerCreated", s =>
        {
            string cleanName = ValidateRequiredText(name, "Jméno");
            var customer = new Customer(s.NextCustomerId++, cleanName);
            s.Customers.Add(customer);

            string message = $"Customer #{customer.Id}: {customer.Name}";
            return (customer, message);
        });
    }

    // Заключает новый договор (расчёт цены — через тот же движок).
    public Policy CreatePolicy(NewPolicyRequest request)
    {
        return RunTransaction("PolicyCreated", s =>
        {
            if (!s.Customers.Any(c => c.Id == request.CustomerId))
            {
                throw new BusinessException("Zákazník neexistuje.");
            }

            // Договор считает цену тем же движком, что и предварительная оценка (Quote).
            var quoteRequest = new QuoteRequest(
                request.ProductId,
                request.VariantId,
                request.Years,
                request.Risk);
            var quote = _pricing.Quote(quoteRequest);

            string subject = ValidateRequiredText(request.Subject, "Předmět pojištění");
            var policy = new Policy(
                s.NextPolicyId++,
                request.CustomerId,
                request.ProductId,
                request.VariantId,
                subject,
                request.Risk,
                s.Today,
                s.Today.AddYears(request.Years),
                request.Years,
                quote.AnnualPremium);

            s.Policies.Add(policy);

            string message = $"Policy #{policy.Id}, customer #{policy.CustomerId}, annual {policy.AnnualPremium} CZK, {policy.Years} years";
            return (policy, message);
        });
    }

    // Сдвигает симулируемую дату и создаёт предложения о продлении истекающих договоров.
    public DateOnly AdvanceMonths(int months)
    {
        return RunTransaction("TimeAdvanced", s =>
        {
            if (months < 1 || months > _rules.Service.MaxMonths)
            {
                throw new BusinessException($"Čas lze posunout o 1–{_rules.Service.MaxMonths} měsíců.");
            }

            DateOnly date = s.Today.AddMonths(months);
            if (date.Year > _rules.Service.MaxSimulationYear)
            {
                throw new BusinessException($"Konec simulačního kalendáře: rok {_rules.Service.MaxSimulationYear}.");
            }

            s.Today = date;

            // Истекающие активные договоры: по каждому — по одному предложению, если нет.
            var expiring = s.Policies
                .Where(p => p.CancelledOn is null && p.EndsOn <= s.Today.AddMonths(_rules.Service.LookaheadMonths))
                .ToArray();

            foreach (var policy in expiring)
            {
                // Если предложение по этому договору уже есть, оно не дублируется.
                if (s.Offers.Any(o => o.PolicyId == policy.Id))
                {
                    continue;
                }

                // Стаж риска: максимум срока и лет от начала — копится и при продлениях.
                int elapsedYears = Math.Max(policy.Years, s.Today.Year - policy.StartsOn.Year); // <-- стаж должен накапливаться и при продлении
                int age = Math.Min(_rules.Service.MaxRiskAge, policy.Risk.Age + elapsedYears); // <-- возраст ограничивается сверху, чтобы валидация прошла

                // Была неотклонённая заявка → бонус обнуляется.
                bool hadClaim = s.Claims.Any(c => c.PolicyId == policy.Id && c.Status != ClaimStatus.Rejected);

                int accidentFreeYears;
                if (hadClaim)
                {
                    accidentFreeYears = 0;
                }
                else
                {
                    accidentFreeYears = Math.Min(age - _rules.Common.MinAge, policy.Risk.AccidentFreeYears + elapsedYears); // <-- верхняя граница — физически возможный стаж при данном возрасте
                }

                var updatedRisk = policy.Risk with
                {
                    Age = age,
                    AccidentFreeYears = accidentFreeYears,
                    PropertyAge = Math.Min(_rules.Service.MaxRiskBuildingAge, policy.Risk.PropertyAge + elapsedYears)
                };

                // Цена продления: пересчёт через Quote + тариф инфляции.
                var quoteRequest = new QuoteRequest(
                    policy.ProductId,
                    policy.VariantId,
                    policy.Years,
                    updatedRisk);

                decimal amount = _pricing.Quote(quoteRequest).AnnualPremium;
                amount = decimal.Round(amount * _rules.Service.RenewalInflation, _rules.Common.RoundDigits, MidpointRounding.AwayFromZero); // <-- инфляция применяется к пересчитанной цене

                s.Offers.Add(new RenewalOffer(s.NextOfferId++, policy.Id, amount, null, updatedRisk));
            }

            return (date, $"Simulation date {date:yyyy-MM-dd}");
        });
    }

    // Утверждает цену предложения (пусто = расчётная) и передаёт его клиенту.
    public RenewalOffer ApproveOffer(int id, decimal? annualPremium)
    {
        return RunTransaction("RenewalApproved", s =>
        {
            var offer = FindOfferOrThrow(s, id);

            if (offer.Status != OfferStatus.AwaitingAdmin)
            {
                throw new BusinessException("Nabídka již byla zpracována.");
            }

            // Пустая цена = расчётная; число переопределяет её.
            decimal amount = annualPremium ?? offer.CalculatedAnnualPremium;

            int roundDigits = _rules.Common.RoundDigits;
            bool invalid = amount <= 0
                || amount > _rules.Service.MaxApprovedPrice
                || decimal.Round(amount, roundDigits) != amount; // <-- отвергается цена с числом десятичных знаков больше разрешённого
            if (invalid)
            {
                throw new BusinessException($"Roční cena musí být kladná, nejvýše {_rules.Service.MaxApprovedPrice:N0} Kč, maximálně {roundDigits} desetinná místa.");
            }

            var approved = offer with
            {
                Status = OfferStatus.AwaitingCustomer,
                ApprovedAnnualPremium = amount
            };

            // Замена записи новой — records неизменяемы.
            Replace(s.Offers, offer, approved);

            return (approved, $"Offer #{id}, approved annual price {amount} CZK");
        });
    }

    // Принимает предложение клиентом: создаёт новый договор-продолжение.
    public Policy AcceptOffer(int id, int customerId)
    {
        return RunTransaction("PolicyRenewed", s =>
        {
            var offer = FindOfferOrThrow(s, id);
            var previous = FindPolicyOrThrow(s, offer.PolicyId);
            EnsureOwner(previous, customerId);

            bool canAccept = offer.Status == OfferStatus.AwaitingCustomer && previous.CancelledOn is null;
            if (!canAccept)
            {
                throw new BusinessException("Nabídku nelze přijmout.");
            }

            // Новое покрытие: со дня окончания прежнего (или сегодня) — без перерыва.
            DateOnly starts;
            if (s.Today > previous.EndsOn)
            {
                starts = s.Today;
            }
            else
            {
                starts = previous.EndsOn;
            }

            var policy = new Policy(
                s.NextPolicyId++,
                customerId,
                previous.ProductId,
                previous.VariantId,
                previous.Subject,
                offer.UpdatedRisk,
                starts,
                starts.AddYears(previous.Years),
                previous.Years,
                offer.ApprovedAnnualPremium!.Value,
                PreviousPolicyId: previous.Id); // <-- ссылка на прежний договор формирует цепочку продлений

            s.Policies.Add(policy);

            var accepted = offer with
            {
                Status = OfferStatus.Accepted,
                NewPolicyId = policy.Id
            };
            Replace(s.Offers, offer, accepted);

            string message = $"Policy #{previous.Id} renewed as #{policy.Id}, annual {policy.AnnualPremium} CZK";
            return (policy, message);
        });
    }

    // Отклоняет предложение клиентом.
    public bool DeclineOffer(int id, int customerId)
    {
        return RunTransaction("RenewalDeclined", s =>
        {
            var offer = FindOfferOrThrow(s, id);
            var policy = FindPolicyOrThrow(s, offer.PolicyId);
            EnsureOwner(policy, customerId);

            if (offer.Status != OfferStatus.AwaitingCustomer)
            {
                throw new BusinessException("Nabídku nelze odmítnout.");
            }

            var declined = offer with { Status = OfferStatus.Declined };
            Replace(s.Offers, offer, declined);

            return (true, $"Offer #{id} declined");
        });
    }

    // Расторгает договор и снимает его открытые предложения.
    public bool CancelPolicy(int id, int customerId)
    {
        return RunTransaction("PolicyCancelled", s =>
        {
            var policy = FindPolicyOrThrow(s, id);
            EnsureOwner(policy, customerId);

            bool stillValid = policy.CancelledOn is null && s.Today < policy.EndsOn;
            if (!stillValid)
            {
                throw new BusinessException("Smlouva již skončila.");
            }

            var cancelled = policy with { CancelledOn = s.Today };
            Replace(s.Policies, policy, cancelled);

            // Расторжение снимает открытые предложения продления.
            var liveOffers = s.Offers
                .Where(o => o.PolicyId == id && (o.Status == OfferStatus.AwaitingAdmin || o.Status == OfferStatus.AwaitingCustomer))
                .ToArray();

            foreach (var offer in liveOffers)
            {
                var withdrawn = offer with { Status = OfferStatus.Withdrawn };
                Replace(s.Offers, offer, withdrawn);
            }

            return (true, $"Policy #{id} cancelled");
        });
    }

    // Подаёт заявление о страховом случае (возмещение не превышает остаток лимита).
    public Claim SubmitClaim(ClaimRequest request)
    {
        return RunTransaction("ClaimRequested", s =>
        {
            var policy = FindPolicyOrThrow(s, request.PolicyId);
            EnsureOwner(policy, request.CustomerId);

            // Дата ущерба: не в будущем и в периоде покрытия.
            if (request.OccurredOn > s.Today || !policy.Covers(request.OccurredOn))
            {
                throw new BusinessException("Datum škody musí být v minulosti nebo dnes, v době platného krytí.");
            }

            decimal calculated = _pricing.Compensation(policy, request.Damage);

            // Лимит: из страховой стоимости вычитаются уже учтённые заявки.
            decimal reserved = s.Claims
                .Where(c => c.PolicyId == policy.Id && c.Status != ClaimStatus.Rejected)
                .Sum(c => c.CalculatedCompensation); // <-- сумма уже учтённых расчётных возмещений

            decimal remaining = Math.Max(0, policy.Risk.InsuredValue - reserved); // <-- остаток лимита после предыдущих заявок
            decimal amount = Math.Min(calculated, remaining); // <-- расчётное возмещение ограничивается остатком лимита

            string description = ValidateRequiredText(request.Description, "Popis škody");
            var claim = new Claim(
                s.NextClaimId++,
                policy.Id,
                request.OccurredOn,
                description,
                request.Damage,
                amount);

            s.Claims.Add(claim);

            string message = $"Claim #{claim.Id}, policy #{policy.Id}, requested {request.Damage} CZK, calculated {amount} CZK";
            return (claim, message);
        });
    }

    // Выносит решение по заявке: одобрить (выплатить) или отклонить.
    public Claim DecideClaim(int id, bool approve, string note)
    {
        string action = approve ? "ClaimPaid" : "ClaimRejected";

        return RunTransaction(action, s =>
        {
            Claim? claim = s.Claims.SingleOrDefault(c => c.Id == id);
            if (claim is null)
            {
                throw new BusinessException("Žádost neexistuje.");
            }

            if (claim.Status != ClaimStatus.Pending)
            {
                throw new BusinessException("Žádost již byla zpracována.");
            }

            decimal paidAmount = approve ? claim.CalculatedCompensation : 0; // <-- при одобрении выплачивается расчётное возмещение
            ClaimStatus status = approve ? ClaimStatus.Paid : ClaimStatus.Rejected;
            string decisionNote = ValidateRequiredText(note, "Odůvodnění");

            var decided = claim with
            {
                Status = status,
                PaidAmount = paidAmount,
                DecisionNote = decisionNote
            };

            Replace(s.Claims, claim, decided);

            string message = $"Claim #{id}, {decided.Status}, payment {decided.PaidAmount} CZK, {decided.DecisionNote}";
            return (decided, message);
        });
    }
}