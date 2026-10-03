using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

string dataPath = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), "hotel-data.json");

var storage = new JsonHotelStorage(dataPath);
bool firstRun = !storage.Exists;

HotelData data;
try
{
    data = storage.Load();
}
catch (InvalidDataException ex)
{
    string backupPath = dataPath + ".corrupt";
    File.Move(dataPath, backupPath, overwrite: true);
    Console.WriteLine(ex.Message);
    Console.WriteLine($"Старий файл збережено як {backupPath}. Починаємо з порожнього готелю.");
    Console.WriteLine();
    data = new HotelData();
    firstRun = true;
}

var hotel = new HotelService(data);

if (firstRun && hotel.GetRooms().Count == 0)
{
    hotel.AddRoom(101, 2, 1200m);
    hotel.AddRoom(102, 2, 1500m);
    hotel.AddRoom(201, 3, 2200m);
    hotel.AddRoom(301, 4, 3500m);
    storage.Save(hotel.Snapshot());
    Console.WriteLine("Перший запуск: додано 4 демонстраційні номери (101, 102, 201, 301).");
    Console.WriteLine($"Дані зберігаються у файлі: {dataPath}");
    Console.WriteLine();
}

new ConsoleApp(hotel, storage).Run();

class Room
{
    public int RoomNumber { get; set; }

    public int Capacity { get; set; }

    public decimal PricePerNight { get; set; }
}

