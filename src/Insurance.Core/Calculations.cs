namespace Insurance.Core;

// IInsuranceVariant — стратегия премии и возмещения; реализация заменяема через интерфейс.
public interface IInsuranceVariant
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    decimal Premium(decimal basePremium, RiskData risk);
    decimal Compensation(decimal damage, RiskData risk);
}

// BaseVariant — база вариантов: возмещение не выше страховой стоимости.
public abstract class BaseVariant : IInsuranceVariant
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    public abstract decimal Premium(decimal basePremium, RiskData risk);

    public virtual decimal Compensation(decimal damage, RiskData risk)
    {
        return Math.Min(damage, risk.InsuredValue); // <-- возмещение ограничивается страховой стоимостью
    }
}

// Полное покрытие: без франшизы. Премия равна базовой, возмещение — полное.
public sealed class FullCoverage : BaseVariant
{
    public override string Id
    {
        get { return "full"; }
    }

    public override string Name
    {
        get { return "Bez spoluúčasti"; }
    }

    public override string Description
    {
        get { return "100 % kryté škody, bez spoluúčasti."; }
    }

    // Возвращает базовую премию (без изменений — скидок нет).
    public override decimal Premium(decimal basePremium, RiskData risk)
    {
        return basePremium; // <-- скидок нет, цена без изменений
    }
}

// Франшиза: премия ниже (скидка в Rules), часть ущерба несёт клиент.
public sealed class DeductibleCoverage : BaseVariant
{
    private readonly Rules _rules;

    public DeductibleCoverage(Rules rules)
    {
        _rules = rules;
    }

    public override string Id
    {
        get { return "deductible"; }
    }

    public override string Name
    {
        get { return "Se spoluúčasti"; }
    }

    public override string Description
    {
        get
        {
            int discountPercent = (int)Math.Round((1 - _rules.Deductible.Discount) * 100);
            int percent = (int)Math.Round(_rules.Deductible.Percent * 100);
            return $"Sleva {discountPercent} % na pojistném; spoluúčast max({percent} % škody, {_rules.Deductible.FixedMin:N0} Kč).";
        }
    }

    // Премия со скидкой за франшизу.
    public override decimal Premium(decimal basePremium, RiskData risk)
    {
        return basePremium * _rules.Deductible.Discount; // <-- скидка применяется к базовой премии
    }

    // Возмещение за вычетом франшизы (процент от ущерба, но не меньше минимума).
    public override decimal Compensation(decimal damage, RiskData risk)
    {
        decimal covered = Math.Min(damage, risk.InsuredValue); // <-- базовое возмещение, ограниченное страховой стоимостью
        decimal spoluucast = Math.Max(covered * _rules.Deductible.Percent, _rules.Deductible.FixedMin); // <-- франшиза: процент от ущерба, но не меньше фиксированного минимума
        return Math.Max(0, covered - spoluucast); // <-- из возмещения вычитается франшиза; 0, если франшиза превышает ущерб
    }
}

// Малый пробег: скидка (в Rules) при пробеге не выше лимита; иначе ошибка.
public sealed class LowMileageCoverage : BaseVariant
{
    private readonly Rules _rules;

    public LowMileageCoverage(Rules rules)
    {
        _rules = rules;
    }

    public override string Id
    {
        get { return "low-mileage"; }
    }

    public override string Name
    {
        get { return "Malý nájezd"; }
    }

    public override string Description
    {
        get
        {
            int discountPercent = (int)Math.Round((1 - _rules.LowMileage.Discount) * 100);
            return $"Do {_rules.LowMileage.MaxKm:N0} km/rok, sleva {discountPercent} %, bez spoluúčasti.";
        }
    }

    // Премия со скидкой за малый пробег; превышение лимита — ошибка.
    public override decimal Premium(decimal basePremium, RiskData risk)
    {
        // Выше лимита — нет права на скидку (бизнес-ошибка).
        if (risk.AnnualKilometers > _rules.LowMileage.MaxKm)
        {
            throw new BusinessException($"Varianta malého nájezdu vyžaduje nejvýše {_rules.LowMileage.MaxKm:N0} km/rok.");
        }

        return basePremium * _rules.LowMileage.Discount; // <-- скидка применяется к базовой премии
    }
}

// IInsuranceProduct — контракт вида страхования: валидация, базовая премия, варианты.
public interface IInsuranceProduct
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    IReadOnlyList<IInsuranceVariant> Variants { get; }
    void Validate(RiskData risk);
    decimal BaseAnnualPremium(RiskData risk);
}

// InsuranceProduct — база продуктов: общая валидация (стоимость, возраст) + Rules; потомок — через base.Validate.
public abstract class InsuranceProduct : IInsuranceProduct
{
    protected Rules Rules { get; }

    protected InsuranceProduct(Rules rules)
    {
        Rules = rules;
    }

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<IInsuranceVariant> Variants { get; }

