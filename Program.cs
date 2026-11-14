using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PocketLister;

Console.OutputEncoding = Encoding.UTF8;

var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "list";
var verbose = args.Contains("--verbose") || args.Contains("-v");
var useA3 = args.Contains("--a3");
var force = args.Contains("--force");
var keyArg = OptionValue("--key");
var dateArg = OptionValue("--date");

try
{
    return command switch
    {
        "pair" => await PairAsync(),
        "list" => await ListAsync(),
        _ => Usage(),
    };
}
catch (PocketException ex)
{
    Console.Error.WriteLine($"Fel: {ex.Message}");
    return 1;
}

// ------------------------------------------------------------------ Kommandon

async Task<int> PairAsync()
{
    var config = Config.Load();
    if (config is not null && keyArg is null && !force)
    {
        Console.WriteLine("Det finns redan en sparad nyckel. Kör med --force för att parkoppla om");
        Console.WriteLine("(enheten måste då vara fabriksåterställd), eller kör 'list' direkt.");
        return 1;
    }

    // Ingen --key: generera en ny nyckel för en fabriksåterställd enhet.
    // Med --key: använd en befintlig nyckel (t.ex. den som Pocket-appen redan använder).
    var isNewKey = keyArg is null;
    var key = keyArg ?? RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 16);
    if (key.Length != 16 || !key.All(char.IsAsciiLetterOrDigit))
        throw new PocketException("Sessionsnyckeln måste vara exakt 16 bokstäver/siffror.");

    if (isNewKey)
    {
        Console.WriteLine("Återställ enheten först: trippelklicka på sidoknappen tills lampan blinkar rött,");
        Console.WriteLine("håll sedan in knappen tills den pulserar blått. Tryck Enter när det är klart.");
        Console.ReadLine();
    }

    var (address, name) = await FindDeviceAsync();

    // Spara nyckeln INNAN den skickas: enheten kan hinna koppla ner innan svaret kommer,
    // och då får nyckeln inte gå förlorad.
    new Config(key, address).Save();

    await using var pocket = await PocketDevice.ConnectAsync(address, useA3);
    pocket.Verbose = verbose;
    await pocket.AuthenticateAsync(key);

    Console.WriteLine($"Parkopplad med {name}.");
    Console.WriteLine($"Nyckeln är sparad i {Config.FilePath}");
    if (isNewKey)
        Console.WriteLine("Enheten kopplar ner efter parkopplingen – vänta ett par sekunder och kör sedan 'list'.");
    return 0;
}

async Task<int> ListAsync()
{
    var config = Config.Load()
        ?? throw new PocketException("Ingen sparad nyckel. Kör 'PocketLister pair' först.");

    var (address, name) = await FindDeviceAsync();
    await using var pocket = await PocketDevice.ConnectAsync(address, useA3);
    pocket.Verbose = verbose;
    await pocket.AuthenticateAsync(config.Key);

    var battery = await pocket.GetBatteryAsync();
    var firmware = await pocket.GetFirmwareAsync();
    Console.WriteLine($"{name}  ·  batteri {battery ?? "?"} %  ·  firmware {firmware ?? "?"}");
    Console.WriteLine();

    List<string> dates;
    if (dateArg is not null)
    {
        dates = [dateArg];
    }
    else
    {
        var fromDevice = await pocket.ListDatesAsync();
        if (fromDevice is null)
        {
            // Äldre firmware kan sakna LIST_DIRS – fråga då dag för dag, 30 dagar bakåt.
            Console.WriteLine("(Enheten svarade inte på LIST_DIRS – kontrollerar de senaste 30 dagarna.)");
            fromDevice = Enumerable.Range(0, 30)
                .Select(i => DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"))
                .ToList();
        }
        dates = fromDevice;
    }

    var total = 0;
    foreach (var date in dates.Order())
    {
        var files = await pocket.ListFilesAsync(date);
        if (files.Count == 0) continue;

        Console.WriteLine(date);
        foreach (var file in files)
            Console.WriteLine($"  {file.Name,-28} {file.Duration:hh\\:mm\\:ss}");
        total += files.Count;
    }

    Console.WriteLine();
    Console.WriteLine(total == 0 ? "Inga filer hittades." : $"{total} filer totalt.");
    return 0;
}

// --------------------------------------------------------------------- Hjälp

async Task<(ulong Address, string Name)> FindDeviceAsync()
{
    Console.WriteLine("Söker efter Pocket-enhet (PKT01…)…");
    var found = await PocketDevice.ScanAsync(TimeSpan.FromSeconds(20))
        ?? throw new PocketException(
            "Hittade ingen enhet. Kontrollera att den är på, att Bluetooth är aktiverat på datorn " +
            "och att Pocket-appen på telefonen är helt stängd (enheten tar bara en anslutning åt gången).");
    Console.WriteLine($"Hittade {found.Name}.");
    return found;
}

string? OptionValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int Usage()
{
    Console.WriteLine("""
        PocketLister – parkoppla med en Pocket-inspelare och lista filerna på den.

          PocketLister pair                Parkoppla en fabriksåterställd enhet med en ny nyckel
          PocketLister pair --key <nyckel> Använd en befintlig 16-teckens sessionsnyckel
          PocketLister list                Lista alla filer på enheten
          PocketLister list --date 2026-11-14

        Flaggor:
          --verbose   Visa all BLE-trafik
          --a3        Skicka kommandon på karakteristik …a3 i stället för …a2
          --force     Skriv över sparad nyckel vid 'pair'
        """);
    return 1;
}

// ------------------------------------------------------------- Konfiguration

internal sealed record Config(string Key, ulong Address)
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketLister", "config.json");

    public static Config? Load() =>
        File.Exists(FilePath) ? JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) : null;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
