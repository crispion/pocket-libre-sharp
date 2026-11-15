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
/// BLE client for the Pocket recorder (PKT01).
///
/// The protocol is plain ASCII over a custom GATT service: the app sends "APP&amp;..."
/// and the device replies with "MCU&amp;..." as notifications. The device ignores all
/// commands until it has received the correct session key (APP&amp;SK&amp;&lt;16 characters&gt;).
/// The protocol is not officially documented but has been mapped by the
/// community projects private-pocket and pocket-libre.
/// </summary>
public sealed class PocketDevice : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = new("001120a0-2233-4455-6677-889912345678");
    public static readonly Guid CommandCharUuid = new("001120a2-2233-4455-6677-889912345678"); // write
    public static readonly Guid ReplyCharUuid = new("001120a3-2233-4455-6677-889912345678");   // write-no-response + notify

    public static readonly Guid DataCharUuid = new("001120a1-2233-4455-6677-889912345678");    // notify, raw MP3 data

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DataStallTimeout = TimeSpan.FromSeconds(15);

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _commandChar;
    private readonly GattCharacteristic _replyChar;
    private readonly GattCharacteristic? _dataChar;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();

    // Set only during an ongoing download; other data on a1 (e.g. live audio) is discarded.
    private readonly object _downloadLock = new();
    private MemoryStream? _download;

    /// <summary>Print all traffic (commands and replies) to the console.</summary>
    public bool Verbose { get; set; }

    public string Name => _device.Name;

    private PocketDevice(BluetoothLEDevice device, GattDeviceService service,
        GattCharacteristic commandChar, GattCharacteristic replyChar, GattCharacteristic? dataChar)
    {
        _device = device;
        _service = service;
        _commandChar = commandChar;
        _replyChar = replyChar;
        _dataChar = dataChar;
    }

    // ----------------------------------------------------------------- Scanning

    /// <summary>Scans for a device advertising a name that starts with "PKT01".</summary>
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

    // -------------------------------------------------------------- Connection

    /// <param name="address">Bluetooth address from <see cref="ScanAsync"/>.</param>
    /// <param name="writeToReplyChar">
    /// Send commands on ...a3 instead of ...a2. The manufacturer's app writes to a2,
    /// but pocket-libre uses a3 for both directions – try this if a2 gives no reply.
    /// </param>
    public static async Task<PocketDevice> ConnectAsync(ulong address, bool writeToReplyChar = false)
    {
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new PocketException("Could not open the Bluetooth device.");

        var services = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            device.Dispose();
            throw new PocketException(
                $"Pocket service not found ({services.Status}). The device accepts only one connection " +
                "at a time – close the Pocket app on the phone and try again.");
        }

        var service = services.Services[0];
        var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (chars.Status != GattCommunicationStatus.Success)
        {
            service.Dispose();
            device.Dispose();
            throw new PocketException($"Could not read characteristics ({chars.Status}).");
        }

        var reply = chars.Characteristics.FirstOrDefault(c => c.Uuid == ReplyCharUuid);
        var command = writeToReplyChar
            ? reply
            : chars.Characteristics.FirstOrDefault(c => c.Uuid == CommandCharUuid) ?? reply;
        if (reply is null || command is null)
        {
            service.Dispose();
            device.Dispose();
            throw new PocketException("The device is missing the expected characteristics (…a2/…a3).");
        }

        var data = chars.Characteristics.FirstOrDefault(c => c.Uuid == DataCharUuid);

        var pocket = new PocketDevice(device, service, command, reply, data);
        reply.ValueChanged += pocket.OnReply;

        var cccd = await reply.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
        {
            await pocket.DisposeAsync();
            throw new PocketException($"Could not enable notifications ({cccd}).");
        }

        if (data is not null)
        {
            data.ValueChanged += pocket.OnData;
            var dataCccd = await data.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (dataCccd != GattCommunicationStatus.Success)
            {
                await pocket.DisposeAsync();
                throw new PocketException($"Could not enable notifications for audio data ({dataCccd}).");
            }
        }

        return pocket;
    }

    private void OnData(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var bytes = e.CharacteristicValue.ToArray();
        lock (_downloadLock)
        {
            _download?.Write(bytes, 0, bytes.Length);
        }
    }

    private void OnReply(GattCharacteristic sender, GattValueChangedEventArgs e)
    {
        var text = Encoding.ASCII.GetString(e.CharacteristicValue.ToArray());

        // One message per notification is normal, but to be safe, split
        // on line breaks and on every new "MCU&" start.
        foreach (var part in Regex.Split(text, @"[\r\n\0]+|(?=MCU&)"))
        {
            var msg = part.Trim();
            if (msg.Length == 0) continue;
            if (Verbose) Console.WriteLine($"  <- {msg}");
            _incoming.Writer.TryWrite(msg);
        }
    }

    // ---------------------------------------------------------------- Commands

    private async Task SendAsync(string command)
    {
        if (Verbose) Console.WriteLine($"  -> {command}");

        var option = _commandChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write)
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var result = await _commandChar.WriteValueWithResultAsync(
            Encoding.ASCII.GetBytes(command).AsBuffer(), option);
        if (result.Status != GattCommunicationStatus.Success)
            throw new PocketException($"Could not send \"{command}\" ({result.Status}).");
    }

    /// <summary>Waits for the next message starting with any of the prefixes. Null on timeout.</summary>
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

    /// <summary>Sends a command and returns the field after the reply prefix, or null.</summary>
    private async Task<string?> QueryAsync(string command, string replyPrefix)
    {
        DrainIncoming();
        await SendAsync(command);
        var reply = await WaitForAsync(ReplyTimeout, replyPrefix);
        return reply?[replyPrefix.Length..];
    }

    /// <summary>
    /// Sends the session key. On a factory-reset device the first key received
    /// becomes the device's permanent key (pairing); otherwise it must match.
    /// </summary>
    public async Task AuthenticateAsync(string sessionKey)
    {
        DrainIncoming();
        await SendAsync($"APP&SK&{sessionKey}");

        var reply = await WaitForAsync(ReplyTimeout, "MCU&SK&");
        if (reply == "MCU&SK&OK") return;

        throw new PocketException(reply is null
            ? "No response to the session key. Either the key is wrong (the device then ignores everything) " +
              "or commands need to be sent on the other characteristic – try --a3."
            : $"The device rejected the session key ({reply}).");
    }

    public Task<string?> GetBatteryAsync() => QueryAsync("APP&BAT", "MCU&BAT&");

    public Task<string?> GetFirmwareAsync() => QueryAsync("APP&FW", "MCU&FW&");

    /// <summary>Dates (YYYY-MM-DD) that have recordings. Null if the device does not respond to the command.</summary>
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

    /// <summary>The files for a date (YYYY-MM-DD).</summary>
    public async Task<List<PocketFile>> ListFilesAsync(string date)
    {
        DrainIncoming();
        await SendAsync($"APP&LIST&{date}");

        var files = new List<PocketFile>();
        while (true)
        {
            // File lines: MCU&F&<date>&<file>&<seconds>   End marker: MCU&LIST&<count>
            var msg = await WaitForAsync(ReplyTimeout, "MCU&F&", "MCU&LIST&");
            if (msg is null || msg.StartsWith("MCU&LIST&", StringComparison.Ordinal)) return files;

            var fields = msg.Split('&');
            if (fields.Length < 4) continue;
            var seconds = fields.Length > 4 && int.TryParse(fields[4], out var s) ? s : 0;
            files.Add(new PocketFile(fields[2], fields[3], TimeSpan.FromSeconds(seconds)));
        }
    }

    /// <summary>
    /// Downloads a file (MP3). The device replies with the file size (MCU&amp;U&amp;&lt;bytes&gt;), then sends
    /// the raw data as notifications on ...a1 and finishes with MCU&amp;OFF.
    /// </summary>
    /// <returns>The received data and the size the device reported.</returns>
    public async Task<(byte[] Data, long ExpectedBytes)> DownloadAsync(
        string date, string fileName, IProgress<(long Received, long Total)>? progress = null)
    {
        if (_dataChar is null)
            throw new PocketException("The device is missing the audio data characteristic (…a1).");

        DrainIncoming();
        var buffer = new MemoryStream();
        lock (_downloadLock) _download = buffer;

        try
        {
            await SendAsync($"APP&U&{date}&{fileName}");

            var head = await WaitForAsync(ReplyTimeout, "MCU&U&");
            if (head is null)
                throw new PocketException("No response to the download request – check the date and file name.");
            if (!long.TryParse(head["MCU&U&".Length..], out var expected))
                throw new PocketException($"The device rejected the download ({head}).");

            var lastLength = -1L;
            var lastProgress = DateTime.UtcNow;
            DateTime? offSeenAt = null;

            while (true)
            {
                long length;
                lock (_downloadLock) length = buffer.Length;

                var now = DateTime.UtcNow;
                if (length != lastLength)
                {
                    lastLength = length;
                    lastProgress = now;
                    progress?.Report((length, expected));
                }

                if (expected > 0 && length >= expected) break;

                // MCU&OFF may arrive before the last data packets (different characteristics),
                // so give the data one more second after the end marker.
                if (offSeenAt is null)
                {
                    while (_incoming.Reader.TryRead(out var msg))
                        if (msg.StartsWith("MCU&OFF", StringComparison.Ordinal)) offSeenAt = now;
                }
                else if (now - offSeenAt > TimeSpan.FromSeconds(1))
                {
                    break;
                }

                if (now - lastProgress > DataStallTimeout)
                    throw new PocketException(
                        $"The download stalled after {length} of {expected} bytes.");

                await Task.Delay(50);
            }

            // Wait for the end marker so it does not interfere with the next command.
            if (offSeenAt is null) await WaitForAsync(TimeSpan.FromSeconds(2), "MCU&OFF");

            lock (_downloadLock) return (buffer.ToArray(), expected);
        }
        finally
        {
            lock (_downloadLock) _download = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _replyChar.ValueChanged -= OnReply;
        if (_dataChar is not null) _dataChar.ValueChanged -= OnData;
        _service.Dispose();
        _device.Dispose(); // disconnects the BLE link
        return ValueTask.CompletedTask;
    }
}

public sealed class PocketException(string message) : Exception(message);