    // Общая валидация риска: страховая стоимость и возраст в допустимых пределах.
    public virtual void Validate(RiskData risk)
    {
        // Пределы, общие для всех видов страхования.
        decimal insuredValue = risk.InsuredValue;
        if (insuredValue <= 0 || insuredValue > Rules.Common.MaxInsuredValue)
        {
            throw new BusinessException($"Hodnota musí být 1 až {Rules.Common.MaxInsuredValue:N0} Kč.");
        }

        int age = risk.Age;
        if (age < Rules.Common.MinAge || age > Rules.Common.MaxAge)
        {
            throw new BusinessException($"Věk žadatele musí být {Rules.Common.MinAge}–{Rules.Common.MaxAge} let.");
        }
    }

    public abstract decimal BaseAnnualPremium(RiskData risk);
}

public sealed class CarInsurance : InsuranceProduct
{
    private readonly IInsuranceVariant[] _variants;

    public CarInsurance(Rules rules)
        : base(rules)
    {
        _variants = new IInsuranceVariant[]
        {
            new FullCoverage(),
            new DeductibleCoverage(Rules),
            new LowMileageCoverage(Rules)
        };
    }

    public override string Id
    {
        get { return "car"; }
    }

    public override string Name
    {
        get { return "Pojištění auta"; }
    }

    public override string Description
    {
        get { return "Cena podle hodnoty auta, věku řidiče, kilometrů za rok a let bez nehody."; }
    }

    public override IReadOnlyList<IInsuranceVariant> Variants => _variants;

    // Валидация авто + расчёт базовой годовой премии (стоимость x ставка x факторы).
    public override void Validate(RiskData risk)
    {
        base.Validate(risk);

        // Дополнительные правила для авто: пределы пробега и лет без аварий.
        int kilometers = risk.AnnualKilometers;
        if (kilometers < 0 || kilometers > Rules.Car.MaxAnnualKm)
        {
            throw new BusinessException($"Nájezd musí být 0–{Rules.Car.MaxAnnualKm:N0} km/rok.");
        }

        // Лет без аварий ≤ возраст минус минимальный возраст страхователя.
        int accidentFreeYears = risk.AccidentFreeYears;
        int maxAccidentFreeYears = risk.Age - Rules.Common.MinAge;
        if (accidentFreeYears < 0 || accidentFreeYears > maxAccidentFreeYears)
        {
            throw new BusinessException($"Počet let bez nehody musí být 0 až věk minus {Rules.Common.MinAge}.");
        }
    }

    // Базовый расчёт авто: стоимость x ставка x возраст x пробег x бонус.
    public override decimal BaseAnnualPremium(RiskData risk)
    {
        // Возрастной фактор: молодые и пожилые дороже, промежуток = 1.
        decimal ageFactor; // <-- множитель, зависящий от возраста водителя
        if (risk.Age < Rules.Car.YoungAgeLimit)
        {
            ageFactor = Rules.Car.YoungFactor;
        }
        else if (risk.Age >= Rules.Car.SeniorAgeLimit)
        {
            ageFactor = Rules.Car.SeniorFactor;
        }
        else
        {
            ageFactor = 1m;
        }

        // Фактор пробега: малый снижает тариф, большой повышает.
        decimal mileageFactor; // <-- множитель, зависящий от годового пробега
        if (risk.AnnualKilometers <= Rules.Car.LowKmLimit)
        {
            mileageFactor = Rules.Car.LowKmFactor;
        }
        else if (risk.AnnualKilometers > Rules.Car.HighKmLimit)
        {
            mileageFactor = Rules.Car.HighKmFactor;
        }
        else
        {
            mileageFactor = 1m;
        }

        // Бонус за годы без аварий: cкидка за год, кап — MaxBonusYears.
        decimal cappedYears = Math.Min(risk.AccidentFreeYears, Rules.Car.MaxBonusYears); // <-- ограничение числа лет, учитываемых в бонусе
        decimal bonus = 1 - cappedYears * Rules.Car.BonusPerYear; // <-- суммарный бонус: из единицы вычитается скидка за годы

        // Формула (docs/RULES.md): стоимость × ставка × возраст × пробег × бонус.
        decimal result = risk.InsuredValue * Rules.Car.BaseRate * ageFactor * mileageFactor * bonus; // <-- итоговая базовая годовая премия
        return result;
    }
}

public sealed class PropertyInsurance : InsuranceProduct
{
    private readonly IInsuranceVariant[] _variants;

    public PropertyInsurance(Rules rules)
        : base(rules)
    {
        _variants = new IInsuranceVariant[]
        {
            new FullCoverage(),
            new DeductibleCoverage(Rules)
        };
    }

    public override string Id
    {
        get { return "property"; }
    }

    public override string Name
    {
        get { return "Pojištění majetku"; }
    }

    public override string Description
    {
        get { return "Cena podle hodnoty majetku, stáří budovy a zabezpečení."; }
    }

    public override IReadOnlyList<IInsuranceVariant> Variants => _variants;

