using System.Text.Json;

namespace Insurance.Core;

public interface IStateStore
{
    InsuranceState Read();
    T Update<T>(Func<InsuranceState, T> operation);
}

// JsonStateStore — состояние в JSON-файле; изменения под замком над копией.
public sealed class JsonStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly string? _path;
    private InsuranceState _state;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    // Конструктор: загружает состояние из файла или создаёт пустое.
    public JsonStateStore(string? path = null)
    {
        _path = path;

        // Загрузка из файла при старте; при первом запуске — пустое состояние.
        if (path is not null && File.Exists(path))
        {
            string json = File.ReadAllText(path);
            _state = JsonSerializer.Deserialize<InsuranceState>(json, Options)
                ?? throw new InvalidDataException("Prázdná databáze.");
        }
        else
        {
            _state = new InsuranceState();
        }
    }

    // Глубокая копия через JSON: независимая, без общих ссылок.
    private static InsuranceState CloneState(InsuranceState state)
    {
        string json = JsonSerializer.Serialize(state, Options);
        return JsonSerializer.Deserialize<InsuranceState>(json, Options)!;
    }

    // Чтение: всегда клон текущего состояния.
    public InsuranceState Read()
    {
        lock (_gate)
        {
            return CloneState(_state);
        }
    }

    // Единственный способ изменения: копия → операция → запись на диск → переключение памяти.
    public T Update<T>(Func<InsuranceState, T> operation)
    {
        lock (_gate)
        {
            var next = CloneState(_state);

            // Исключение операции → ничего не фиксируется (ни диск, ни память).
            T result = operation(next);

            if (_path is not null)
            {
                // Сначала диск, потом память, чтобы в памяти не было состояния без файла.
                string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
                Directory.CreateDirectory(directory);

                // Временный файл + Move = атомарная замена: читатель видит версию целиком.
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
                File.Move(temporary, _path, true);
            }

            _state = next;
            return result;
        }
    }
}

public interface IInsuranceLogger
{
    void Log(AuditEntry entry);
}

// Логгер аудита в консоль — для отладки.
public sealed class ConsoleInsuranceLogger : IInsuranceLogger
{
    // Пишет запись аудита в консоль.
    public void Log(AuditEntry entry)
    {
        // Аудит прямо на консоль — для отладки, для боевой — файл.
        Console.WriteLine($"[{entry.Date:yyyy-MM-dd}] {entry.Action}: {entry.Message}");
    }
}

// Файловый логгер: дописывает записи; замок от пересечения потоков.
public sealed class FileInsuranceLogger : IInsuranceLogger
{
    private readonly string _path;
    private readonly object _gate = new();

    // Конструктор: сохраняет путь к файлу лога.
    public FileInsuranceLogger(string path)
    {
        _path = path;
    }

    // Дописывает запись аудита в файл (одна запись = одна строка).
    public void Log(AuditEntry entry)
    {
        lock (_gate)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);

            // Переносы заменяются пробелами: одна запись = одна строка.
            string line = $"[{entry.Date:yyyy-MM-dd}] {entry.Action}: {entry.Message.ReplaceLineEndings(" ")}"; // <-- нормализация переносов строк
            using var writer = new StreamWriter(_path, append: true);
            writer.WriteLine(line);
        }
    }
}

// Составной логгер: передаёт запись всем вложенным (для "both").
public sealed class CompositeInsuranceLogger : IInsuranceLogger
{
    private readonly IInsuranceLogger[] _loggers;

    // Конструктор: принимает список вложенных логгеров.
    public CompositeInsuranceLogger(params IInsuranceLogger[] loggers)
    {
        _loggers = loggers;
    }

    // Передаёт запись всем вложенным логгерам.
    public void Log(AuditEntry entry)
    {
        foreach (var logger in _loggers)
        {
            logger.Log(entry);
        }
    }
}