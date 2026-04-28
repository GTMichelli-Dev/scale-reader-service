using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using ScaleReaderService.Models;

namespace ScaleReaderService.Services;

/// <summary>
/// Reads continuous-stream weight data from a scale indicator over RS-232 serial.
/// Currently parses Cardinal IQ355 format:
///
///   "   8980 LB G    "   (stable, gross)
///   "   8860 LB G MO "   (in motion, gross)
///
///   Layout (whitespace-padded, terminated by CR/LF):
///     [weight (digits, right-justified)] [unit (LB/KG)] [mode (G/N/T)] [motion (MO or blank)]
///
/// The reader holds the port open and emits each parsed frame via a callback. The caller is
/// responsible for throttling/forwarding readings (e.g. via PollingIntervalMs).
/// </summary>
public sealed class SerialScaleClient
{
    private readonly ILogger<SerialScaleClient> _log;

    public SerialScaleClient(ILogger<SerialScaleClient> log)
    {
        _log = log;
    }

    /// <summary>
    /// Opens the configured serial port and reads frames in a loop, invoking <paramref name="onFrame"/>
    /// for each parsed reading. Returns when <paramref name="ct"/> is cancelled or the port faults.
    /// </summary>
    public async Task ReadStreamAsync(
        ScaleConfigEntity scale,
        Func<SerialFrame, Task> onFrame,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scale.SerialPortName))
            throw new InvalidOperationException($"Scale '{scale.ScaleId}' has ConnectionType=Serial but SerialPortName is empty.");

        using var port = new SerialPort(
            scale.SerialPortName,
            scale.BaudRate > 0 ? scale.BaudRate : 9600,
            ParseParity(scale.Parity),
            scale.DataBits > 0 ? scale.DataBits : 8,
            ParseStopBits(scale.StopBits))
        {
            ReadTimeout = scale.TimeoutMs > 0 ? scale.TimeoutMs : 2000,
            WriteTimeout = scale.TimeoutMs > 0 ? scale.TimeoutMs : 2000,
            NewLine = "\r",
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true
        };

        port.Open();
        _log.LogInformation(
            "Serial port {Port} opened ({Baud},{Bits},{Par},{Stop}) for scale '{ScaleId}'",
            scale.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, scale.ScaleId);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await Task.Run(() => ReadLineFlexible(port), ct);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                var frame = ParseIq355(line);
                frame.RawText = line.Replace("\n", "<LF>").Replace("\r", "<CR>");
                frame.RawHex = BitConverter.ToString(Encoding.ASCII.GetBytes(line));
                _log.LogDebug("Serial frame raw='{Raw}' parsed weight={W} motion={M} ok={Ok} status={S}",
                    line, frame.Weight, frame.Motion, frame.Ok, frame.Status);

                await onFrame(frame);
            }
        }
        finally
        {
            try { if (port.IsOpen) port.Close(); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// SerialPort.ReadLine only splits on NewLine (CR). Some indicators send CR+LF or LF+CR. Strip
    /// both ends so the parser sees a clean frame regardless of line-ending convention.
    /// </summary>
    private static string ReadLineFlexible(SerialPort port)
    {
        string raw = port.ReadLine();
        return raw.Trim('\r', '\n', '\0', '\x02');
    }

    /// <summary>
    /// Parses an IQ355 frame. Format observed on Cardinal 225 Navigator:
    ///   "   8980 LB G    "   stable
    ///   "   8860 LB G MO "   in motion
    /// First whitespace-separated token is the integer weight; remaining tokens carry units, mode,
    /// and an optional "MO" motion flag.
    /// </summary>
    public static SerialFrame ParseIq355(string line)
    {
        var frame = new SerialFrame();

        var upper = line.ToUpperInvariant();
        bool motion = upper.Contains("MO");

        var tokens = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            frame.Ok = false;
            frame.Status = "No data";
            return frame;
        }

        if (!int.TryParse(tokens[0], out int weight))
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }

        bool hasLb = tokens.Any(t => t == "LB");
        bool hasKg = tokens.Any(t => t == "KG");
        if (!hasLb && !hasKg)
        {
            frame.Weight = weight;
            frame.Motion = motion;
            frame.Ok = false;
            frame.Status = "Wrong Units";
            return frame;
        }

        char mode = ' ';
        for (int i = 1; i < tokens.Length; i++)
        {
            if (tokens[i] is "G" or "N" or "T") { mode = tokens[i][0]; break; }
        }

        if (mode != 'G')
        {
            frame.Weight = weight;
            frame.Motion = motion;
            frame.Ok = false;
            frame.Status = "Not Gross Mode";
            return frame;
        }

        frame.Weight = weight;
        frame.Motion = motion;
        frame.Ok = true;
        frame.Status = motion ? "Motion" : "Ok";
        return frame;
    }

    private static Parity ParseParity(string? value) => value?.ToUpperInvariant() switch
    {
        "EVEN" => System.IO.Ports.Parity.Even,
        "ODD" => System.IO.Ports.Parity.Odd,
        "MARK" => System.IO.Ports.Parity.Mark,
        "SPACE" => System.IO.Ports.Parity.Space,
        _ => System.IO.Ports.Parity.None,
    };

    private static StopBits ParseStopBits(int value) => value switch
    {
        2 => System.IO.Ports.StopBits.Two,
        0 => System.IO.Ports.StopBits.OnePointFive,
        _ => System.IO.Ports.StopBits.One,
    };
}

public sealed class SerialFrame
{
    public int Weight { get; set; }
    public bool Motion { get; set; }
    public bool Ok { get; set; }
    public string Status { get; set; } = "";
    public string RawText { get; set; } = "";
    public string RawHex { get; set; } = "";
}