class Booking
{
    public int Id { get; set; }
    public int RoomNumber { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int Guests { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    public decimal TotalPrice { get; set; }

    [JsonIgnore]
    public int Nights => EndDate.DayNumber - StartDate.DayNumber;

    public bool OverlapsWith(DateOnly start, DateOnly end) => StartDate < end && start < EndDate;
}

class HotelData
{
    public List<Room> Rooms { get; set; } = new();
    public List<Booking> Bookings { get; set; } = new();
    public int NextBookingId { get; set; } = 1;
}


sealed record OperationResult(bool Success, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

sealed record OperationResult<T>(bool Success, string Message, T? Value = default)
{
    public static OperationResult<T> Ok(T value, string message) => new(true, message, value);
    public static OperationResult<T> Fail(string message) => new(false, message);
}


class HotelService
{
    private readonly Dictionary<int, Room> _rooms;
    private readonly List<Booking> _bookings;
    private readonly Func<DateOnly> _today;
    private int _nextBookingId;

    public HotelService(HotelData? data = null, Func<DateOnly>? today = null)
    {
        data ??= new HotelData();
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));

        _rooms = data.Rooms
            .GroupBy(r => r.RoomNumber)
            .ToDictionary(g => g.Key, g => g.First());
        _bookings = data.Bookings.ToList();

        int maxId = _bookings.Count == 0 ? 0 : _bookings.Max(b => b.Id);
        _nextBookingId = Math.Max(data.NextBookingId, maxId + 1);
    }


    public OperationResult AddRoom(int roomNumber, int capacity, decimal pricePerNight)
    {
        if (roomNumber <= 0)
            return OperationResult.Fail("Номер кімнати має бути додатним числом.");
        if (capacity <= 0)
            return OperationResult.Fail("Місткість має бути не менше 1 гостя.");
        if (pricePerNight <= 0)
            return OperationResult.Fail("Ціна за ніч має бути більшою за нуль.");
        if (_rooms.ContainsKey(roomNumber))
            return OperationResult.Fail($"Номер {roomNumber} уже існує.");

        _rooms.Add(roomNumber, new Room
        {
            RoomNumber = roomNumber,
            Capacity = capacity,
            PricePerNight = pricePerNight
        });
        return OperationResult.Ok($"Номер {roomNumber} додано.");
    }

    public OperationResult RemoveRoom(int roomNumber)
    {
        if (!_rooms.ContainsKey(roomNumber))
            return OperationResult.Fail($"Номера {roomNumber} не існує.");

        var today = _today();
        if (_bookings.Any(b => b.RoomNumber == roomNumber && b.EndDate > today))
            return OperationResult.Fail(
                $"Номер {roomNumber} має поточні або майбутні бронювання. Спочатку скасуйте їх.");

        _rooms.Remove(roomNumber);
        _bookings.RemoveAll(b => b.RoomNumber == roomNumber);
        return OperationResult.Ok($"Номер {roomNumber} видалено.");
    }

    public Room? GetRoom(int roomNumber) =>
        _rooms.TryGetValue(roomNumber, out var room) ? room : null;

    public IReadOnlyList<Room> GetRooms() =>
        _rooms.Values.OrderBy(r => r.RoomNumber).ToList();

    public bool IsRoomOccupied(int roomNumber, DateOnly date) =>
        _bookings.Any(b => b.RoomNumber == roomNumber && b.StartDate <= date && date < b.EndDate);


    public OperationResult<Booking> BookRoom(
        int roomNumber, string? clientName, DateOnly startDate, DateOnly endDate, int guests)
    {
        if (!_rooms.TryGetValue(roomNumber, out var room))
            return OperationResult<Booking>.Fail($"Номера {roomNumber} не існує.");

        clientName = clientName?.Trim();
        string? error = ValidateBookingRequest(room, clientName, startDate, endDate, guests);
        if (error is not null)
            return OperationResult<Booking>.Fail(error);

        var conflict = FindConflict(roomNumber, startDate, endDate);
        if (conflict is not null)
            return OperationResult<Booking>.Fail(
                $"Номер {roomNumber} уже заброньовано з {conflict.StartDate:dd.MM.yyyy} " +
                $"по {conflict.EndDate:dd.MM.yyyy}.");

        var booking = CreateBooking(room, clientName!, startDate, endDate, guests);
        _bookings.Add(booking);

        return OperationResult<Booking>.Ok(booking,
            $"Бронювання №{booking.Id}: номер {roomNumber}, {booking.ClientName}, " +
            $"{startDate:dd.MM.yyyy}–{endDate:dd.MM.yyyy} ({booking.Nights} ноч.), " +
            $"разом {booking.TotalPrice:N2} грн.");
    }

    private string? ValidateBookingRequest(
        Room room, string? clientName, DateOnly startDate, DateOnly endDate, int guests)
    {
        if (string.IsNullOrEmpty(clientName))
            return "Вкажіть ім'я клієнта.";
        if (startDate >= endDate)
            return "Дата виїзду має бути пізніше за дату заселення (мінімум 1 ніч).";
        if (startDate < _today())
            return "Не можна бронювати на минулі дати.";
        if (guests <= 0)
            return "Кількість гостей має бути не менше 1.";
        if (guests > room.Capacity)
            return $"Номер {room.RoomNumber} вміщує максимум {room.Capacity} гост.";
        return null;
    }

    private Booking? FindConflict(int roomNumber, DateOnly startDate, DateOnly endDate) =>
        _bookings.FirstOrDefault(b => b.RoomNumber == roomNumber && b.OverlapsWith(startDate, endDate));

    private Booking CreateBooking(
        Room room, string clientName, DateOnly startDate, DateOnly endDate, int guests) =>
        new()
        {
            Id = _nextBookingId++,
            RoomNumber = room.RoomNumber,
            ClientName = clientName,
            Guests = guests,
            StartDate = startDate,
            EndDate = endDate,
            TotalPrice = room.PricePerNight * (endDate.DayNumber - startDate.DayNumber)
        };

    public OperationResult CancelBooking(int bookingId)
    {
        var booking = _bookings.FirstOrDefault(b => b.Id == bookingId);
        if (booking is null)
            return OperationResult.Fail($"Бронювання №{bookingId} не знайдено.");

        if (booking.EndDate <= _today())
            return OperationResult.Fail($"Бронювання №{bookingId} уже завершилося, скасувати його не можна.");

        _bookings.Remove(booking);
        return OperationResult.Ok($"Бронювання №{bookingId} скасовано.");
    }

    public IReadOnlyList<Booking> GetBookings() =>
        _bookings.OrderBy(b => b.StartDate).ThenBy(b => b.Id).ToList();

    public IReadOnlyList<Booking> GetBookingsByRoom(int roomNumber) =>
        GetBookings().Where(b => b.RoomNumber == roomNumber).ToList();

    public IReadOnlyList<Booking> GetBookingsByClient(string? namePart)
    {
        if (string.IsNullOrWhiteSpace(namePart))
            return Array.Empty<Booking>();

        return GetBookings()
            .Where(b => b.ClientName.Contains(namePart.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public OperationResult<IReadOnlyList<Room>> FindAvailableRooms(
        DateOnly startDate, DateOnly endDate, int minCapacity = 1, decimal? maxPricePerNight = null)
    {
        if (startDate >= endDate)
            return OperationResult<IReadOnlyList<Room>>.Fail(
                "Дата виїзду має бути пізніше за дату заселення.");

        if (startDate < _today())
            return OperationResult<IReadOnlyList<Room>>.Fail("Не можна шукати номери на минулі дати.");

        var available = _rooms.Values
            .Where(r => r.Capacity >= minCapacity)
            .Where(r => maxPricePerNight is null || r.PricePerNight <= maxPricePerNight)
            .Where(r => !_bookings.Any(b => b.RoomNumber == r.RoomNumber && b.OverlapsWith(startDate, endDate)))
            .OrderBy(r => r.PricePerNight)
            .ThenBy(r => r.RoomNumber)
            .ToList();

        return OperationResult<IReadOnlyList<Room>>.Ok(available, $"Знайдено номерів: {available.Count}.");
    }

    public OperationResult<decimal> CalculatePrice(int roomNumber, DateOnly startDate, DateOnly endDate)
    {
        if (!_rooms.TryGetValue(roomNumber, out var room))
            return OperationResult<decimal>.Fail($"Номера {roomNumber} не існує.");

        if (startDate >= endDate)
            return OperationResult<decimal>.Fail("Дата виїзду має бути пізніше за дату заселення.");

        int nights = endDate.DayNumber - startDate.DayNumber;
        decimal total = room.PricePerNight * nights;
        return OperationResult<decimal>.Ok(total, $"{nights} ноч. × {room.PricePerNight:N2} = {total:N2} грн.");
    }


    public HotelData Snapshot() => new()
    {
        Rooms = _rooms.Values.OrderBy(r => r.RoomNumber).ToList(),
        Bookings = _bookings.OrderBy(b => b.Id).ToList(),
        NextBookingId = _nextBookingId
    };
}


class JsonHotelStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public JsonHotelStorage(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    public bool Exists => File.Exists(FilePath);

    public HotelData Load()
    {
        if (!File.Exists(FilePath))
            return new HotelData();

        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<HotelData>(json, Options)
                   ?? throw new InvalidDataException("Файл даних порожній.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Файл даних пошкоджений: " + ex.Message, ex);
        }
    }

    public void Save(HotelData data)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(FilePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data, Options));
        File.Move(tempPath, FilePath, overwrite: true);
    }
}


class ConsoleApp
{
    private const string DateFormat = "dd.MM.yyyy";

