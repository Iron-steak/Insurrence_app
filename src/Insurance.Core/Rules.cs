namespace Insurance.Core;

// Rules — параметры тарифов: из appsettings.json, здесь дефолты. Правка цены = без пересборки.
public sealed class Rules
{
    public RulesCommon Common { get; set; } = new();
    public RulesCar Car { get; set; } = new();
    public RulesProperty Property { get; set; } = new();
    public RulesDeductible Deductible { get; set; } = new();
    public RulesLowMileage LowMileage { get; set; } = new();
    public RulesService Service { get; set; } = new();

    // Общие ограничения, действующие для всех продуктов.
    public sealed class RulesCommon
    {
        public decimal MaxInsuredValue { get; set; } = 100000000m; // верхний предел страховой стоимости
        public int MinAge { get; set; } = 18;                       // минимальный возраст страхователя
        public int MaxAge { get; set; } = 100;                      // максимальный возраст страхователя
        public int MinYears { get; set; } = 1;                      // минимальный срок договора, лет
        public int MaxYears { get; set; } = 10;                     // максимальный срок договора, лет
        public decimal MinPremium { get; set; } = 1m;               // нижний предел годовой премии
        public decimal MaxDamage { get; set; } = 100000000m;        // верхний предел заявляемого ущерба
        public int MaxTextLength { get; set; } = 500;               // максимальная длина текстовых полей
        public int RoundDigits { get; set; } = 2;                   // количество знаков после запятой при округлении
    }

    // Тарифы автострахования.
    public sealed class RulesCar
    {
        public decimal BaseRate { get; set; } = 0.04m;        // базовая ставка от страховой стоимости
        public int YoungAgeLimit { get; set; } = 25;           // возраст, ниже которого применяется молодёжный фактор
        public decimal YoungFactor { get; set; } = 1.4m;       // повышающий фактор для молодых водителей
        public int SeniorAgeLimit { get; set; } = 70;          // возраст, с которого применяется пожилой фактор
        public decimal SeniorFactor { get; set; } = 1.2m;      // повышающий фактор для пожилых водителей
        public int LowKmLimit { get; set; } = 10000;           // пробег до которого действует низкий фактор
        public decimal LowKmFactor { get; set; } = 0.9m;       // понижающий фактор малого пробега
        public int HighKmLimit { get; set; } = 25000;          // пробег после которого действует высокий фактор
        public decimal HighKmFactor { get; set; } = 1.2m;      // повышающий фактор большого пробега
        public int MaxBonusYears { get; set; } = 10;           // предельное число лет, учитываемых в бонусе
        public decimal BonusPerYear { get; set; } = 0.03m;     // скидка за один год без аварий
        public int MaxAnnualKm { get; set; } = 200000;         // верхний предел годового пробега
    }

    // Тарифы страхования недвижимости.
    public sealed class RulesProperty
    {
        public decimal BaseRate { get; set; } = 0.005m;     // базовая ставка от страховой стоимости
        public int OldBuildingAge { get; set; } = 50;        // возраст, после которого применяется наценка
        public decimal OldBuildingFactor { get; set; } = 1.3m; // повышающий фактор для старых зданий
        public decimal SecuredFactor { get; set; } = 0.85m;  // понижающий фактор при наличии защиты
        public int MaxBuildingAge { get; set; } = 300;       // верхний предел возраста здания
    }

    // Параметры варианты с франшизой.
    public sealed class RulesDeductible
    {
        public decimal Discount { get; set; } = 0.8m;   // доля премии при выборе варианты (0.8 = скидка 20 %)
        public decimal Percent { get; set; } = 0.05m;   // доля ущерба, относимая на франшизу
        public decimal FixedMin { get; set; } = 5000m;  // минимальная франшиза в деньгах
    }

    // Параметры варианты "малый пробег".
    public sealed class RulesLowMileage
    {
        public int MaxKm { get; set; } = 10000;   // лимит пробега для получения скидки
        public decimal Discount { get; set; } = 0.75m; // доля премии при выборе варианты (0.75 = скидка 25 %)
    }

    // Ограничения, связанные с симуляцией и продлением.
    public sealed class RulesService
    {
        public int MaxMonths { get; set; } = 120;               // максимальный шаг сдвига времени в месяцах
        public int MaxSimulationYear { get; set; } = 2190;      // конечный год симуляции
        public int LookaheadMonths { get; set; } = 1;           // период до истечения договора, в котором создаётся предложение
        public int MaxRiskAge { get; set; } = 100;              // предел возраста риска при продлении
        public int MaxRiskBuildingAge { get; set; } = 300;      // предел возраста здания при продлении
        public decimal RenewalInflation { get; set; } = 1.03m;  // тариф инфляции при продлении (3 %)
        public decimal MaxApprovedPrice { get; set; } = 100000000m; // верхний предел утверждаемой цены
    }
}