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
    private readonly BrandsCache _brands;

    public SerialScaleClient(ILogger<SerialScaleClient> log, BrandsCache brands)
    {
        _log = log;
        _brands = brands;
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
        using var port = BuildPort(scale, defaultTimeoutMs: 2000, setNewLine: true);

        port.Open();
        _log.LogInformation(
            "Serial port {Port} opened ({Baud},{Bits},{Par},{Stop}) for scale '{ScaleId}'",
            scale.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, scale.ScaleId);

        // Compile the brand-specific weight regex once per connect cycle.
        // When set, ParseSerialFrame tries it before any built-in parser so
        // future scale types can be onboarded by editing scale-models.json
        // alone — no code change in this service. Falls back to built-in
        // formats (Rice Lake IQ plus 355, then Cardinal IQ355) when null
        // or when the brand isn't found / its regex is malformed.
        var brandRegex = ResolveBrandRegex(scale);

        // Operator-configured column positions, when this scale has them. Resolved once
        // per connect cycle alongside the regex; null leaves the brand/built-in path intact.
        var positions = ScaleFormatDetector.PositionTokens.From(scale);
        if (positions.HasValue)
            _log.LogInformation(
                "Scale '{ScaleId}' parsing by column positions: weight {Start}..{End}, motion '{Char}' @ {Motion}",
                scale.ScaleId, positions.Value.WeightStart, positions.Value.WeightEnd,
                positions.Value.MotionChar, positions.Value.MotionIndex);

        try
        {
            // Throttle per-frame logging so a 10Hz stream doesn't drown the console,
            // and warn loudly when no frames arrive at all (silent feed = wrong port,
            // wrong baud rate, scale powered off, or wrong protocol).
            DateTime lastFrameLog = DateTime.MinValue;
            DateTime lastNoDataWarn = DateTime.MinValue;
            DateTime lastByteAt = DateTime.UtcNow;
            // After this long with no data, throw out the SerialPort so the outer
            // poller reopens it. Some USB-serial adapters get stuck and need a
            // fresh handle even though the OS still sees the device.
            var portResetAfter = TimeSpan.FromSeconds(30);

            var buffer = new byte[ReadBufferBytes];
            var pending = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                int read = DrainAvailable(port, buffer);
                if (read == 0)
                {
                    var sinceData = DateTime.UtcNow - lastByteAt;
                    if (sinceData.TotalSeconds >= 5 && (DateTime.UtcNow - lastNoDataWarn).TotalSeconds >= 5)
                    {
                        _log.LogWarning(
                            "Scale '{ScaleId}' on {Port}: no data received in {Sec:0}s. " +
                            "Check baud/parity, that the scale is powered on and streaming, and that you have the right COM port.",
                            scale.ScaleId, scale.SerialPortName, sinceData.TotalSeconds);
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

                    // The only await in the loop, and it happens with no read in
                    // flight — so cancellation always unwinds to a quiet port.
                    await Task.Delay(PollDelayMs, ct);
                    continue;
                }

                lastByteAt = DateTime.UtcNow;
                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

                var lines = TakeLines(pending);

                // Whatever survives framing is a partial frame awaiting its terminator.
                // Once that runs past the cap the stream isn't CR/LF framed at all —
                // usually the wrong baud rate producing garbage — so drop it instead of
                // buffering forever. Checked after framing so a large burst that does
                // contain terminators still yields its frames.
                if (pending.Length > MaxPendingChars)
                {
                    _log.LogWarning(
                        "Scale '{ScaleId}' on {Port}: {Count} bytes with no CR/LF terminator — discarding. " +
                        "This usually means the baud rate, data bits or parity is wrong.",
                        scale.ScaleId, scale.SerialPortName, pending.Length);
                    pending.Clear();
                }

                foreach (var line in lines)
                {
                    var frame = ParseSerialFrame(line, brandRegex, positions);
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
        }
        finally
        {
            try { if (port.IsOpen) port.Close(); }
            catch { /* ignore */ }
        }
    }

    /// <summary>How long to idle between polls when the port has no bytes waiting.</summary>
    private const int PollDelayMs = 20;

    /// <summary>Size of the scratch buffer each read loop drains the port into.</summary>
    private const int ReadBufferBytes = 1024;

    /// <summary>Unterminated bytes we'll hold before deciding the framing is wrong.</summary>
    private const int MaxPendingChars = 4096;

    /// <summary>
    /// Takes whatever bytes are already buffered on the port, and only those.
    /// Returns 0 when nothing is waiting.
    ///
    /// This never blocks, which is the whole point: the previous implementation
    /// blocked in SerialPort.ReadLine on a thread-pool thread via Task.Run, and
    /// cancelling the token only abandoned the await — it left that thread parked
    /// inside the read while the finally below closed and disposed the port.
    /// Closing a handle with a read still in flight wedges some USB-serial
    /// drivers (CH340 especially) hard enough that every later open fails with
    /// "A device attached to the system is not functioning" until the adapter is
    /// physically re-plugged. Polling BytesToRead instead means cancellation is
    /// only ever observed between reads, so the port is always quiet when it closes.
    /// </summary>
    private static int DrainAvailable(SerialPort port, byte[] buffer)
    {
        int waiting = port.BytesToRead;
        if (waiting <= 0) return 0;
        return port.Read(buffer, 0, Math.Min(buffer.Length, waiting));
    }

    /// <summary>
    /// Pulls the complete lines out of <paramref name="pending"/>, leaving any partial
    /// trailing frame behind for the next read to finish. Splits on CR and LF alike, so
    /// CR, LF, CRLF and LFCR indicators all frame correctly — this replaces
    /// ReadLineFlexible, which could only split on the single configured NewLine.
    /// Blank lines are dropped; STX and NUL padding is trimmed off each frame.
    /// </summary>
    private static List<string> TakeLines(StringBuilder pending)
    {
        var lines = new List<string>();
        string text = pending.ToString();

        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n') continue;

            string line = text[start..i].Trim('\r', '\n', '\0', '\x02');
            if (line.Length > 0) lines.Add(line);
            start = i + 1;
        }

        pending.Clear();
        pending.Append(text[start..]);
        return lines;
    }

    // Rice Lake 920i / IQ plus 355 EDP/PRN continuous stream:
    //   <POL><WEIGHT><UNIT><MODE><STATUS>     e.g. "   12426LG "
    // No spaces between the adjacent fields (unlike Cardinal IQ355), so
    // the Cardinal token-splitter can't parse it. Also used by Condec UMC
    // and other relabels of the same Rice Lake board.
    //   POL    : ' ' positive, '-' negative, '^' overload, ']' under-range
    //   WEIGHT : digits, optionally with decimal, leading-space suppression
    //   UNIT   : L=lb K=kg T=ton G=g O=oz
    //   MODE   : G=gross N=net T=tare
    //   STATUS : ' ' valid, I=invalid, M=motion, O=over/under range
    private static readonly System.Text.RegularExpressions.Regex RiceLakeIqPlus355 =
        new(@"^\s*([ \-\^\]])?\s*(\d+(?:\.\d+)?)\s*([LKTGO])([GNT])([ IMO])?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Backward-compatible entry point used by older call sites and tests.
    /// New code should call ParseSerialFrame with the brand-defined regex
    /// resolved via ResolveBrandRegex / BrandsCache.FindByKey.
    /// </summary>
    public static SerialFrame ParseIq355(string line) => ParseSerialFrame(line, null);

    /// <summary>
    /// Parses a serial weight frame in priority order:
    ///   1. Brand-defined weightRegex (from scale-models.json) — captures
    ///      Group 1 = weight; any of Groups 2..N may carry a single-char mode
    ///      (G/N/T) or the literal "MO" motion token.
    ///   2. Rice Lake 920i / IQ plus 355 EDP/PRN stream format (no whitespace
    ///      between weight and unit). Also used by Condec UMC.
    ///   3. Cardinal 225 Navigator IQ355 token format:
    ///        "   8980 LB G    "   stable
    ///        "   8860 LB G MO "   in motion
    ///        "-    20 LB G BZ "   below zero (sign in its own column)
    ///      Status flags appear as 2-char tokens after the mode (G/N/T):
    ///        MO = motion, BZ = below zero, ZR = at zero,
    ///        OL = overload, UR = under-range, ER = indicator error.
    /// </summary>
    public static SerialFrame ParseSerialFrame(
        string line, System.Text.RegularExpressions.Regex? brandRegex)
        => ParseSerialFrame(line, brandRegex, null);

    /// <summary>
    /// As above, but honouring per-scale column positions when the operator has set
    /// them (Auto-Detect, or by hand on the scale setup screen). Positions win over
    /// every brand/built-in parser: they were configured against frames this exact
    /// indicator actually sent, so they are better evidence than a shared regex.
    /// </summary>
    public static SerialFrame ParseSerialFrame(
        string line,
        System.Text.RegularExpressions.Regex? brandRegex,
        ScaleFormatDetector.PositionTokens? positions)
    {
        var frame = new SerialFrame();
        var input = line ?? string.Empty;

        // ---- 0. Operator-configured column positions ----
        if (positions.HasValue && positions.Value.IsUsable)
            return ScaleFormatDetector.ParseByPositions(input, positions.Value);

        // ---- 1. Brand-defined regex (data-driven, preferred) ----
        if (brandRegex != null)
        {
            var bm = brandRegex.Match(input);
            if (bm.Success)
            {
                ApplyBrandRegexMatch(bm, input, frame);
                return frame;
            }
        }

        // ---- 2. Rice Lake / Condec UMC built-in ----
        var rl = RiceLakeIqPlus355.Match(input);
        if (rl.Success)
        {
            ApplyRiceLakeIqPlus355(rl, frame);
            return frame;
        }

        // ---- 3. Cardinal 225 Navigator IQ355 token format (existing logic) ----
        var upper = input.ToUpperInvariant();
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
    /// Compile the brand-defined weightRegex for this scale, if the brand is
    /// known and its regex is well-formed. Returns null when the brand isn't
    /// in BrandsCache (e.g. before the first refresh, or for a deleted entry)
    /// or its regex fails to compile — both cases route the caller through
    /// the built-in Rice Lake / Cardinal fallbacks.
    /// </summary>
    /// <summary>
    /// The compiled brand regex for a scale, for callers outside this class that
    /// parse frames themselves — notably the TCP streaming path, which reads its own
    /// socket but must parse frames identically to the serial one.
    /// </summary>
    public System.Text.RegularExpressions.Regex? GetBrandRegex(ScaleConfigEntity scale) => ResolveBrandRegex(scale);

    private System.Text.RegularExpressions.Regex? ResolveBrandRegex(ScaleConfigEntity scale)
    {
        var brand = _brands.FindByKey(scale.ScaleBrand);
        if (brand == null)
        {
            _log.LogInformation(
                "Scale '{ScaleId}' brand '{Brand}' not found in BrandsCache; using built-in serial parsers.",
                scale.ScaleId, scale.ScaleBrand);
            return null;
        }
        if (string.IsNullOrWhiteSpace(brand.WeightRegex))
        {
            _log.LogInformation(
                "Scale '{ScaleId}' brand '{Brand}' has no weightRegex in scale-models.json; using built-in serial parsers.",
                scale.ScaleId, scale.ScaleBrand);
            return null;
        }
        try
        {
            var rx = new System.Text.RegularExpressions.Regex(
                brand.WeightRegex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);
            _log.LogInformation(
                "Scale '{ScaleId}' using brand weightRegex from '{Brand}': {Rx}",
                scale.ScaleId, scale.ScaleBrand, brand.WeightRegex);
            return rx;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "Scale '{ScaleId}' brand '{Brand}' has an invalid weightRegex ({Rx}): {Msg}. Falling back to built-in parsers.",
                scale.ScaleId, scale.ScaleBrand, brand.WeightRegex, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Populate a SerialFrame from a successful brand-defined regex match.
    /// Contract (positional with safe fallbacks):
    ///   Group 1 : weight (unsigned or signed decimal; required).
    ///   Group 2 : unit token (informational, ignored — e.g. "LB", "KG", "L").
    ///   Group 3 : mode char {G,N,T} — gates Gross-only readings.
    ///   Group 4 : status char {M, I, O, ' '}:
    ///               M = motion, I = invalid, O = over/under range,
    ///               anything else / empty = valid.
    ///   Group 5+: legacy slots; any group whose value is "MO" anywhere
    ///             flags motion (Cardinal-style two-char status block).
    /// Polarity handling: many Rice Lake / Condec frames carry the sign in
    /// a separate POL byte at the start of the frame, with leading-space
    /// suppression on the weight field — e.g. "-  11200LGM". The regex's
    /// [+-]? alternation can't span the whitespace gap, so the digits-only
    /// portion ends up in group 1 without a sign. To recover the sign, we
    /// scan the input text BEFORE the weight match for a polarity char.
    /// This also keeps the "group 1 = weight" contract intact for every
    /// existing brand regex.
    /// The status read is position-aware (group 4 only) so a unit "O"
    /// (ounces) captured at group 2 is never mistaken for an over-range
    /// flag. Mirrors the Cardinal/Rice Lake paths' frame contract:
    /// Weight is rounded to int; Motion is set on M or MO; Ok is true
    /// only for a clean Gross-mode valid reading.
    /// </summary>
    private static void ApplyBrandRegexMatch(
        System.Text.RegularExpressions.Match m, string input, SerialFrame frame)
    {
        if (m.Groups.Count < 2 || string.IsNullOrEmpty(m.Groups[1].Value))
        {
            frame.Ok = false;
            frame.Status = "Brand regex captured no weight";
            return;
        }

        var weightStr = m.Groups[1].Value.Trim();
        if (!decimal.TryParse(weightStr,
                              System.Globalization.NumberStyles.Number,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out var w))
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return;
        }

        // Polarity recovery: look in the input text from position 0 (NOT
        // from m.Index — the regex's leading \s* can only eat whitespace,
        // so a POL byte like '-' actually sits BEFORE the regex match
        // starts) up to where group 1 began. If a '-' lives in that
        // prefix (a Rice Lake / Condec POL byte that the regex couldn't
        // capture because of the whitespace gap between '-' and the
        // digits), negate the weight. Already-signed weights captured
        // inline (group 1 = "-12000") are unaffected: in that case
        // group 1 starts at the '-' itself and the prefix is empty.
        var polPrefixEnd = m.Groups[1].Index;
        if (polPrefixEnd > 0 && polPrefixEnd <= (input?.Length ?? 0))
        {
            for (int i = 0; i < polPrefixEnd; i++)
            {
                if (input![i] == '-') { w = -w; break; }
                // '^' = overload, ']' = under-range (IQ plus 355 spec).
                // The regex would only have matched if the weight field
                // also had digits, so seeing these here is unusual — flag
                // them and stop, ignoring whatever the digits decoded to.
                if (input[i] == '^') { frame.Ok = false; frame.Status = "Overload";    return; }
                if (input[i] == ']') { frame.Ok = false; frame.Status = "Under-Range"; return; }
            }
        }

        var mode   = ReadModeChar(m, groupIndex: 3);
        var status = ReadStatusChar(m, groupIndex: 4);
        var motion = status == "M" || HasMOToken(m);

        frame.Weight = (int)Math.Round(w);
        frame.Motion = motion;

        // Gross-mode gate.
        if (!string.IsNullOrEmpty(mode) && mode != "G")
        {
            frame.Ok = false;
            frame.Status = "Not Gross Mode";
            return;
        }

        // Status flags — only fire when group 4 actually carried the code,
        // so a unit "O" (ounces) elsewhere is never read as over-range.
        if (status == "I") { frame.Ok = false; frame.Status = "Invalid";          return; }
        if (status == "O") { frame.Ok = false; frame.Status = "Over/Under Range"; return; }

        frame.Ok = true;
        frame.Status = motion ? "Motion" : "Ok";
    }

    private static string ReadModeChar(System.Text.RegularExpressions.Match m, int groupIndex)
    {
        if (m.Groups.Count <= groupIndex) return string.Empty;
        var v = m.Groups[groupIndex].Value.Trim().ToUpperInvariant();
        return (v.Length == 1 && (v == "G" || v == "N" || v == "T")) ? v : string.Empty;
    }

    private static string ReadStatusChar(System.Text.RegularExpressions.Match m, int groupIndex)
    {
        if (m.Groups.Count <= groupIndex) return string.Empty;
        var v = m.Groups[groupIndex].Value.Trim().ToUpperInvariant();
        return (v.Length == 1 && (v == "M" || v == "I" || v == "O")) ? v : string.Empty;
    }

    private static bool HasMOToken(System.Text.RegularExpressions.Match m)
    {
        for (int i = 2; i < m.Groups.Count; i++)
        {
            if (m.Groups[i].Value.Trim().ToUpperInvariant() == "MO") return true;
        }
        return false;
    }

    /// <summary>
    /// Populate a SerialFrame from a successful RiceLakeIqPlus355 regex match.
    /// Groups: 1=POL, 2=weight, 3=unit, 4=mode, 5=status. Mirrors the Cardinal
    /// path's contract: Weight is the integer gross-mode reading, Motion reflects
    /// the M status flag, Ok is true only when the frame is a clean gross-mode
    /// reading, and Status carries the user-facing label.
    /// </summary>
    private static void ApplyRiceLakeIqPlus355(
        System.Text.RegularExpressions.Match m, SerialFrame frame)
    {
        var pol        = m.Groups[1].Value;
        var weightStr  = m.Groups[2].Value;
        var unitChar   = m.Groups[3].Value.ToUpperInvariant();
        var modeChar   = m.Groups[4].Value.ToUpperInvariant();
        var statusChar = m.Groups[5].Value.ToUpperInvariant();

        // POL='^' = overload, POL=']' = under-range. The weight field in those
        // frames is filled with carets or close-brackets and the regex would
        // never have matched the digits anyway, but the spec also allows POL
        // alone to signal the condition — handle both cases here.
        if (pol == "^") { frame.Ok = false; frame.Status = "Overload";    return; }
        if (pol == "]") { frame.Ok = false; frame.Status = "Under-Range"; return; }

        if (!decimal.TryParse(weightStr, System.Globalization.NumberStyles.Number,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out var w))
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return;
        }
        if (pol == "-") w = -w;

        var motion       = statusChar == "M";
        var invalid      = statusChar == "I";
        var overOrUnder  = statusChar == "O";

        frame.Weight = (int)Math.Round(w);
        frame.Motion = motion;

        // Match the Cardinal path's "Gross only" gating so downstream
        // consumers see a consistent contract regardless of which serial
        // protocol the indicator speaks.
        if (modeChar != "G")
        {
            frame.Ok = false;
            frame.Status = "Not Gross Mode";
            return;
        }

        if (invalid)           { frame.Ok = false; frame.Status = "Invalid"; }
        else if (overOrUnder)  { frame.Ok = false; frame.Status = "Over/Under Range"; }
        else if (motion)       { frame.Ok = true;  frame.Status = "Motion"; }
        else                   { frame.Ok = true;  frame.Status = "Ok"; }
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
        if (string.IsNullOrWhiteSpace(requestCommand))
            throw new InvalidOperationException($"Scale '{scale.ScaleId}' is OnDemand but no Request Command is set (e.g. 'Gross\\r').");

        using var port = BuildPort(scale, defaultTimeoutMs: 1000, setNewLine: false);

        port.Open();
        _log.LogInformation(
            "Serial port {Port} opened ({Baud},{Bits},{Par},{Stop}) for scale '{ScaleId}' (OnDemand, brand='{Brand}', cmd='{Cmd}').",
            scale.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, scale.ScaleId,
            scale.ScaleBrand, requestCommand.Replace("\r", "\\r").Replace("\n", "\\n"));

        // Warn once if there is no on-demand parser tuned for this brand — the
        // Cardinal fallback assumes "any 'M' = motion / any '-' = negative",
        // which other brands may violate.
        if (!IsKnownOnDemandBrand(scale.ScaleBrand))
        {
            _log.LogWarning(
                "Scale '{ScaleId}' brand '{Brand}' has no dedicated OnDemand parser; falling back to Cardinal-style parsing. " +
                "If readings look wrong, add a ParseXxxOnDemand for this brand.",
                scale.ScaleId, scale.ScaleBrand);
        }

        var requestBytes = BuildRequestBytes(requestCommand);
        var replyBuffer = new byte[ReadBufferBytes];
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

                    // Written straight on this thread rather than via Task.Run: with
                    // Handshake.None a few command bytes go into the driver's output
                    // buffer and return, and a Task.Run here had the same close-during-
                    // IO hazard as the read it used to sit beside.
                    port.Write(requestBytes, 0, requestBytes.Length);

                    var response = await ReadReplyAsync(
                        port, replyBuffer, scale.TimeoutMs > 0 ? scale.TimeoutMs : 1000, ct);

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

                        // Operator-configured columns beat the brand parser here too.
                        // A scale whose columns were set from its own frames must parse
                        // the same way regardless of which protocol fetched them.
                        var onDemandPositions = ScaleFormatDetector.PositionTokens.From(scale);
                        var frame = onDemandPositions.HasValue
                            ? ScaleFormatDetector.ParseByPositions(response.TrimEnd('\r', '\n'), onDemandPositions.Value)
                            : ParseOnDemandForBrand(scale.ScaleBrand, response);
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
    /// Reads the indicator's reply until a CR (0x0D) is seen or timeoutMs elapses,
    /// returning whatever arrived. Strips leading/trailing CR/LF/NUL on the way out.
    ///
    /// Like the streaming loop, this polls BytesToRead rather than blocking in
    /// SerialPort.ReadByte, so cancelling mid-poll leaves no read in flight for the
    /// caller's port.Close() to race against. See DrainAvailable for why that matters.
    /// Bytes after the CR are dropped, matching the old behaviour — the next poll
    /// discards the input buffer before sending its request anyway.
    /// </summary>
    private static async Task<string> ReadReplyAsync(
        SerialPort port, byte[] buffer, int timeoutMs, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var sb = new StringBuilder();

        while (DateTime.UtcNow < deadline)
        {
            int read = DrainAvailable(port, buffer);
            if (read == 0)
            {
                await Task.Delay(PollDelayMs, ct);
                continue;
            }

            for (int i = 0; i < read; i++)
            {
                sb.Append((char)buffer[i]);
                if (buffer[i] == 0x0D) return sb.ToString().Trim('\r', '\n', '\0');
            }
        }

        return sb.ToString().Trim('\r', '\n', '\0');
    }

    /// <summary>
    /// Parses an On-Demand reply. Cardinal indicators emit several variants
    /// depending on configuration:
    ///   "     120lb G"             — minimal (weight + units + mode, no spaces)
    ///   "   8980 LB G    "         — IQ355-style (separated)
    ///   "   8980 LB G MO "         — IQ355 with motion
    ///   " Z1G   100.0lb"           — documented full SMA-like layout
    ///   "-    20 LB G BZ "         — below-zero with sign in its own column
    ///
    /// Strategy: pull weight, units, mode, motion, and status flags out
    /// independently so we don't fail on column alignment differences.
    /// </summary>
    private static readonly Regex WeightUnitsRegex = new(
        @"(?<sign>[-+])?\s*(?<weight>\d+(?:\.\d+)?)\s*(?<units>lb|kg|ton|oz|t|g)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ModeRegex = new(@"(?<![A-Za-z])([GNT])(?![A-Za-z])", RegexOptions.Compiled);

    private static bool IsKnownOnDemandBrand(string? brand) =>
        (brand ?? "").Trim().ToLowerInvariant() == "cardinal";

    /// <summary>
    /// Routes an on-demand reply to the right brand-specific parser. New
    /// brands with their own on-demand reply format should be added here
    /// with their own ParseXxxOnDemand method, not by extending the
    /// Cardinal parser — its "any 'M' = motion / any '-' = negative" rules
    /// are tuned to the Cardinal 225 reply format and won't be safe for
    /// indicators that emit different status bytes.
    /// </summary>
    public static SerialFrame ParseOnDemandForBrand(string brand, string raw)
    {
        var b = (brand ?? "").Trim().ToLowerInvariant();
        return b switch
        {
            "cardinal" => ParseCardinalOnDemand(raw),
            // Add cases here as more on-demand brands are supported, e.g.:
            // "mettler toledo" => ParseMettlerOnDemand(raw),
            _ => ParseCardinalOnDemand(raw),  // fallback; warn at the call site
        };
    }

    public static SerialFrame ParseCardinalOnDemand(string raw)
    {
        var frame = new SerialFrame { RawText = raw };
        if (string.IsNullOrWhiteSpace(raw))
        {
            frame.Ok = false;
            frame.Status = "No data";
            return frame;
        }

        var weightMatch = WeightUnitsRegex.Match(raw);
        if (!weightMatch.Success)
        {
            frame.Ok = false;
            frame.Status = "Parse error";
            return frame;
        }

        double weightDecimal = double.Parse(weightMatch.Groups["weight"].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        // The Cardinal On-Demand reply uses simple positional flags rather than
        // dedicated bytes: any '-' anywhere means a below-zero (negative) reading,
        // any 'M'/'m' anywhere means motion. None of the valid tokens (lb/kg/ton/
        // oz/t/g, mode chars G/N/T, status chars Z/O/E/e) contain those letters,
        // so a whole-string scan is safe.
        bool negative = raw.IndexOf('-') >= 0;
        if (negative && weightDecimal > 0) weightDecimal = -weightDecimal;

        frame.Weight = (int)Math.Round(weightDecimal);

        bool motion = raw.IndexOf('M') >= 0 || raw.IndexOf('m') >= 0;
        frame.Motion = motion;

        // Mode: look anywhere, but ignore letters embedded in 'lb'/'KG' tokens.
        // Look in the part of the string that isn't the units token to be safe.
        string scanForMode = raw.Substring(0, weightMatch.Index)
                             + " "
                             + raw.Substring(weightMatch.Index + weightMatch.Length);
        var modeMatch = ModeRegex.Match(scanForMode);
        char mode = modeMatch.Success ? modeMatch.Groups[1].Value[0] : 'G';

        // Less-common status flags — left as token matches so we don't false-positive
        // on letters inside other tokens.
        var upper = raw.ToUpperInvariant();
        bool overload   = upper.Contains(" OL ") || (upper.Length > 1 && upper[0] == 'O' && char.IsDigit(upper[1]));
        bool errorFlag  = upper.Contains(" ER ") || upper.Contains(" E1") || upper.Contains(" E2");
        bool belowZero  = negative;
        bool atZero     = upper.Contains(" ZR ") || upper.Contains(">0<");
        bool notDisplayed = raw.Contains(" e") && raw.Contains(" e ");

        if (overload)        { frame.Ok = false; frame.Status = "Overload"; }
        else if (errorFlag)  { frame.Ok = false; frame.Status = "Indicator Error"; }
        else if (notDisplayed){ frame.Ok = false; frame.Status = "Weight Not Displayed"; }
        else if (belowZero)  { frame.Ok = true;  frame.Status = motion ? "Motion" : "Below Zero"; }
        else if (atZero)     { frame.Ok = true;  frame.Status = motion ? "Motion" : ">0<"; }
        else if (motion)     { frame.Ok = true;  frame.Status = "Motion"; }
        else                 { frame.Ok = true;  frame.Status = "Ok"; }

        if (mode != 'G' && frame.Ok)
        {
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

    /// <summary>
    /// Builds the SerialPort for a scale. Shared by the streaming, on-demand and
    /// capture paths so all three agree on framing, handshake and line settings —
    /// they drifted apart when this was duplicated inline.
    /// </summary>
    private static SerialPort BuildPort(ScaleConfigEntity scale, int defaultTimeoutMs, bool setNewLine)
    {
        if (string.IsNullOrWhiteSpace(scale.SerialPortName))
            throw new InvalidOperationException(
                $"Scale '{scale.ScaleId}' has ConnectionType=Serial but SerialPortName is empty.");

        int timeout = scale.TimeoutMs > 0 ? scale.TimeoutMs : defaultTimeoutMs;

        var port = new SerialPort(
            scale.SerialPortName,
            scale.BaudRate > 0 ? scale.BaudRate : 9600,
            ParseParity(scale.Parity),
            scale.DataBits > 0 ? scale.DataBits : 8,
            ParseStopBits(scale.StopBits))
        {
            ReadTimeout = timeout,
            WriteTimeout = timeout,
            Encoding = Encoding.ASCII,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true
        };

        // Only the streaming path reads by line; the on-demand path reads to CR by hand.
        if (setNewLine) port.NewLine = "\r";
        return port;
    }

    /// <summary>
    /// Serial ports this machine offers. The Linux branch matters on the Pi
    /// deployments, where GetPortNames alone misses USB adapters.
    /// </summary>
    public static List<string> ListPorts()
    {
        var ports = new HashSet<string>(SerialPort.GetPortNames());
        if (!OperatingSystem.IsWindows())
        {
            foreach (var pattern in new[] { "ttyUSB*", "ttyACM*", "ttyAMA*", "ttyS*" })
            {
                try
                {
                    foreach (var path in Directory.GetFiles("/dev", pattern)) ports.Add(path);
                }
                catch (DirectoryNotFoundException) { /* no /dev — nothing to add */ }
            }
        }
        return ports.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Opens the port and collects raw frames for Auto-Detect, without parsing them
    /// or touching the database. Returns whatever arrived when the window closes —
    /// an empty list is a legitimate answer (wrong baud, wrong port, or the
    /// indicator isn't in continuous mode), and the caller reports it as such.
    /// </summary>
    public async Task<CaptureResult> CaptureFramesAsync(
        ScaleConfigEntity scale, int captureMs, int maxFrames, CancellationToken ct)
    {
        var result = new CaptureResult();
        using var port = BuildPort(scale, defaultTimeoutMs: 1000, setNewLine: true);
        port.Open();
        port.DiscardInBuffer();

        _log.LogInformation("Capturing frames on {Port} ({Baud},{Bits},{Par},{Stop}) for up to {Ms}ms",
            scale.SerialPortName, port.BaudRate, port.DataBits, port.Parity, port.StopBits, captureMs);

        // A demand-mode indicator says nothing until asked. Send the request command
        // (if one is configured) and keep re-sending it through the window, otherwise
        // detection would report "no data" for a perfectly healthy scale.
        byte[]? request = string.IsNullOrWhiteSpace(scale.RequestCommand)
            ? null
            : BuildRequestBytes(scale.RequestCommand);
        DateTime nextRequest = DateTime.MinValue;

        var deadline = DateTime.UtcNow.AddMilliseconds(captureMs);
        var buffer = new byte[512];
        var pending = new StringBuilder();
        var rawBytes = new List<byte>();

        while (DateTime.UtcNow < deadline && result.Frames.Count < maxFrames && !ct.IsCancellationRequested)
        {
            if (request != null && DateTime.UtcNow >= nextRequest)
            {
                try { port.Write(request, 0, request.Length); } catch (TimeoutException) { }
                nextRequest = DateTime.UtcNow.AddMilliseconds(
                    scale.PollingIntervalMs > 0 ? scale.PollingIntervalMs : 500);
            }

            if (port.BytesToRead == 0)
            {
                await Task.Delay(20, ct);
                continue;
            }

            // Read raw bytes rather than ReadLine(). ReadLine only splits on the
            // configured NewLine ("\r"), and detection exists precisely because we do
            // NOT yet know how this indicator frames its output — an LF-terminated
            // scale would time out forever and look like a dead port.
            int read = port.Read(buffer, 0, Math.Min(buffer.Length, port.BytesToRead));
            if (read <= 0) continue;

            for (int i = 0; i < read; i++) rawBytes.Add(buffer[i]);
            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var text = pending.ToString();
            int cut;
            while ((cut = text.IndexOfAny(new[] { '\r', '\n' })) >= 0)
            {
                var line = text.Substring(0, cut).Trim('\0', '\x02', '\x03');
                text = text.Substring(cut + 1);
                if (!string.IsNullOrWhiteSpace(line)) result.Frames.Add(line);
                if (result.Frames.Count >= maxFrames) break;
            }
            pending.Clear();
            pending.Append(text);
        }

        result.BytesRead = rawBytes.Count;
        result.RawSample = BitConverter.ToString(rawBytes.Take(64).ToArray());

        // Bytes arrived but nothing was CR/LF terminated — hand back what we saw so
        // the operator can tell "wrong baud" from "different terminator".
        if (result.Frames.Count == 0 && pending.Length > 0)
            result.Unterminated = pending.ToString();

        return result;
    }

    private static Parity ParseParity(string? value) => value?.ToUpperInvariant() switch
    {
        "EVEN" => System.IO.Ports.Parity.Even,
        "ODD" => System.IO.Ports.Parity.Odd,
        "MARK" => System.IO.Ports.Parity.Mark,
        "SPACE" => System.IO.Ports.Parity.Space,
        _ => System.IO.Ports.Parity.None,
    };

    /// <summary>
    /// Stop bits as stored on the scale row. 0 means "never set" — a row saved before
    /// the field existed, or one posted without it — and must fall back to 1, the value
    /// every brand definition and the editor's own default use. It previously mapped 0
    /// to 1.5, which is only legal alongside 5 data bits: with the usual 8 the DCB is
    /// rejected and the open fails with a driver-level error that reads like broken
    /// hardware rather than bad configuration.
    /// </summary>
    private static StopBits ParseStopBits(int value) => value switch
    {
        2 => System.IO.Ports.StopBits.Two,
        _ => System.IO.Ports.StopBits.One,
    };
}

/// <summary>
/// What a detection capture actually saw. Carries more than the frames so
/// "nothing arrived" can be told apart from "bytes arrived but never framed" —
/// on an unknown indicator those two have completely different fixes.
/// </summary>
public sealed class CaptureResult
{
    public List<string> Frames { get; set; } = new();

    /// <summary>Total bytes received during the window, framed or not.</summary>
    public int BytesRead { get; set; }

    /// <summary>Hex of the first bytes seen, for diagnosing baud/parity mismatches.</summary>
    public string RawSample { get; set; } = "";

    /// <summary>Leftover text when bytes arrived but no CR/LF ever did.</summary>
    public string? Unterminated { get; set; }
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