    // Валидация недвижимости + расчёт базовой годовой премии (стоимость x ставка x факторы).
    public override void Validate(RiskData risk)
    {
        base.Validate(risk);

        // Дополнительное правило для недвижимости: предел возраста здания.
        int propertyAge = risk.PropertyAge;
        if (propertyAge < 0 || propertyAge > Rules.Property.MaxBuildingAge)
        {
            throw new BusinessException($"Stáří budovy musí být 0–{Rules.Property.MaxBuildingAge:N0} let.");
        }
    }

    // Базовый расчёт недвижимости: стоимость x ставка, затем наценки за возраст и скидка за защиту.
    public override decimal BaseAnnualPremium(RiskData risk)
    {
        // Базовая премия: страховая стоимость * базовая ставка.
        decimal result = risk.InsuredValue * Rules.Property.BaseRate;

        // Здание старше предела — наценка.
        if (risk.PropertyAge > Rules.Property.OldBuildingAge)
        {
            result *= Rules.Property.OldBuildingFactor; // <-- наценка за старую постройку
        }

        // Есть защита — скидка.
        if (risk.Secured)
        {
            result *= Rules.Property.SecuredFactor; // <-- скидка за защищённость объекта
        }

        return result;
    }
}

// PricingEngine — расчётный движок: продукты через DI (Program.cs), работа через интерфейс.
public sealed class PricingEngine
{
    private readonly Dictionary<string, IInsuranceProduct> _products;
    private readonly Rules _rules;

    // Словарь продуктов по id; дубликат id — ошибка конфигурации при старте.
    public PricingEngine(IEnumerable<IInsuranceProduct> products, Rules rules)
    {
        _products = products.ToDictionary(product => product.Id); // <-- построение словаря мгновенно выявляет дубликаты id
        _rules = rules;
    }

    // Плоские описания продуктов и вариантов для GUI (без реализаций).
    public IReadOnlyList<ProductDescription> Describe()
    {
        return _products.Values
            .Select(product => new ProductDescription(
                product.Id,
                product.Name,
                product.Description,
                product.Variants
                    .Select(variant => new VariantDescription(
                        variant.Id,
                        variant.Name,
                        variant.Description)) // <-- варианты превращаются в описания аналогично
                    .ToArray()))
            .ToArray();
    }

    // Поиск продукта и варианты по id; неизвестный id — бизнес-ошибка.
    private (IInsuranceProduct Product, IInsuranceVariant Variant) Resolve(string productId, string variantId)
    {
        if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(variantId))
        {
            throw new BusinessException("Zvolte druh a variantu pojištění.");
        }

        if (!_products.TryGetValue(productId, out var product))
        {
            throw new BusinessException("Neznámý druh pojištění.");
        }

        var variant = product.Variants.SingleOrDefault(v => v.Id == variantId);
        if (variant is null)
        {
            throw new BusinessException("Neznámá varianta pojištění.");
        }

        return (product, variant);
    }

    // Расчёт цены без изменения состояния (чистый: можно вызывать повторно).
    public Quote Quote(QuoteRequest request)
    {
        int years = request.Years;
        if (years < _rules.Common.MinYears || years > _rules.Common.MaxYears)
        {
            throw new BusinessException($"Doba pojištění musí být {_rules.Common.MinYears}–{_rules.Common.MaxYears} let.");
        }

        var risk = request.Risk;
        if (risk is null)
        {
            throw new BusinessException("Chybí údaje o riziku.");
        }

        var (product, variant) = Resolve(request.ProductId, request.VariantId);
        product.Validate(risk); // <-- валидация данных риска перед расчётом

        decimal basePremium = product.BaseAnnualPremium(risk);
        decimal annualPremium = variant.Premium(basePremium, risk);

        // Нижний предел цены и обязательное округление — предсказуемая сумма.
        decimal minPremium = _rules.Common.MinPremium;
        int roundDigits = _rules.Common.RoundDigits;
        annualPremium = Math.Max(minPremium, decimal.Round(annualPremium, roundDigits, MidpointRounding.AwayFromZero)); // <-- не ниже минимума и с фиксированным округлением

        decimal totalPremium = annualPremium * years; // <-- итоговая премия за весь срок
        return new Quote(annualPremium, totalPremium);
    }

    // Возмещение по стратегии варианты, зафиксированной в договоре.
    public decimal Compensation(Policy policy, decimal damage)
    {
        if (damage <= 0 || damage > _rules.Common.MaxDamage)
        {
            throw new BusinessException($"Škoda musí být 1 až {_rules.Common.MaxDamage:N0} Kč.");
        }

        // Нужна только варианта; продукт не используется.
        var (_, variant) = Resolve(policy.ProductId, policy.VariantId);

        decimal compensation = variant.Compensation(damage, policy.Risk);
        return decimal.Round(compensation, _rules.Common.RoundDigits, MidpointRounding.AwayFromZero); // <-- единообразное округление возмещения
    }
}