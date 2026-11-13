using System.Globalization;
using System.Net.Sockets;
using System.Text;
using ScaleReaderService.Models;

namespace ScaleReaderService.Services;

/// <summary>
/// Client for the Mettler Toledo Shared Data Server (IND700, IND560 / IND570 /
/// IND780, TCP port 1701 by default). Per the IND700 Shared Data Reference §1.10:
///
///   user admin            → "12 Access OK"   (no password required)
///                         → "51 Enter Password"
///   pass secret           → "12 Access OK"   (anything else = no access)
///   read wt0101 wt0103    → "00R003~ 17.08~lb~"
///   write wc0104=1        → "00W006~OK"
///   quit                  → "52 Closing connection"
///
/// read/write/callback replies carry a header: two status characters ("00"
/// success, "99" failure), a type character (R, W, C…) and a 001–999 sequence
/// number. Individually requested fields are separated by '~'. Only user, pass,
/// help and quit are accepted before login. The connection stays open between
/// reads, so a session logs in once and polls until the socket drops.
///
/// Field names come from the brand definition in device-definitions rather
/// than being hard-coded, so a terminal family that differs can be corrected
/// there without a new build.
/// </summary>
public sealed class SdsSession : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly int _timeoutMs;
    private readonly StringBuilder _pending = new();
    private readonly byte[] _buffer = new byte[1024];

    /// <summary>The greeting the terminal sent on connect, if any (handy in logs).</summary>
    public string? Greeting { get; private set; }

    private SdsSession(TcpClient client, int timeoutMs)
    {
        _client = client;
        _stream = client.GetStream();
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Connects and, when a user name is given, logs in. Throws
    /// <see cref="SdsException"/> with the terminal's own reply when the login is
    /// refused, so the operator sees "93 Login failed" rather than a timeout.
    /// </summary>
    public static async Task<SdsSession> ConnectAsync(
        string host, int port, string? user, string? password, int timeoutMs, CancellationToken ct)
    {
        timeoutMs = timeoutMs > 0 ? timeoutMs : 2000;
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(timeoutMs);
                await client.ConnectAsync(host, port, connectCts.Token);
            }

            var session = new SdsSession(client, timeoutMs);

            // The manual documents no greeting, but some firmware sends a banner
            // line on connect. Swallow it if it comes so it isn't mistaken for the
            // reply to "user"; a silent server just costs this short wait.
            session.Greeting = await session.TryReadLineAsync(Math.Min(timeoutMs, 1000), ct);

            if (!string.IsNullOrWhiteSpace(user))
                await session.LoginAsync(user.Trim(), password, ct);

            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task LoginAsync(string user, string? password, CancellationToken ct)
    {
        var reply = await CommandAsync("user " + user, ct);
        if (IsAccessOk(reply)) return;

        if (!LooksLikePasswordPrompt(reply))
            throw new SdsException($"Login refused for user '{user}': {reply}");

        if (string.IsNullOrEmpty(password))
            throw new SdsException($"Terminal wants a password for user '{user}' but none is configured: {reply}");

        reply = await CommandAsync("pass " + password, ct);
        if (!IsAccessOk(reply))
            throw new SdsException($"Login failed for user '{user}': {reply}");
    }

    /// <summary>
    /// Reads the given fields in one request. Returns the raw reply line and the
    /// values in request order (untrimmed — the caller decides what spaces mean).
    /// </summary>
    public async Task<(string Raw, string[] Values)> ReadAsync(IReadOnlyList<string> fields, CancellationToken ct)
    {
        if (fields.Count == 0) throw new ArgumentException("No shared data fields to read.", nameof(fields));

        await WriteLineAsync("read " + string.Join(' ', fields), ct);

        var line = await ReadReplyAsync('R', ct);
        if (!IsSuccess(line))
            throw new SdsException($"read {string.Join(' ', fields)} failed: {line}");

        var values = ParseReadReply(line);
        if (values.Length < fields.Count)
            throw new SdsException(
                $"Terminal returned {values.Length} value(s) for {fields.Count} field(s): {line}");
        return (line, values);
    }

    /// <summary>
    /// Writes one field ("write wc0104=1"). Used for command triggers such as
    /// pushbutton zero; needs a login whose class has write access to the field.
    /// </summary>
    public async Task<string> WriteAsync(string field, string value, CancellationToken ct)
    {
        await WriteLineAsync($"write {field}={value}", ct);
        var line = await ReadReplyAsync('W', ct);
        if (!IsSuccess(line))
            throw new SdsException($"write {field}={value} failed: {line}");
        return line;
    }

    /// <summary>
    /// Next reply for a read ('R') or write ('W'), skipping unsolicited output
    /// that can share the connection: callback ("00C…"), contout and the like.
    /// Anything else is returned so the caller can report it ("99…" failures,
    /// other error codes).
    /// </summary>
    private async Task<string> ReadReplyAsync(char type, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            var line = await ReadLineAsync(ct);
            var t = line.TrimStart();
            bool otherSuccessType = t.Length >= 3 && t.StartsWith("00") && char.IsLetter(t[2])
                                    && char.ToUpperInvariant(t[2]) != type;
            if (!otherSuccessType) return line;
        }
        throw new SdsException("No reply from terminal (only unsolicited output).");
    }

    /// <summary>"00R…" / "00W…" = success; "99…" or any other code = failure.</summary>
    private static bool IsSuccess(string line) => line.TrimStart().StartsWith("00");

    /// <summary>"00R003~ 17.08~lb~" → [" 17.08", "lb"].</summary>
    public static string[] ParseReadReply(string line)
    {
        var parts = line.Split('~');
        if (parts.Length < 2) return Array.Empty<string>();
        // parts[0] is the "00Rnnn" header. Every value is '~'-terminated, so the
        // last element is the empty remainder after the final tilde — drop it, but
        // keep empty values in between.
        int count = parts.Length - 1 - (line.EndsWith('~') ? 1 : 0);
        return parts.Skip(1).Take(count).ToArray();
    }

    /// <summary>Two-digit code at the start of a reply, or -1.</summary>
    public static int ReplyCode(string line)
    {
        var t = line.TrimStart();
        return t.Length >= 2 && char.IsDigit(t[0]) && char.IsDigit(t[1])
            ? (t[0] - '0') * 10 + (t[1] - '0')
            : -1;
    }

    private static bool IsAccessOk(string reply)
    {
        var code = ReplyCode(reply);
        return code == 12 || reply.Contains("Access OK", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePasswordPrompt(string reply) =>
        ReplyCode(reply) == 51 || reply.Contains("password", StringComparison.OrdinalIgnoreCase);

    private async Task<string> CommandAsync(string command, CancellationToken ct)
    {
        await WriteLineAsync(command, ct);
        return await ReadLineAsync(ct);
    }

    private async Task WriteLineAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\r\n");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        await _stream.WriteAsync(bytes, cts.Token);
        await _stream.FlushAsync(cts.Token);
    }

    private async Task<string?> TryReadLineAsync(int timeoutMs, CancellationToken ct)
    {
        try { return await ReadLineAsync(ct, timeoutMs); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct, int? timeoutMs = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs ?? _timeoutMs);

        while (true)
        {
            var text = _pending.ToString();
            int cut = text.IndexOfAny(new[] { '\r', '\n' });
            while (cut >= 0)
            {
                var line = text.Substring(0, cut);
                text = text.Substring(cut + 1);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _pending.Clear().Append(text);
                    return line.Trim('\0');
                }
                cut = text.IndexOfAny(new[] { '\r', '\n' });
            }
            _pending.Clear().Append(text);

            int read = await _stream.ReadAsync(_buffer, cts.Token);
            if (read <= 0) throw new IOException("Terminal closed the Shared Data connection.");
            _pending.Append(Encoding.ASCII.GetString(_buffer, 0, read));
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // Polite logout; the terminal frees the session slot sooner.
            if (_client.Connected)
            {
                var bytes = Encoding.ASCII.GetBytes("quit\r\n");
                using var cts = new CancellationTokenSource(250);
                await _stream.WriteAsync(bytes, cts.Token);
            }
        }
        catch { /* closing anyway */ }
        _stream.Dispose();
        _client.Dispose();
    }
}

