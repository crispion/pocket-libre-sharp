using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PocketLister;

public sealed record PocketFile(string Date, string Name, TimeSpan Duration);

/// <summary>
/// BLE-klient för Pocket-inspelaren (PKT01).
///
/// Protokollet är ren ASCII över en egen GATT-tjänst: appen skickar "APP&amp;..."
/// och enheten svarar med "MCU&amp;..." som notifieringar. Enheten ignorerar alla
/// kommandon tills den fått rätt sessionsnyckel (APP&amp;SK&amp;&lt;16 tecken&gt;).
/// Protokollet är inte officiellt dokumenterat utan kartlagt av
/// community-projekten private-pocket och pocket-libre.
/// </summary>
public sealed class PocketDevice : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = new("001120a0-2233-4455-6677-889912345678");
    public static readonly Guid CommandCharUuid = new("001120a2-2233-4455-6677-889912345678"); // write
    public static readonly Guid ReplyCharUuid = new("001120a3-2233-4455-6677-889912345678");   // write-no-response + notify

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _commandChar;
    private readonly GattCharacteristic _replyChar;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

    /// <summary>Skriv ut all trafik (kommandon och svar) på konsolen.</summary>
    public bool Verbose { get; set; }

    public string Name => _device.Name;

    private PocketDevice(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic commandChar, GattCharacteristic replyChar)
    {
        _device = device;
        _service = service;
        _commandChar = commandChar;
        _replyChar = replyChar;
    }

    // ---------------------------------------------------------------- Sökning

    /// <summary>Söker efter en enhet som annonserar ett namn som börjar med "PKT01".</summary>
    public static async Task<(ulong Address, string Name)?> ScanAsync(TimeSpan timeout)
    {
        var found = new TaskCompletionSource<(ulong, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        watcher.Received += (_, e) =>
        {
            var name = e.Advertisement.LocalName;
            if (!string.IsNullOrEmpty(name) && name.StartsWith("PKT01", StringComparison.OrdinalIgnoreCase))
                found.TrySetResult((e.BluetoothAddress, name));
        };

        watcher.Start();
        try
        {
            var winner = await Task.WhenAny(found.Task, Task.Delay(timeout));
            return winner == found.Task ? found.Task.Result : null;
        }
        finally
        {
            watcher.Stop();
        }
    }

    // -------------------------------------------------------------- Anslutning

    /// <param name="address">Bluetooth-adress från <see cref="ScanAsync"/>.</param>
    /// <param name="writeToReplyChar">
    /// Skicka kommandon på ...a3 i stället för ...a2. Tillverkarens app skriver till a2,
    /// men pocket-libre använder a3 för båda riktningarna – prova detta om a2 inte ger svar.
    /// </param>
    public static async Task<PocketDevice> ConnectAsync(ulong address, bool writeToReplyChar = false)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new PocketException("Kunde inte öppna Bluetooth-enheten.");

        var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            device.Dispose();
            throw new PocketException(
                $"Hittade inte Pocket-tjänsten ({services.Status}). Enheten tar bara emot en anslutning " +
                "åt gången – stäng Pocket-appen på telefonen och försök igen.");
        }

        var service = services.Services[0];
        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success)
        {
            service.Dispose();
            device.Dispose();
            throw new PocketException($"Kunde inte läsa karakteristiker ({chars.Status}).");
        }

        var reply = chars.Characteristics.FirstOrDefault(c => c.Uuid == ReplyCharUuid);
        var command = writeToReplyChar
            ? reply
            : chars.Characteristics.FirstOrDefault(c => c.Uuid == CommandCharUuid) ?? reply;
        if (reply is null || command is null)
        {
            service.Dispose();
            device.Dispose();
            throw new PocketException("Enheten saknar de förväntade karakteristikerna (…a2/…a3).");
        }

        var pocket = new PocketDevice(device, service, command, reply);
        reply.ValueChanged += pocket.OnReply;

        var cccd = await reply.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
        {
            await pocket.DisposeAsync();
            throw new PocketException($"Kunde inte slå på notifieringar ({cccd}).");
        }

        return pocket;
    }

    private void OnReply(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var text = Encoding.ASCII.GetString(e.CharacteristicValue.ToArray());

        // Ett meddelande per notifiering är det normala, men dela för säkerhets skull
        // på radbrytningar och på varje ny "MCU&"-start.
        foreach (var part in Regex.Split(text, @"[\r\n\0]+|(?=MCU&)"))
        {
            var msg = part.Trim();
            if (msg.Length == 0) continue;
            if (Verbose) Console.WriteLine($"  <- {msg}");
            _incoming.Writer.TryWrite(msg);
        }
    }

    // -------------------------------------------------------------- Kommandon

    private async Task SendAsync(string command)
    {
        if (Verbose) Console.WriteLine($"  -> {command}");

        var option = _commandChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var result = await _commandChar.WriteValueWithResultAsync(
            Encoding.ASCII.GetBytes(command).AsBuffer(), option);
        if (result.Status != GattCommunicationStatus.Success)
            throw new PocketException($"Kunde inte skicka \"{command}\" ({result.Status}).");
    }

    /// <summary>Väntar på nästa meddelande som börjar med något av prefixen. Null vid timeout.</summary>
    private async Task<string?> WaitForAsync(TimeSpan timeout, params string[] prefixes)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var msg = await _incoming.Reader.ReadAsync(cts.Token);
                if (prefixes.Any(p => msg.StartsWith(p, StringComparison.Ordinal)))
                    return msg;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private void DrainIncoming()
    {
        while (_incoming.Reader.TryRead(out _)) { }
    }

    /// <summary>Skickar ett kommando och returnerar fältet efter svarsprefixet, eller null.</summary>
    private async Task<string?> QueryAsync(string command, string replyPrefix)
    {
        DrainIncoming();
        await SendAsync(command);
        var reply = await WaitForAsync(ReplyTimeout, replyPrefix);
        return reply?[replyPrefix.Length..];
    }

    /// <summary>
    /// Skickar sessionsnyckeln. På en fabriksåterställd enhet blir den första nyckeln som
    /// tas emot enhetens permanenta nyckel (parkoppling); annars måste den matcha.
    /// </summary>
    public async Task AuthenticateAsync(string sessionKey)
    {
        DrainIncoming();
        await SendAsync($"APP&SK&{sessionKey}");

        var reply = await WaitForAsync(ReplyTimeout, "MCU&SK&");
        if (reply == "MCU&SK&OK") return;

        throw new PocketException(reply is null
            ? "Inget svar på sessionsnyckeln. Antingen är nyckeln fel (enheten ignorerar då allt) " +
              "eller så behöver kommandon skickas på den andra karakteristiken – prova --a3."
            : $"Enheten avvisade sessionsnyckeln ({reply}).");
    }

    public Task<string?> GetBatteryAsync() => QueryAsync("APP&BAT", "MCU&BAT&");

    public Task<string?> GetFirmwareAsync() => QueryAsync("APP&FW", "MCU&FW&");

    /// <summary>Datum (YYYY-MM-DD) som har inspelningar. Null om enheten inte svarar på kommandot.</summary>
    public async Task<List<string>?> ListDatesAsync()
    {
        DrainIncoming();
        await SendAsync("APP&LIST_DIRS");

        var dates = new List<string>();
        while (true)
        {
            var msg = await WaitForAsync(ReplyTimeout, "MCU&DIRS&", "MCU&DIRS_SUM&");
            if (msg is null) return dates.Count > 0 ? dates : null;
            if (msg.StartsWith("MCU&DIRS_SUM&", StringComparison.Ordinal)) return dates;
            dates.Add(msg["MCU&DIRS&".Length..]);
        }
    }

    /// <summary>Filerna för ett datum (YYYY-MM-DD).</summary>
    public async Task<List<PocketFile>> ListFilesAsync(string date)
    {
        DrainIncoming();
        await SendAsync($"APP&LIST&{date}");

        var files = new List<PocketFile>();
        while (true)
        {
            // Filrader: MCU&F&<datum>&<fil>&<sekunder>   Slutmarkör: MCU&LIST&<antal>
            var msg = await WaitForAsync(ReplyTimeout, "MCU&F&", "MCU&LIST&");
            if (msg is null || msg.StartsWith("MCU&LIST&", StringComparison.Ordinal)) return files;

            var fields = msg.Split('&');
            if (fields.Length < 4) continue;
            var seconds = fields.Length > 4 && int.TryParse(fields[4], out var s) ? s : 0;
            files.Add(new PocketFile(fields[2], fields[3], TimeSpan.FromSeconds(seconds)));
        }
    }

    public ValueTask DisposeAsync()
    {
        _replyChar.ValueChanged -= OnReply;
        _service.Dispose();
        _device.Dispose(); // kopplar ner BLE-länken
        return ValueTask.CompletedTask;
    }
}

public sealed class PocketException(string message) : Exception(message);
