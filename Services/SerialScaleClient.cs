using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
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
            // Throttle per-frame logging so a 10Hz stream doesn't drown the console,
            // and warn loudly when no frames arrive at all (silent feed = wrong port,
            // wrong baud rate, scale powered off, or wrong protocol).
            DateTime lastFrameLog = DateTime.MinValue;
            DateTime lastNoDataWarn = DateTime.MinValue;
            DateTime lastByteAt = DateTime.UtcNow;
            int silentTimeouts = 0;
            // After this long with no data, throw out the SerialPort so the outer
            // poller reopens it. Some USB-serial adapters get stuck and need a
            // fresh handle even though the OS still sees the device.
            var portResetAfter = TimeSpan.FromSeconds(30);

            while (!ct.IsCancellationRequested)
            {
                string line;
                try
                {
                    line = await Task.Run(() => ReadLineFlexible(port), ct);
                }
                catch (TimeoutException)
                {
                    silentTimeouts++;
                    var sinceData = DateTime.UtcNow - lastByteAt;
                    if (sinceData.TotalSeconds >= 5 && (DateTime.UtcNow - lastNoDataWarn).TotalSeconds >= 5)
                    {
                        _log.LogWarning(
                            "Scale '{ScaleId}' on {Port}: no data received in {Sec:0}s ({Timeouts} read timeouts). " +
                            "Check baud/parity, that the scale is powered on and streaming, and that you have the right COM port.",
                            scale.ScaleId, scale.SerialPortName, sinceData.TotalSeconds, silentTimeouts);
                        lastNoDataWarn = DateTime.UtcNow;
                    }
                    if (sinceData > portResetAfter)
                    {
                        // Force the outer poller to close + reopen the port. The
                        // OperationCanceledException check below leaves cancellation
                        // alone; this is a deliberate IO failure to trigger recovery.
                        throw new IOException(
                            $"No serial data on {scale.SerialPortName} for {sinceData.TotalSeconds:0}s — recycling port.");
                    }
                    continue;
                }

                lastByteAt = DateTime.UtcNow;
                silentTimeouts = 0;

                if (string.IsNullOrWhiteSpace(line)) continue;

                var frame = ParseIq355(line);
                frame.RawText = line.Replace("\n", "<LF>").Replace("\r", "<CR>");
                frame.RawHex = BitConverter.ToString(Encoding.ASCII.GetBytes(line));

                // Information-level so it's visible in production, but rate-limited to
                // ~1 per second so a continuous 10Hz stream doesn't spam the log.
                if ((DateTime.UtcNow - lastFrameLog).TotalMilliseconds >= 1000)
                {
                    _log.LogInformation(
                        "Scale '{ScaleId}' frame raw='{Raw}' hex={Hex} -> weight={W} motion={M} ok={Ok} status={S}",
                        scale.ScaleId, line, frame.RawHex, frame.Weight, frame.Motion, frame.Ok, frame.Status);
                    lastFrameLog = DateTime.UtcNow;
                }

                // Don't let a SignalR/network blip in the consumer kill the read loop.
                // The poller's job is to keep reading the scale; downstream errors get
                // logged and the next frame will be tried.
                try
                {
                    await onFrame(frame);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning("Scale '{ScaleId}' onFrame handler failed: {Msg}. Continuing read loop.",
                        scale.ScaleId, ex.Message);
                }
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
    ///   "-    20 LB G BZ "   below zero (sign in its own column)
    /// Status flags appear as 2-char tokens after the mode (G/N/T):
    ///   MO = motion, BZ = below zero, ZR = at zero,
    ///   OL = overload, UR = under-range, ER = indicator error.
    /// </summary>
    public static SerialFrame ParseIq355(string line)
    {
        var frame = new SerialFrame();

        var upper = line.ToUpperInvariant();
        var tokens = upper.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            frame.Ok = false;
            frame.Status = "No data";
            return frame;
        }

        // Status flags as discrete tokens (avoids substring false positives inside LB/KG/etc.)
        bool motion     = tokens.Any(t => t == "MO");
        bool belowZero  = tokens.Any(t => t == "BZ");
        bool atZero     = tokens.Any(t => t == "ZR");
        bool overload   = tokens.Any(t => t == "OL");
        bool underRange = tokens.Any(t => t == "UR");
        bool errorFlag  = tokens.Any(t => t == "ER");

        // The sign can appear in its own column with padding spaces between '-' and digits.
        int weightTokenIdx = 0;
        bool negative = false;
        if (tokens[0] == "-")
        {
            negative = true;
            weightTokenIdx = 1;
        }

        if (weightTokenIdx >= tokens.Length || !int.TryParse(tokens[weightTokenIdx], out int weight))
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }
        if (negative) weight = -weight;

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
        for (int i = weightTokenIdx + 1; i < tokens.Length; i++)
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

        // Map IQ355 flags to the user-facing status. BZ and ZR are informational —
        // the reading is still valid, so ok stays true.
        if (overload)        { frame.Ok = false; frame.Status = "Overload"; }
        else if (underRange) { frame.Ok = false; frame.Status = "Under-Range"; }
        else if (errorFlag)  { frame.Ok = false; frame.Status = "Indicator Error"; }
        else if (belowZero)  { frame.Ok = true;  frame.Status = motion ? "Motion" : "Below Zero"; }
        else if (atZero)     { frame.Ok = true;  frame.Status = motion ? "Motion" : ">0<"; }
        else if (motion)     { frame.Ok = true;  frame.Status = "Motion"; }
        else                 { frame.Ok = true;  frame.Status = "Ok"; }

        return frame;
    }

    /// <summary>
    /// On-Demand serial loop: opens the port, then in a loop sends the request command
    /// (e.g. "Gross\r"), reads the response until CR or timeout, parses, and emits
    /// each frame to onFrame. Like ReadStreamAsync, throws if the port faults so the
    /// outer poller can recycle it.
    /// </summary>
    public async Task PollOnDemandAsync(
        ScaleConfigEntity scale,
        string requestCommand,
        int pollIntervalMs,
        Func<SerialFrame, Task> onFrame,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(scale.SerialPortName))
            throw new InvalidOperationException($"Scale '{scale.ScaleId}' has ConnectionType=Serial but SerialPortName is empty.");
        if (string.IsNullOrWhiteSpace(requestCommand))
            throw new InvalidOperationException($"Scale '{scale.ScaleId}' is OnDemand but no Request Command is set (e.g. 'Gross\\r').");

        using var port = new SerialPort(
            scale.SerialPortName,
            scale.BaudRate > 0 ? scale.BaudRate : 9600,
            ParseParity(scale.Parity),
            scale.DataBits > 0 ? scale.DataBits : 8,
            ParseStopBits(scale.StopBits))
        {
            ReadTimeout = scale.TimeoutMs > 0 ? scale.TimeoutMs : 1000,
            WriteTimeout = scale.TimeoutMs > 0 ? scale.TimeoutMs : 1000,
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true
        };

        port.Open();
        _log.LogInformation(
            "Serial port {Port} opened ({Baud},{Bits},{Par},{Stop}) for scale '{ScaleId}' (OnDemand, cmd='{Cmd}').",
            scale.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, scale.ScaleId,
            requestCommand.Replace("\r", "\\r").Replace("\n", "\\n"));

        var requestBytes = BuildRequestBytes(requestCommand);
        var pollDelay = pollIntervalMs > 0 ? pollIntervalMs : 500;
        DateTime lastFrameLog = DateTime.MinValue;
        DateTime lastByteAt = DateTime.UtcNow;
        DateTime lastNoDataWarn = DateTime.MinValue;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Drain anything stale before each request so we don't read an old frame.
                    if (port.BytesToRead > 0)
                    {
                        try { port.DiscardInBuffer(); } catch { /* ignore */ }
                    }

                    await Task.Run(() => port.Write(requestBytes, 0, requestBytes.Length), ct);

                    var response = await Task.Run(() => ReadUntilCr(port, scale.TimeoutMs > 0 ? scale.TimeoutMs : 1000), ct);

                    if (string.IsNullOrEmpty(response))
                    {
                        var sinceData = DateTime.UtcNow - lastByteAt;
                        if (sinceData.TotalSeconds >= 5 && (DateTime.UtcNow - lastNoDataWarn).TotalSeconds >= 5)
                        {
                            _log.LogWarning(
                                "Scale '{ScaleId}' on {Port}: sent '{Cmd}' but got no reply in {Sec:0}s. " +
                                "Check baud/parity, that the scale answers to this command, and that you have the right COM port.",
                                scale.ScaleId, scale.SerialPortName,
                                requestCommand.Replace("\r", "\\r").Replace("\n", "\\n"),
                                sinceData.TotalSeconds);
                            lastNoDataWarn = DateTime.UtcNow;
                        }
                    }
                    else
                    {
                        lastByteAt = DateTime.UtcNow;
                        var frame = ParseOnDemand(response);
                        frame.RawText = response.Replace("\n", "<LF>").Replace("\r", "<CR>");
                        frame.RawHex = BitConverter.ToString(Encoding.ASCII.GetBytes(response));

                        if ((DateTime.UtcNow - lastFrameLog).TotalMilliseconds >= 1000)
                        {
                            _log.LogInformation(
                                "Scale '{ScaleId}' OnDemand frame raw='{Raw}' hex={Hex} -> weight={W} motion={M} ok={Ok} status={S}",
                                scale.ScaleId, response, frame.RawHex, frame.Weight, frame.Motion, frame.Ok, frame.Status);
                            lastFrameLog = DateTime.UtcNow;
                        }

                        try
                        {
                            await onFrame(frame);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            _log.LogWarning("Scale '{ScaleId}' onFrame handler failed: {Msg}. Continuing.", scale.ScaleId, ex.Message);
                        }
                    }
                }
                catch (TimeoutException) { /* fall through to next poll */ }

                try { await Task.Delay(pollDelay, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { if (port.IsOpen) port.Close(); }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Reads bytes from the port until a CR (0x0D) is seen or timeoutMs elapses.
    /// Strips leading/trailing CR/LF/NUL on the way out.
    /// </summary>
    private static string ReadUntilCr(SerialPort port, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var sb = new StringBuilder();
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                int b = port.ReadByte();
                if (b < 0) break;
                sb.Append((char)b);
                if (b == 0x0D) break;
            }
            catch (TimeoutException) { break; }
        }
        return sb.ToString().Trim('\r', '\n', '\0');
    }

    /// <summary>
    /// Parses the Cardinal On-Demand reply:
    ///   <s><r><n><m><f><xxxxxx.xxx><uuu>
    /// where s = status flag, r = range digit, n = mode (G/T/N), m = motion (M or space),
    /// f = custom flag, xxxxxx.xxx = signed weight, uuu = units (lb/kg/ton/...).
    /// </summary>
    private static readonly Regex OnDemandRegex = new(
        @"([ ZOEe])([1-9 ])([GTN])([M ])(.)\s*([+-]?\d+\.\d+)\s*(ton|lb|oz|t|kg|g)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SerialFrame ParseOnDemand(string raw)
    {
        var frame = new SerialFrame { RawText = raw };
        var m = OnDemandRegex.Match(raw);
        if (!m.Success)
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }

        char statusFlag = m.Groups[1].Value[0];
        char mode       = m.Groups[3].Value[0];
        char motionChar = m.Groups[4].Value[0];
        double weightDecimal = double.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture);

        // Round to whole pounds for the wire format used by the rest of the system.
        frame.Weight = (int)Math.Round(weightDecimal);
        frame.Motion = motionChar == 'M' || motionChar == 'm';

        // Map status flag to friendly text. 'e' = weight not currently displayed (treat as error).
        switch (statusFlag)
        {
            case 'O': frame.Ok = false; frame.Status = "Overload"; break;
            case 'E': frame.Ok = false; frame.Status = "Indicator Error"; break;
            case 'e': frame.Ok = false; frame.Status = "Weight Not Displayed"; break;
            case 'Z': frame.Ok = true;  frame.Status = frame.Motion ? "Motion" : ">0<"; break;
            default:  frame.Ok = true;  frame.Status = frame.Motion ? "Motion" : "Ok"; break;
        }

        if (mode != 'G' && frame.Ok)
        {
            // Non-gross frames are valid but flagged so the caller can decide.
            frame.Status = $"{frame.Status} ({mode})";
        }
        return frame;
    }

    /// <summary>
    /// Converts a request-command string from the UI (e.g. "Gross\r") into bytes,
    /// interpreting "\r" as CR and "\n" as LF the same way the TCP path does.
    /// </summary>
    private static byte[] BuildRequestBytes(string s)
    {
        s = s.Replace("\\r", "\r").Replace("\\n", "\n");
        return Encoding.ASCII.GetBytes(s);
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