/// <summary>A reply from the terminal that means the request was refused.</summary>
public sealed class SdsException : Exception
{
    public SdsException(string message) : base(message) { }
}

/// <summary>
/// Turns a read of the configured fields into the weight/motion/status the rest of
/// the service publishes. Status names match what <see cref="SmaClient"/> reports,
/// so the web app treats an SDS scale exactly like an SMA one.
/// </summary>
public static class SdsReading
{
    public const string DefaultGrossField = "wt0101";
    public const string DefaultUnitsField = "wt0103";
    /// <summary>wc--04 "Pushbutton Zero Scale": set to 1 to trigger; the engine resets it to 0.</summary>
    public const string DefaultZeroField = "wc0104";

    /// <summary>The fields to request, in a fixed order, and where each one lands.</summary>
    public sealed record Plan(
        List<string> Fields, int GrossIndex, int UnitsIndex,
        int MotionIndex, int CenterOfZeroIndex, int OverCapacityIndex, int UnderZeroIndex)
    {
        public bool HasStatus =>
            MotionIndex >= 0 || CenterOfZeroIndex >= 0 || OverCapacityIndex >= 0 || UnderZeroIndex >= 0;
    }

    public static Plan BuildPlan(ScaleBrandDefinition? brand)
    {
        var f = brand?.SdsFields;
        var fields = new List<string>();
        int Add(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return -1;
            fields.Add(name.Trim());
            return fields.Count - 1;
        }

        // wt--01 "Displayed Gross Weight", wt--03 "Weight Units"; instance 01 = scale 1.
        int gross = Add(string.IsNullOrWhiteSpace(f?.Gross) ? DefaultGrossField : f!.Gross);
        int units = Add(f == null ? DefaultUnitsField : f.Units);
        return new Plan(fields, gross, units,
            Add(f?.Motion), Add(f?.CenterOfZero), Add(f?.OverCapacity), Add(f?.UnderZero));
    }

