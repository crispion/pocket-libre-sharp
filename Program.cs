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
        "download" => await DownloadAsync(),
        _ => Usage(),
    };
}
catch (PocketException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// ------------------------------------------------------------------- Commands

async Task<int> PairAsync()
{
    var config = Config.Load();
    if (config is not null && keyArg is null && !force)
    {
        Console.WriteLine("A key is already saved. Run with --force to pair again");
        Console.WriteLine("(the device must then be factory reset), or run 'list' directly.");
        return 1;
    }

    // No --key: generate a new key for a factory-reset device.
    // With --key: use an existing key (e.g. the one the Pocket app already uses).
    var isNewKey = keyArg is null;
    var key = keyArg ?? RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 16);
    if (key.Length != 16 || !key.All(char.IsAsciiLetterOrDigit))
        throw new PocketException("The session key must be exactly 16 letters/digits.");

    if (isNewKey)
    {
        Console.WriteLine("Reset the device first: triple-click the side button until the light blinks red,");
        Console.WriteLine("then hold the button until it pulses blue. Press Enter when done.");
        Console.ReadLine();
    }

    var (address, name) = await FindDeviceAsync();

    // Save the key BEFORE sending it: the device may disconnect before the reply arrives,
    // and the key must not be lost then.
    new Config(key, address).Save();

    await using var pocket = await PocketDevice.ConnectAsync(address, useA3);
    pocket.Verbose = verbose;
    await pocket.AuthenticateAsync(key);

    Console.WriteLine($"Paired with {name}.");
    Console.WriteLine($"The key is saved in {Config.FilePath}");
    if (isNewKey)
        Console.WriteLine("The device disconnects after pairing – wait a few seconds, then run 'list'.");
    return 0;
}

async Task<int> ListAsync()
{
    var config = Config.Load()
        ?? throw new PocketException("No saved key. Run 'PocketLister pair' first.");

    var (address, name) = await FindDeviceAsync();
    await using var pocket = await PocketDevice.ConnectAsync(address, useA3);
    pocket.Verbose = verbose;
    await pocket.AuthenticateAsync(config.Key);

    var battery = await pocket.GetBatteryAsync();
    var firmware = await pocket.GetFirmwareAsync();
    Console.WriteLine($"{name}  ·  battery {battery ?? "?"} %  ·  firmware {firmware ?? "?"}");
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
            // Older firmware may lack LIST_DIRS – then ask day by day, 30 days back.
            Console.WriteLine("(The device did not respond to LIST_DIRS – checking the last 30 days.)");
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
    Console.WriteLine(total == 0 ? "No files found." : $"{total} files in total.");
    return 0;
}

async Task<int> DownloadAsync()
{
    var fileArg = OptionValue("--file");
    if (dateArg is null || fileArg is null)
        throw new PocketException("Specify both --date and --file (see 'list' for dates and file names).");

    var config = Config.Load()
        ?? throw new PocketException("No saved key. Run 'PocketLister pair' first.");

    // Default: save in the current folder. --out can be a folder or a full file name.
    var localName = Path.GetFileName(fileArg);
    if (!Path.HasExtension(localName)) localName += ".mp3";
    var outArg = OptionValue("--out");
    var target = outArg is null ? localName
        : Directory.Exists(outArg) ? Path.Combine(outArg, localName)
        : outArg;

    var (address, _) = await FindDeviceAsync();
    await using var pocket = await PocketDevice.ConnectAsync(address, useA3);
    pocket.Verbose = verbose;
    await pocket.AuthenticateAsync(config.Key);

    Console.WriteLine($"Downloading {dateArg}/{fileArg}…");
    var started = DateTime.UtcNow;
    var lastShown = DateTime.MinValue;
    var progress = new SyncProgress(p =>
    {
        // Update the line at most four times per second.
        if (verbose || (DateTime.UtcNow - lastShown).TotalMilliseconds < 250) return;
        lastShown = DateTime.UtcNow;
        var percent = p.Total > 0 ? 100.0 * p.Received / p.Total : 0;
        Console.Write($"\r  {p.Received / 1024.0:F0} / {p.Total / 1024.0:F0} kB  ({percent:F0} %)   ");
    });

    var (data, expected) = await pocket.DownloadAsync(dateArg, fileArg, progress);
    await File.WriteAllBytesAsync(target, data);

    var seconds = Math.Max((DateTime.UtcNow - started).TotalSeconds, 0.1);
    Console.WriteLine();
    Console.WriteLine($"Saved {data.Length} bytes to {Path.GetFullPath(target)} " +
                      $"({data.Length / 1024.0 / seconds:F1} kB/s).");

    if (expected > 0 && data.Length != expected)
    {
        Console.WriteLine($"Warning: the device reported {expected} bytes – the file may be incomplete.");
        return 2;
    }
    return 0;
}

// ---------------------------------------------------------------------- Helpers

async Task<(ulong Address, string Name)> FindDeviceAsync()
{
    Console.WriteLine("Searching for Pocket device (PKT01…)…");
    var found = await PocketDevice.ScanAsync(TimeSpan.FromSeconds(20))
        ?? throw new PocketException(
            "No device found. Make sure it is on, that Bluetooth is enabled on the computer " +
            "and that the Pocket app on the phone is fully closed (the device accepts only one connection at a time).");
    Console.WriteLine($"Found {found.Name}.");
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
        PocketLister – pair with a Pocket recorder and list the files on it.

          PocketLister pair                Pair a factory-reset device with a new key
          PocketLister pair --key <key>    Use an existing 16-character session key
          PocketLister list                List all files on the device
          PocketLister list --date 2026-11-14
          PocketLister download --date 2026-11-14 --file <filename> [--out <folder or file>]

        Flags:
          --verbose   Show all BLE traffic
          --a3        Send commands on characteristic …a3 instead of …a2
          --force     Overwrite the saved key on 'pair'
        """);
    return 1;
}

/// <summary>Invokes the callback directly on the calling thread (Progress&lt;T&gt; queues on the thread pool).</summary>
internal sealed class SyncProgress(Action<(long Received, long Total)> report)
    : IProgress<(long Received, long Total)>
{
    public void Report((long Received, long Total) value) => report(value);
}

// ------------------------------------------------------------- Configuration

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