    private readonly HotelService _hotel;
    private readonly JsonHotelStorage _storage;

    public ConsoleApp(HotelService hotel, JsonHotelStorage storage)
    {
        _hotel = hotel;
        _storage = storage;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public void Run()
    {
        while (true)
        {
            PrintMenu();
            string? choice = Prompt("Ваш вибір");
            if (choice is null) return;
            Console.WriteLine();

            switch (choice)
            {
                case "1": ShowRooms(); break;
                case "2": AddRoom(); break;
                case "3": RemoveRoom(); break;
                case "4": FindAvailableRooms(); break;
                case "5": BookRoom(); break;
                case "6": CalculatePrice(); break;
                case "7": PrintBookings(_hotel.GetBookings(), "Бронювань ще немає."); break;
                case "8": ShowBookingsByRoom(); break;
                case "9": ShowBookingsByClient(); break;
                case "10": CancelBooking(); break;
                case "0": return;
                default: Warn("Невідомий пункт меню."); break;
            }

            Console.WriteLine();
        }
    }


    private static void PrintMenu()
    {
        Console.WriteLine("=== Готель: керування бронюваннями ===");
        Console.WriteLine(" 1. Показати всі номери");
        Console.WriteLine(" 2. Додати номер");
        Console.WriteLine(" 3. Видалити номер");
        Console.WriteLine(" 4. Знайти вільні номери");
        Console.WriteLine(" 5. Забронювати номер");
        Console.WriteLine(" 6. Розрахувати вартість проживання");
        Console.WriteLine(" 7. Усі бронювання");
        Console.WriteLine(" 8. Бронювання за номером");
        Console.WriteLine(" 9. Бронювання за клієнтом");
        Console.WriteLine("10. Скасувати бронювання");
        Console.WriteLine(" 0. Вихід");
    }

    private void ShowRooms()
    {
        var rooms = _hotel.GetRooms();
        if (rooms.Count == 0)
        {
            Info("Номерів ще немає. Додайте їх через пункт 2.");
            return;
        }

        Console.WriteLine($"{"Номер",6} {"Місць",6} {"Ціна/ніч",14}  Сьогодні");
        foreach (var room in rooms)
        {
            string status = _hotel.IsRoomOccupied(room.RoomNumber, Today) ? "зайнятий" : "вільний";
            Console.WriteLine($"{room.RoomNumber,6} {room.Capacity,6} {Money(room.PricePerNight),14}  {status}");
        }
    }

    private void AddRoom()
    {
        int? number = ReadInt("Номер кімнати");
        if (number is null) return;
        int? capacity = ReadInt("Місткість (кількість гостей)", 1, 50);
        if (capacity is null) return;
        decimal? price = ReadDecimal("Ціна за ніч, грн");
        if (price is null) return;

        Report(_hotel.AddRoom(number.Value, capacity.Value, price.Value));
    }

    private void RemoveRoom()
    {
        int? number = ReadInt("Номер кімнати для видалення");
        if (number is null) return;
        if (!Confirm($"Видалити номер {number.Value}?")) return;

        Report(_hotel.RemoveRoom(number.Value));
    }

    private void FindAvailableRooms()
    {
        var range = ReadDateRange();
        if (range is null) return;
        int? guests = ReadInt("Кількість гостей", 1, 100);
        if (guests is null) return;
        decimal? maxPrice = ReadOptionalDecimal("Макс. ціна за ніч, грн (Enter — без обмеження)");

        var (start, end) = range.Value;
        var result = _hotel.FindAvailableRooms(start, end, guests.Value, maxPrice);
        if (!result.Success)
        {
            Error(result.Message);
            return;
        }

        var rooms = result.Value!;
        if (rooms.Count == 0)
        {
            Info("На ці дати вільних номерів немає.");
            return;
        }

        int nights = end.DayNumber - start.DayNumber;
        string totalHeader = $"Разом за {nights} ноч.";
        Console.WriteLine($"{"Номер",6} {"Місць",6} {"Ціна/ніч",14} {totalHeader,20}");
        foreach (var room in rooms)
        {
            Console.WriteLine(
                $"{room.RoomNumber,6} {room.Capacity,6} {Money(room.PricePerNight),14} {Money(room.PricePerNight * nights),20}");
        }
    }

    private void BookRoom()
    {
        int? number = ReadInt("Номер кімнати");
        if (number is null) return;
        string? client = ReadText("Ім'я клієнта");
        if (client is null) return;
        var range = ReadDateRange();
        if (range is null) return;
        int? guests = ReadInt("Кількість гостей", 1, 100);
        if (guests is null) return;

        var result = _hotel.BookRoom(number.Value, client, range.Value.Start, range.Value.End, guests.Value);
        Report(result.Success, result.Message);
    }

    private void CalculatePrice()
    {
        int? number = ReadInt("Номер кімнати");
        if (number is null) return;
        var range = ReadDateRange();
        if (range is null) return;

        var result = _hotel.CalculatePrice(number.Value, range.Value.Start, range.Value.End);
        if (result.Success) Info(result.Message);
        else Error(result.Message);
    }

    private void ShowBookingsByRoom()
    {
        int? number = ReadInt("Номер кімнати");
        if (number is null) return;
        if (_hotel.GetRoom(number.Value) is null)
        {
            Error($"Номера {number.Value} не існує.");
            return;
        }

        PrintBookings(_hotel.GetBookingsByRoom(number.Value), "Для цього номера бронювань немає.");
    }

    private void ShowBookingsByClient()
    {
        string? name = ReadText("Ім'я клієнта (або його частина)");
        if (name is null) return;

        PrintBookings(_hotel.GetBookingsByClient(name), "Бронювань для такого клієнта не знайдено.");
    }

    private void CancelBooking()
    {
        int? id = ReadInt("Номер бронювання (ID)");
        if (id is null) return;
        if (!Confirm($"Скасувати бронювання №{id.Value}?")) return;

        Report(_hotel.CancelBooking(id.Value));
    }

    private static void PrintBookings(IReadOnlyList<Booking> bookings, string emptyMessage)
    {
        if (bookings.Count == 0)
        {
            Info(emptyMessage);
            return;
        }

        Console.WriteLine(
            $"{"ID",4} {"Номер",6} {"Клієнт",-20} {"Гостей",6} {"Заселення",11} {"Виїзд",11} {"Ночей",6} {"Сума",14}");
        foreach (var b in bookings)
        {
            Console.WriteLine(
                $"{b.Id,4} {b.RoomNumber,6} {Truncate(b.ClientName, 20),-20} {b.Guests,6} " +
                $"{b.StartDate.ToString(DateFormat, CultureInfo.InvariantCulture),11} " +
                $"{b.EndDate.ToString(DateFormat, CultureInfo.InvariantCulture),11} " +
                $"{b.Nights,6} {Money(b.TotalPrice),14}");
        }
    }


    private void Report(OperationResult result) => Report(result.Success, result.Message);

    private void Report(bool success, string message)
    {
        if (!success)
        {
            Error(message);
            return;
        }

        Success(message);
        try
        {
            _storage.Save(_hotel.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error($"Зміну виконано, але зберегти файл не вдалося: {ex.Message}");
        }
    }

    private static void Success(string message) => WriteColored(message, ConsoleColor.Green);
    private static void Warn(string message) => WriteColored(message, ConsoleColor.Yellow);
    private static void Error(string message) => WriteColored(message, ConsoleColor.Red);
    private static void Info(string message) => Console.WriteLine(message);

    private static void WriteColored(string message, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = previous;
    }


    private static string? Prompt(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine()?.Trim();
    }

    private static string? ReadText(string label)
    {
        string? text = Prompt($"{label} (Enter — скасувати)");
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static int? ReadInt(string label, int min = 1, int max = int.MaxValue)
    {
        while (true)
        {
            string? text = Prompt($"{label} (Enter — скасувати)");
            if (string.IsNullOrEmpty(text)) return null;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && value >= min && value <= max)
                return value;

            string range = max == int.MaxValue ? $"від {min}" : $"від {min} до {max}";
            Warn($"Введіть ціле число {range}.");
        }
    }

    private static decimal? ReadDecimal(string label)
    {
        while (true)
        {
            string? text = Prompt($"{label} (Enter — скасувати)");
            if (string.IsNullOrEmpty(text)) return null;

            if (TryParsePositiveDecimal(text, out decimal value))
                return value;

            Warn("Введіть додатне число, наприклад 1500 або 1499.50.");
        }
    }

    private static decimal? ReadOptionalDecimal(string label)
    {
        while (true)
        {
            string? text = Prompt(label);
            if (string.IsNullOrEmpty(text)) return null;

            if (TryParsePositiveDecimal(text, out decimal value))
                return value;

            Warn("Введіть додатне число або натисніть Enter.");
        }
    }

    private static bool TryParsePositiveDecimal(string text, out decimal value)
    {
        string normalized = text.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    private static DateOnly? ReadDate(string label)
    {
        while (true)
        {
            string? text = Prompt($"{label} (дд.мм.рррр, Enter — скасувати)");
            if (string.IsNullOrEmpty(text)) return null;

            if (DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out DateOnly date))
                return date;

            string example = Today.AddDays(1).ToString(DateFormat, CultureInfo.InvariantCulture);
            Warn($"Невірна дата. Формат: {DateFormat}, наприклад {example}.");
        }
    }

    private static (DateOnly Start, DateOnly End)? ReadDateRange()
    {
        DateOnly? start = ReadDate("Дата заселення");
        if (start is null) return null;
        DateOnly? end = ReadDate("Дата виїзду");
        if (end is null) return null;
        return (start.Value, end.Value);
    }

    private static bool Confirm(string question)
    {
        string? answer = Prompt($"{question} (т/н)");
        return answer is not null &&
               (answer.Equals("т", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("так", StringComparison.OrdinalIgnoreCase));
    }


    private static string Money(decimal value) =>
        value.ToString("N2", CultureInfo.CurrentCulture) + " грн";

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";
}