    /// <summary>Same gross/units, no status fields — the fallback when the terminal rejects one.</summary>
    public static Plan WeightOnly(Plan p)
    {
        var fields = new List<string> { p.Fields[p.GrossIndex] };
        int units = -1;
        if (p.UnitsIndex >= 0) { fields.Add(p.Fields[p.UnitsIndex]); units = 1; }
        return new Plan(fields, 0, units, -1, -1, -1, -1);
    }

    public static (int Weight, bool Motion, bool Ok, string Status) Interpret(Plan plan, string[] values)
    {
        string V(int i) => i >= 0 && i < values.Length ? values[i].Trim() : "";
        bool On(int i) => IsTrue(V(i));

        // The displayed gross is a string (S13). A terminal that is over capacity,
        // under zero or not ready may show dashes or similar instead of a number,
        // so "not a number" is reported as an error, never as zero weight.
        var grossText = V(plan.GrossIndex);
        if (!decimal.TryParse(grossText, NumberStyles.Float, CultureInfo.InvariantCulture, out var gross))
            return (0, false, false, "Error");

        bool motion = On(plan.MotionIndex);
        int weight = (int)Math.Round(gross, MidpointRounding.AwayFromZero);

        if (On(plan.OverCapacityIndex)) return (weight, motion, false, "Over Capacity");
        if (On(plan.UnderZeroIndex)) return (weight, motion, false, "Under Capacity");
        if (motion) return (weight, true, true, "Motion");
        if (On(plan.CenterOfZeroIndex)) return (weight, false, true, ">0<");
        return (weight, false, true, "Ok");
    }

    /// <summary>Shared-data booleans come back as "1"/"0" (sometimes "T"/"F").</summary>
    private static bool IsTrue(string v) =>
        v == "1" || v.Equals("T", StringComparison.OrdinalIgnoreCase)
                 || v.Equals("true", StringComparison.OrdinalIgnoreCase);
}
