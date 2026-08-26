using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using ScaleReaderService.Data;
using ScaleReaderService.Models;
using ScaleReaderService.Services;

namespace ScaleReaderService;

public class ScaleWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ScaleWorker> _log;
    private readonly RestartSignal _restart;
    private readonly AnnounceSignal _announce;
    private readonly SmaClient _smaClient;
    private readonly SerialScaleClient _serialClient;
    private readonly ScaleWeightStore _weightStore;
    private readonly BrandsCache _brands;
    private readonly GpioInputs _gpio;
    private HubConnection? _connection;
    private string _serviceId = "default";
    private string _serverUrl = "";

    /// <summary>This service's version, surfaced on the web app's scale screen so
    /// an operator can see what is deployed without remoting into the box.</summary>
    private static readonly string ServiceVersion =
        typeof(ScaleWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public ScaleWorker(
        IServiceProvider sp,
        ILogger<ScaleWorker> log,
        RestartSignal restart,
        AnnounceSignal announce,
        SmaClient smaClient,
        SerialScaleClient serialClient,
        ScaleWeightStore weightStore,
        BrandsCache brands,
        GpioInputs gpio)
    {
        _sp = sp;
        _log = log;
        _restart = restart;
        _announce = announce;
        _smaClient = smaClient;
        _serialClient = serialClient;
        _weightStore = weightStore;
        _brands = brands;
        _gpio = gpio;
    }

    /// <summary>
    /// Is the truck fully on this scale's deck? False only when a detector at
    /// one end of the platform is blocked, which the web app turns into a
    /// "NOT ON SCALE" refusal. A scale with no detector pins configured is
    /// always on-scale, so this is inert until a site wires them up.
    /// </summary>
    private bool IsOnScale(ScaleConfigEntity scale) =>
        !_gpio.AnyDetectorActive(scale.EndDetectorPin1, scale.EndDetectorPin2,
                                 scale.InvertDetectorPins, scale.DetectorPullUp);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe to announce signal
        _announce.OnAnnounceRequested += async () =>
        {
            try { await AnnounceScales(); }
            catch (Exception ex) { _log.LogWarning("Announce failed: {Msg}", ex.Message); }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Load settings from DB
                string serverUrl, hubPath;
                using (var scope = _sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                    var settings = db.Settings.OrderBy(s => s.Id).FirstOrDefault();
                    serverUrl = settings?.ServerUrl ?? "http://localhost:5110";
                    hubPath = settings?.SignalRHub ?? "/scaleHub";
                    _serviceId = settings?.ServiceId ?? "default";
                    _serverUrl = serverUrl;

                    // Refresh brands cache so the local file mirrors the remote
                    // by the time the first SignalR client asks.
                    if (!string.IsNullOrWhiteSpace(settings?.BrandsUrl))
                    {
                        await _sp.GetRequiredService<BrandsCache>().RefreshAsync();
                    }
                }

                _log.LogInformation("Loaded settings from database: ServiceId={ServiceId}, ServerUrl={Url}", _serviceId, serverUrl);

                await ConnectAndRun(serverUrl, hubPath, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Connection lost. Reconnecting in 5 seconds... Error: {Msg}", ex.Message);
                try { await Task.Delay(5000, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                if (_connection != null)
                {
                    try { await _connection.DisposeAsync(); }
                    catch { /* ignore cleanup errors */ }
                    _connection = null;
                }
            }
        }
    }

    private async Task ConnectAndRun(string serverUrl, string hubPath, CancellationToken stoppingToken)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl.TrimEnd('/')}{hubPath}")
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        _connection.Reconnecting += ex =>
        {
            _log.LogWarning("Connection lost. Reconnecting...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _log.LogInformation("Reconnected. Rejoining scale groups...");
            await JoinGroups();
            await AnnounceScales();
        };

        // Register handlers for SignalR commands from the web app
        RegisterHandlers();

        _log.LogInformation("Connecting to {Url}{Hub}", serverUrl, hubPath);
        await _connection.StartAsync(stoppingToken);
        _log.LogInformation("Connected to server. Joining scale groups (ServiceId={ServiceId})...", _serviceId);

        await JoinGroups();
        _log.LogInformation("Joined scale groups. Starting scale pollers...");

        await AnnounceScales();

        // Start polling all active scales
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _restart.Token);
        await PollScales(linked.Token);
    }

    private void RegisterHandlers()
    {
        // Web app can request the loaded brand definitions list. Each call refreshes
        // from the configured remote URL (and persists it to the local cache); on
        // failure the response carries Source="local" + Error so the UI can warn.
        _connection!.On("GetScaleBrands", async () =>
        {
            try
            {
                var cache = _sp.GetRequiredService<BrandsCache>();
                var result = await cache.RefreshAsync();
                await _connection!.InvokeAsync("ScaleBrandsResponse", new
                {
                    serviceId = _serviceId,
                    brands = result.Brands,
                    source = result.Source,       // "remote" or "local"
                    remoteUrl = result.RemoteUrl,
                    error = result.Error
                });
            }
            catch (Exception ex) { _log.LogWarning("GetScaleBrands failed: {Msg}", ex.Message); }
        });

        // Web app can request scale list
        _connection!.On("GetScaleList", async () =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                // Every scale, not just the active ones — see AnnounceScales.
                var scales = await db.Scales.OrderBy(s => s.ScaleId).ToListAsync();
                await _connection!.InvokeAsync("ScaleListResponse", new
                {
                    serviceId = _serviceId,
                    version = ServiceVersion,
                    serverUrl = _serverUrl,
                    scales = scales.Select(s => new
                    {
                        s.ScaleId, s.DisplayName, s.ScaleBrand,
                        s.ConnectionType, s.Protocol,
                        s.IpAddress, s.Port,
                        s.SerialPortName, s.BaudRate, s.DataBits, s.Parity, s.StopBits,
                        s.RequestCommand,
                        s.PollingIntervalMs, s.TimeoutMs,
                        s.FrameParseMode,
                        s.FrameWeightStart, s.FrameWeightEnd,
                        s.FrameMotionIndex, s.FrameMotionChar,
                        s.FrameSignIndex, s.FrameSignNegChar,
                        s.Active
                    })
                });
            }
            catch (Exception ex) { _log.LogWarning("GetScaleList failed: {Msg}", ex.Message); }
        });

        // Serial ports available on this machine, for the setup screen's port picker.
        _connection!.On("GetSerialPorts", async () =>
        {
            try
            {
                await _connection!.InvokeAsync("ScaleSerialPortsResponse", new
                {
                    serviceId = _serviceId,
                    ports = SerialScaleClient.ListPorts()
                });
            }
            catch (Exception ex) { _log.LogWarning("GetSerialPorts failed: {Msg}", ex.Message); }
        });

        // Auto-Detect: open a temporary connection using the settings the operator has
        // typed into the Add/Edit Scale modal, capture a few seconds of the stream, and
        // report what the frames look like. Deliberately works from posted parameters
        // rather than the database — detection has to run before the scale is saved.
        _connection!.On<string, System.Text.Json.JsonElement>("DetectFormat", (requestId, config) =>
        {
            // Fire-and-forget on a worker thread: a wedged port must not block the
            // SignalR message pump, and the handler answers on every path so the
            // browser's button never hangs waiting for a reply that isn't coming.
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunDetection(requestId, config);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("DetectFormat failed: {Msg}", ex.Message);
                    await SafeDetectReply(requestId, new { ok = false, error = ex.Message });
                }
            });
        });

        // Reload config command
        _connection!.On("ReloadConfig", () =>
        {
            _log.LogInformation("Received ReloadConfig command. Restarting...");
            _restart.TriggerRestart();
        });

        // Zero scale command
        _connection!.On<string>("ZeroScale", async (scaleId) =>
        {
            _log.LogInformation("Received ZeroScale command for scale: {ScaleId}", scaleId);
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                var scale = await db.Scales.FirstOrDefaultAsync(s => s.ScaleId == scaleId && s.Active);
                if (scale == null)
                {
                    _log.LogWarning("ZeroScale: Scale '{ScaleId}' not found or inactive", scaleId);
                    return;
                }

                // Send zero command to the scale indicator
                var zeroCommand = "Z\r\n"; // Standard SMA zero command
                using var client = new System.Net.Sockets.TcpClient();
                using var cts = new CancellationTokenSource(scale.TimeoutMs > 0 ? scale.TimeoutMs : 2000);
                await client.ConnectAsync(scale.IpAddress, scale.Port, cts.Token);
                using var ns = client.GetStream();
                var bytes = System.Text.Encoding.ASCII.GetBytes(zeroCommand);
                await ns.WriteAsync(bytes, cts.Token);
                await ns.FlushAsync(cts.Token);
                _log.LogInformation("ZeroScale: Sent zero command to {ScaleId} at {Ip}:{Port}", scaleId, scale.IpAddress, scale.Port);
            }
            catch (Exception ex)
            {
                _log.LogError("ZeroScale failed for {ScaleId}: {Msg}", scaleId, ex.Message);
            }
        });

        // CRUD: Add scale
        _connection!.On<System.Text.Json.JsonElement>("AddScale", async (config) =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                var entity = System.Text.Json.JsonSerializer.Deserialize<ScaleConfigEntity>(config.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entity == null || string.IsNullOrWhiteSpace(entity.ScaleId))
                {
                    await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = "Invalid scale data" });
                    return;
                }
                entity.Id = 0;
                db.Scales.Add(entity);
                await db.SaveChangesAsync();
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = true, message = "Scale added: " + entity.ScaleId });
                _restart.TriggerRestart(); // restart to pick up new scale
            }
            catch (Exception ex)
            {
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = ex.Message });
            }
        });

        // CRUD: Update scale
        _connection!.On<string, System.Text.Json.JsonElement>("UpdateScale", async (scaleId, config) =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                var existing = db.Scales.FirstOrDefault(s => s.ScaleId == scaleId);
                if (existing == null)
                {
                    await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = "Scale not found: " + scaleId });
                    return;
                }
                var update = System.Text.Json.JsonSerializer.Deserialize<ScaleConfigEntity>(config.GetRawText(),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (update != null)
                {
                    existing.DisplayName = update.DisplayName ?? existing.DisplayName;
                    existing.ScaleBrand = update.ScaleBrand ?? existing.ScaleBrand;
                    if (!string.IsNullOrWhiteSpace(update.ConnectionType)) existing.ConnectionType = update.ConnectionType;
                    if (!string.IsNullOrWhiteSpace(update.Protocol)) existing.Protocol = update.Protocol;
                    existing.IpAddress = update.IpAddress ?? existing.IpAddress;
                    if (update.Port > 0) existing.Port = update.Port;
                    existing.SerialPortName = update.SerialPortName ?? existing.SerialPortName;
                    if (update.BaudRate > 0) existing.BaudRate = update.BaudRate;
                    if (update.DataBits > 0) existing.DataBits = update.DataBits;
                    if (!string.IsNullOrWhiteSpace(update.Parity)) existing.Parity = update.Parity;
                    if (update.StopBits >= 0) existing.StopBits = update.StopBits;
                    existing.RequestCommand = update.RequestCommand;
                    if (update.PollingIntervalMs > 0) existing.PollingIntervalMs = update.PollingIntervalMs;
                    if (update.TimeoutMs > 0) existing.TimeoutMs = update.TimeoutMs;

                    // Stream tokens are assigned straight across, nulls included: clearing
                    // the columns is how an operator reverts a scale to brand-regex parsing,
                    // so a "only copy if set" guard here would make that impossible.
                    if (!string.IsNullOrWhiteSpace(update.FrameParseMode)) existing.FrameParseMode = update.FrameParseMode;
                    existing.FrameWeightStart = update.FrameWeightStart;
                    existing.FrameWeightEnd = update.FrameWeightEnd;
                    existing.FrameMotionIndex = update.FrameMotionIndex;
                    existing.FrameMotionChar = update.FrameMotionChar;
                    existing.FrameSignIndex = update.FrameSignIndex;
                    existing.FrameSignNegChar = update.FrameSignNegChar;

                    // Detector pins are assigned straight across for the same
                    // reason: clearing a pin is how a site removes a detector.
                    existing.EndDetectorPin1 = update.EndDetectorPin1;
                    existing.EndDetectorPin2 = update.EndDetectorPin2;
                    existing.InvertDetectorPins = update.InvertDetectorPins;
                    existing.DetectorPullUp = update.DetectorPullUp;

                    existing.Active = update.Active;
                }
                await db.SaveChangesAsync();
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = true, message = "Scale updated: " + scaleId });
                _restart.TriggerRestart();
            }
            catch (Exception ex)
            {
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = ex.Message });
            }
        });

        // CRUD: Delete scale
        _connection!.On<string>("DeleteScale", async (scaleId) =>
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                var existing = db.Scales.FirstOrDefault(s => s.ScaleId == scaleId);
                if (existing == null)
                {
                    await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = "Scale not found: " + scaleId });
                    return;
                }
                db.Scales.Remove(existing);
                await db.SaveChangesAsync();
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = true, message = "Scale deleted: " + scaleId });
                _restart.TriggerRestart();
            }
            catch (Exception ex)
            {
                await _connection!.InvokeAsync("ScaleCrudResult", new { success = false, message = ex.Message });
            }
        });
    }

    private async Task JoinGroups()
    {
        await _connection!.InvokeAsync("JoinScaleGroup", _serviceId);
    }

    private async Task AnnounceScales()
    {
        if (_connection?.State != HubConnectionState.Connected) return;

        try
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
            // Announce every scale, active or not, and let the web app decide what to do
            // with each. Filtering to Active here made deactivating a scale look like
            // deleting it: the row vanished from the setup table and Edit silently did
            // nothing, so the only way back was to re-create the scale. That collided
            // head-on with Auto-Detect, which requires the scale to be inactive first
            // because an active poller holds the serial port open. The setup screen
            // already renders the active flag as a column, and the pickers that must
            // only offer live hardware filter on it themselves.
            var scales = await db.Scales.OrderBy(s => s.ScaleId).ToListAsync();

            await _connection.InvokeAsync("ScaleServiceReady", new
            {
                serviceId = _serviceId,
                version = ServiceVersion,
                serverUrl = _serverUrl,
                // Still the number being polled, which is what this has always meant.
                scaleCount = scales.Count(s => s.Active),
                scales = scales.Select(s => new
                {
                    s.ScaleId, s.DisplayName, s.ScaleBrand,
                    s.ConnectionType, s.Protocol,
                    s.IpAddress, s.Port,
                    s.SerialPortName, s.BaudRate, s.DataBits, s.Parity, s.StopBits,
                    s.RequestCommand,
                    s.PollingIntervalMs, s.TimeoutMs,
                    s.FrameParseMode,
                    s.FrameWeightStart, s.FrameWeightEnd,
                    s.FrameMotionIndex, s.FrameMotionChar,
                    s.FrameSignIndex, s.FrameSignNegChar,
                    s.EndDetectorPin1, s.EndDetectorPin2,
                    s.InvertDetectorPins, s.DetectorPullUp,
                    s.Active
                })
            });

            _log.LogInformation("Announced {Count} scale(s) to web app, {Active} active.",
                scales.Count, scales.Count(s => s.Active));
        }
        catch (Exception ex)
        {
            _log.LogWarning("Failed to announce scales: {Msg}", ex.Message);
        }
    }

    // ===== AUTO-DETECT =====

    /// <summary>How long a detect run listens before reporting what it heard.</summary>
    private const int DetectCaptureMs = 4000;
    private const int DetectMaxFrames = 40;

    /// <summary>
    /// Captures frames using the posted connection settings and reports the inferred
    /// layout. Answers on every path — success, no data, or failure — because the
    /// browser is sitting on a watchdog waiting for exactly one reply.
    /// </summary>
    private async Task RunDetection(string requestId, System.Text.Json.JsonElement config)
    {
        var probe = System.Text.Json.JsonSerializer.Deserialize<ScaleConfigEntity>(
            config.GetRawText(),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (probe == null)
        {
            await SafeDetectReply(requestId, new { ok = false, error = "Could not read the connection settings." });
            return;
        }

        // Detection reads a stream; never let a stale RequestCommand turn this into
        // a demand poll, and give the capture a sane per-read timeout.
        probe.ScaleId = string.IsNullOrWhiteSpace(probe.ScaleId) ? "detect-probe" : probe.ScaleId;
        if (probe.TimeoutMs <= 0) probe.TimeoutMs = 1000;

        bool isSerial = string.Equals(probe.ConnectionType, "Serial", StringComparison.OrdinalIgnoreCase);

        // A running poller owns the port/socket. Say so plainly rather than surfacing
        // a raw UnauthorizedAccessException the operator can't act on.
        if (isSerial && !string.IsNullOrWhiteSpace(probe.SerialPortName))
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
            // Any active scale on this port holds it open — including the very scale
            // being edited, which is the common case (you open Edit on the scale that
            // isn't reading and press Detect). An earlier version exempted the same
            // scale id, which just traded this clear message for a raw
            // "access denied" from port.Open().
            var holder = await db.Scales.FirstOrDefaultAsync(s =>
                s.Active && s.ConnectionType == "Serial" && s.SerialPortName == probe.SerialPortName);
            if (holder != null)
            {
                var who = holder.ScaleId == probe.ScaleId
                    ? "this scale is active, so its reader already holds the port"
                    : $"the active scale '{holder.ScaleId}' is using it";
                await SafeDetectReply(requestId, new
                {
                    ok = false,
                    error = $"Serial port {probe.SerialPortName} is busy — {who}. "
                          + "Set Active to No and Save, then run Auto-Detect."
                });
                return;
            }
        }

        CaptureResult capture;
        try
        {
            using var cts = new CancellationTokenSource(DetectCaptureMs + 5000);
            capture = isSerial
                ? await _serialClient.CaptureFramesAsync(probe, DetectCaptureMs, DetectMaxFrames, cts.Token)
                : await CaptureTcpFramesAsync(probe, DetectCaptureMs, DetectMaxFrames, cts.Token);
        }
        catch (Exception ex)
        {
            await SafeDetectReply(requestId, new { ok = false, error = DescribeProbeFailure(ex, probe, isSerial) });
            return;
        }

        // "Found nothing" has several very different causes and each has a different
        // fix, so distinguish them rather than reporting one vague failure.
        if (capture.Frames.Count == 0)
        {
            string where = isSerial ? probe.SerialPortName ?? "the port" : $"{probe.IpAddress}:{probe.Port}";
            string error;
            if (capture.BytesRead == 0)
            {
                error = $"Connected to {where} but no data arrived in {DetectCaptureMs / 1000} seconds. "
                      + (isSerial
                          ? "Check the baud rate, data bits and parity, that the cable is on the right port, "
                          + "and that the indicator is set to stream continuously. If it only replies when polled, "
                          + "put its command in Request Command and detect again."
                          : "Check that the indicator streams without being asked; if it only replies when polled, "
                          + "put its command in Request Command and detect again.");
            }
            else
            {
                error = $"Received {capture.BytesRead} bytes from {where}, but none were CR/LF terminated, "
                      + "so no complete frame could be read. This usually means the baud rate, data bits or "
                      + $"parity is wrong. First bytes: {capture.RawSample}";
            }
            await SafeDetectReply(requestId, new { ok = false, error });
            return;
        }

        var brands = _brands.Get().Brands;
        var detection = ScaleFormatDetector.Detect(capture.Frames, brands);

        await SafeDetectReply(requestId, new
        {
            ok = true,
            serviceId = _serviceId,
            connectionType = isSerial ? "Serial" : "TCP",
            bytesRead = capture.BytesRead,
            detection
        });
    }

    /// <summary>Turns a probe exception into something an operator can act on.</summary>
    private static string DescribeProbeFailure(Exception ex, ScaleConfigEntity probe, bool isSerial) => ex switch
    {
        UnauthorizedAccessException =>
            $"Serial port {probe.SerialPortName} is in use or access was denied.",
        FileNotFoundException =>
            $"Serial port {probe.SerialPortName} does not exist on this machine.",
        System.Net.Sockets.SocketException =>
            $"Could not connect to {probe.IpAddress}:{probe.Port} — {ex.Message}",
        OperationCanceledException =>
            isSerial
                ? $"No data arrived on {probe.SerialPortName} within the capture window."
                : $"No data arrived from {probe.IpAddress}:{probe.Port} within the capture window.",
        _ => ex.Message
    };

    /// <summary>
    /// Reply helper that never throws. If the hub connection dropped mid-detect there
    /// is nobody to tell, and an exception here would escape onto a background task.
    /// </summary>
    private async Task SafeDetectReply(string requestId, object payload)
    {
        try
        {
            if (_connection?.State != HubConnectionState.Connected) return;
            await _connection.InvokeAsync("ScaleFormatDetectResult", new
            {
                requestId,
                serviceId = _serviceId,
                result = payload
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not deliver detect result for {RequestId}: {Msg}", requestId, ex.Message);
        }
    }

    /// <summary>
    /// Reads CR-delimited frames from a streaming TCP indicator. Used both by
    /// Auto-Detect and by the continuous TCP poll path.
    /// </summary>
    private static async Task<CaptureResult> CaptureTcpFramesAsync(
        ScaleConfigEntity scale, int captureMs, int maxFrames, CancellationToken ct)
    {
        var result = new CaptureResult();
        var frames = result.Frames;
        var rawBytes = new List<byte>();
        using var client = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(captureMs + 2000);

        await client.ConnectAsync(scale.IpAddress, scale.Port, cts.Token);
        using var ns = client.GetStream();

        // Some indicators stream only after a nudge; send the request command once
        // if one is configured, then just listen.
        if (!string.IsNullOrWhiteSpace(scale.RequestCommand))
        {
            var cmd = System.Text.Encoding.ASCII.GetBytes(
                scale.RequestCommand.Replace("\\r", "\r").Replace("\\n", "\n"));
            await ns.WriteAsync(cmd, cts.Token);
            await ns.FlushAsync(cts.Token);
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(captureMs);
        var buffer = new byte[512];
        var pending = new StringBuilder();

        while (DateTime.UtcNow < deadline && frames.Count < maxFrames && !cts.Token.IsCancellationRequested)
        {
            if (!ns.DataAvailable)
            {
                await Task.Delay(20, cts.Token);
                continue;
            }

            int read = await ns.ReadAsync(buffer, cts.Token);
            if (read <= 0) break;
            for (int i = 0; i < read; i++) rawBytes.Add(buffer[i]);
            pending.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));

            // Split on CR or LF; indicators vary, and blank segments are dropped.
            var text = pending.ToString();
            int cut;
            while ((cut = text.IndexOfAny(new[] { '\r', '\n' })) >= 0)
            {
                var line = text.Substring(0, cut).Trim('\0', '\x02', '\x03');
                if (!string.IsNullOrWhiteSpace(line)) frames.Add(line);
                text = text.Substring(cut + 1);
                if (frames.Count >= maxFrames) break;
            }
            pending.Clear();
            pending.Append(text);
        }

        result.BytesRead = rawBytes.Count;
        result.RawSample = BitConverter.ToString(rawBytes.Take(64).ToArray());
        if (frames.Count == 0 && pending.Length > 0) result.Unterminated = pending.ToString();
        return result;
    }

    private async Task PollScales(CancellationToken ct)
    {
        List<ScaleConfigEntity> scales;
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
            scales = await db.Scales.Where(s => s.Active).ToListAsync(ct);
        }

        if (scales.Count == 0)
        {
            _log.LogWarning("No active scales configured. Waiting for changes...");
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return;
        }

        _log.LogInformation("Starting {Count} scale poller(s).", scales.Count);

        // One independent task per scale
        var tasks = scales.Select(scale => Task.Run(() => PollSingleScale(scale, ct), ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    private Task PollSingleScale(ScaleConfigEntity scale, CancellationToken ct)
    {
        bool isSerial = string.Equals(scale.ConnectionType, "Serial", StringComparison.OrdinalIgnoreCase);
        return isSerial ? PollSerialScale(scale, ct) : PollTcpScale(scale, ct);
    }

    private async Task PollTcpScale(ScaleConfigEntity scale, CancellationToken ct)
    {
        var backoff = 2000;
        var maxBackoff = 10000;

        // Streaming indicators (typically a continuous-output scale behind a
        // serial-to-Ethernet converter) push frames unprompted. Demand-polling one
        // with QueryOnceAsync reconnects on every reading and reads whichever frame
        // happens to be mid-flight, so those get a held-open read loop instead.
        bool isStreaming = string.Equals(scale.Protocol, "Continuous", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(scale.Protocol, "Stream", StringComparison.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (isStreaming)
                {
                    await StreamTcpScale(scale, ct);
                    backoff = 2000;

                    // StreamTcpScale returns when the feed stops or the peer closes.
                    // Pause before reconnecting: an indicator that accepts and then
                    // immediately drops would otherwise be hammered in a tight loop.
                    await Task.Delay(1000, ct);
                    continue;
                }

                var (ok, weight, motion, status, rawText, rawHex) = await _smaClient.QueryOnceAsync(
                    scale.IpAddress, scale.Port, scale.RequestCommand, scale.TimeoutMs, ct);

                // Columns the operator configured for this indicator override the SMA
                // parse. Without this an Auto-Detect result would be silently ignored
                // whenever the brand's protocol happens to be demand rather than
                // continuous — the tokens would save, and change nothing.
                var demandPositions = ScaleFormatDetector.PositionTokens.From(scale);
                if (demandPositions.HasValue)
                {
                    var reparsed = ScaleFormatDetector.ParseByPositions(
                        rawText.Replace("<CR>", "").Replace("<LF>", "").TrimEnd(), demandPositions.Value);
                    weight = reparsed.Weight;
                    motion = reparsed.Motion;
                    ok = reparsed.Ok;
                    status = reparsed.Status;
                }

                await PublishReading(scale, weight, motion, ok, status, rawText, rawHex, ct);

                await Task.Delay(scale.PollingIntervalMs, ct);
                backoff = 2000; // reset after success
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Linked-CTS deadline tripped (not worker shutdown) — connect or
                // read timed out. This is the common "scale unreachable" case.
                _log.LogWarning("Scale '{Name}' at {Ip}:{Port} did not respond within {Timeout}ms. Retrying in {Backoff}ms.",
                    scale.DisplayName, scale.IpAddress, scale.Port, scale.TimeoutMs, backoff);

                await PublishDisconnected(scale, ct);

                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, maxBackoff);
            }
            catch (System.Net.Sockets.SocketException sx)
            {
                _log.LogWarning("Scale '{Name}' at {Ip}:{Port} unreachable: {Msg}. Retrying in {Backoff}ms.",
                    scale.DisplayName, scale.IpAddress, scale.Port, sx.Message, backoff);

                await PublishDisconnected(scale, ct);

                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, maxBackoff);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Scale '{Name}' at {Ip}:{Port} poll failed: {Msg}. Retrying in {Backoff}ms.",
                    scale.DisplayName, scale.IpAddress, scale.Port, ex.Message, backoff);

                await PublishDisconnected(scale, ct);

                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, maxBackoff);
            }
        }
    }

    /// <summary>
    /// Holds a TCP connection open and publishes every CR-delimited frame the
    /// indicator streams. Returns when the socket closes or the token trips, so the
    /// caller's existing catch/backoff blocks handle reconnection. Frames are parsed
    /// by exactly the same code the serial streaming path uses, so a given indicator
    /// behaves identically whether it's wired to a COM port or an Ethernet converter.
    /// </summary>
    private async Task StreamTcpScale(ScaleConfigEntity scale, CancellationToken ct)
    {
        var brandRegex = _serialClient.GetBrandRegex(scale);
        var positions = ScaleFormatDetector.PositionTokens.From(scale);

        using var client = new System.Net.Sockets.TcpClient();
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(scale.TimeoutMs > 0 ? scale.TimeoutMs : 2000);
        await client.ConnectAsync(scale.IpAddress, scale.Port, connectCts.Token);

        using var ns = client.GetStream();
        _log.LogInformation("Streaming scale '{ScaleId}' from {Ip}:{Port}{Mode}",
            scale.ScaleId, scale.IpAddress, scale.Port,
            positions.HasValue ? " (column positions)" : "");

        // Some indicators need a nudge before they start streaming.
        if (!string.IsNullOrWhiteSpace(scale.RequestCommand))
        {
            var cmd = Encoding.ASCII.GetBytes(scale.RequestCommand.Replace("\\r", "\r").Replace("\\n", "\n"));
            await ns.WriteAsync(cmd, ct);
            await ns.FlushAsync(ct);
        }

        var buffer = new byte[512];
        var pending = new StringBuilder();
        DateTime nextBroadcast = DateTime.MinValue;
        DateTime lastData = DateTime.UtcNow;
        int silenceTimeoutMs = Math.Max(scale.TimeoutMs > 0 ? scale.TimeoutMs * 5 : 5000, 5000);

        while (!ct.IsCancellationRequested)
        {
            if (!ns.DataAvailable)
            {
                // A stream that has gone quiet is a disconnected scale. Drop out so
                // the caller reports it and reconnects rather than blocking forever.
                if ((DateTime.UtcNow - lastData).TotalMilliseconds > silenceTimeoutMs)
                {
                    _log.LogWarning("Scale '{ScaleId}' stopped streaming for {Ms}ms. Reconnecting.",
                        scale.ScaleId, silenceTimeoutMs);
                    await PublishDisconnected(scale, ct);
                    return;
                }
                await Task.Delay(20, ct);
                continue;
            }

            int read = await ns.ReadAsync(buffer, ct);
            if (read <= 0) return; // peer closed
            lastData = DateTime.UtcNow;
            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var text = pending.ToString();
            int cut;
            while ((cut = text.IndexOfAny(new[] { '\r', '\n' })) >= 0)
            {
                var line = text.Substring(0, cut).Trim('\0', '\x02', '\x03');
                text = text.Substring(cut + 1);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var frame = SerialScaleClient.ParseSerialFrame(line, brandRegex, positions);
                frame.RawText = line;
                frame.RawHex = BitConverter.ToString(Encoding.ASCII.GetBytes(line));

                _weightStore.Update(scale.ScaleId, new ScaleReading
                {
                    ScaleId = scale.ScaleId,
                    DisplayName = scale.DisplayName,
                    Weight = frame.Weight,
                    Motion = frame.Motion,
                    Ok = frame.Ok,
                    Status = frame.Status,
                    RawResponse = frame.RawText,
                    RawHex = frame.RawHex,
                    LastUpdate = DateTime.Now
                });

                // Same throttle the serial streaming path applies — a 10Hz indicator
                // must not turn into 10 SignalR broadcasts a second.
                var now = DateTime.UtcNow;
                if (now < nextBroadcast) continue;
                nextBroadcast = now.AddMilliseconds(scale.PollingIntervalMs > 0 ? scale.PollingIntervalMs : 250);

                if (_connection?.State == HubConnectionState.Connected)
                {
                    await _connection.InvokeAsync("ScaleWeight", new
                    {
                        serviceId = _serviceId,
                        scaleId = scale.ScaleId,
                        displayName = scale.DisplayName,
                        weight = frame.Weight,
                        motion = frame.Motion,
                        ok = frame.Ok,
                        status = frame.Status,
                        onScale = IsOnScale(scale),
                        rawResponse = frame.RawText,
                        rawHex = frame.RawHex,
                        lastUpdate = DateTime.Now
                    }, ct);
                }
            }
            pending.Clear();
            pending.Append(text);
        }
    }

    private async Task PollSerialScale(ScaleConfigEntity scale, CancellationToken ct)
    {
        var backoff = 2000;
        var maxBackoff = 10000;
        DateTime nextBroadcast = DateTime.MinValue;

        // OnDemand protocols send a request command and read one reply at a time;
        // continuous protocols (IQ355) just open the port and read whatever streams.
        bool isOnDemand = string.Equals(scale.Protocol, "OnDemand", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(scale.Protocol, "Demand", StringComparison.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Func<SerialFrame, Task> handler = async frame =>
                {
                    // Always update the in-memory store with the latest frame
                    _weightStore.Update(scale.ScaleId, new ScaleReading
                    {
                        ScaleId = scale.ScaleId,
                        DisplayName = scale.DisplayName,
                        Weight = frame.Weight,
                        Motion = frame.Motion,
                        Ok = frame.Ok,
                        Status = frame.Status,
                        RawResponse = frame.RawText,
                        RawHex = frame.RawHex,
                        LastUpdate = DateTime.Now
                    });

                    // Throttle SignalR broadcasts using PollingIntervalMs
                    var now = DateTime.UtcNow;
                    if (now < nextBroadcast) return;
                    nextBroadcast = now.AddMilliseconds(scale.PollingIntervalMs > 0 ? scale.PollingIntervalMs : 250);

                    if (_connection?.State == HubConnectionState.Connected)
                    {
                        await _connection.InvokeAsync("ScaleWeight", new
                        {
                            serviceId = _serviceId,
                            scaleId = scale.ScaleId,
                            displayName = scale.DisplayName,
                            weight = frame.Weight,
                            motion = frame.Motion,
                            ok = frame.Ok,
                            status = frame.Status,
                            onScale = IsOnScale(scale),
                            rawResponse = frame.RawText,
                            rawHex = frame.RawHex,
                            lastUpdate = DateTime.Now
                        }, ct);
                    }
                };

                if (isOnDemand)
                {
                    await _serialClient.PollOnDemandAsync(
                        scale,
                        scale.RequestCommand ?? "",
                        scale.PollingIntervalMs > 0 ? scale.PollingIntervalMs : 500,
                        handler,
                        ct);
                }
                else
                {
                    await _serialClient.ReadStreamAsync(scale, handler, ct);
                }

                backoff = 2000;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("Scale '{Name}' serial read failed: {Msg}. Closing port; will reopen in {Backoff}ms.",
                    scale.DisplayName, ex.Message, backoff);

                await PublishDisconnected(scale, ct);

                try { await Task.Delay(backoff, ct); }
                catch (OperationCanceledException) { break; }
                backoff = Math.Min(backoff * 2, maxBackoff);
                _log.LogInformation("Scale '{Name}' attempting reconnect on {Port}...",
                    scale.DisplayName, scale.SerialPortName);
            }
        }
    }

    private async Task PublishReading(ScaleConfigEntity scale, int weight, bool motion, bool ok,
        string status, string rawText, string rawHex, CancellationToken ct)
    {
        _weightStore.Update(scale.ScaleId, new ScaleReading
        {
            ScaleId = scale.ScaleId,
            DisplayName = scale.DisplayName,
            Weight = weight,
            Motion = motion,
            Ok = ok,
            Status = status,
            RawResponse = rawText,
            RawHex = rawHex,
            LastUpdate = DateTime.Now
        });

        if (_connection?.State == HubConnectionState.Connected)
        {
            await _connection.InvokeAsync("ScaleWeight", new
            {
                serviceId = _serviceId,
                scaleId = scale.ScaleId,
                displayName = scale.DisplayName,
                weight,
                motion,
                ok,
                status,
                onScale = IsOnScale(scale),
                rawResponse = rawText,
                rawHex,
                lastUpdate = DateTime.Now
            }, ct);
        }
    }

    private async Task PublishDisconnected(ScaleConfigEntity scale, CancellationToken ct)
    {
        _weightStore.Update(scale.ScaleId, new ScaleReading
        {
            ScaleId = scale.ScaleId,
            DisplayName = scale.DisplayName,
            Weight = 0,
            Motion = false,
            Ok = false,
            Status = "Disconnected",
            LastUpdate = DateTime.Now
        });

        if (_connection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _connection.InvokeAsync("ScaleWeight", new
                {
                    serviceId = _serviceId,
                    scaleId = scale.ScaleId,
                    displayName = scale.DisplayName,
                    weight = 0,
                    motion = false,
                    ok = false,
                    status = "Disconnected",
                    // A dead feed has no detector opinion worth acting on, and
                    // the scale error stops the weighment on its own.
                    onScale = true,
                    lastUpdate = DateTime.Now
                }, ct);
            }
            catch { /* ignore */ }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            try { await _connection.DisposeAsync(); }
            catch { /* ignore */ }
        }
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Retry policy that never gives up — backs off 2s, 5s, 10s, then stays at 30s forever.
/// </summary>
public class ForeverRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] Delays = new[]
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    };

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var idx = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);
        return Delays[idx];
    }
}
