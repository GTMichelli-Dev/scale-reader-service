using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScaleReaderService.Data;
using ScaleReaderService.Models;
using ScaleReaderService.Services;

namespace ScaleReaderService.Controllers;

[ApiController]
public class StatusController : Controller
{
    private readonly ScaleDbContext _db;
    private readonly ScaleWeightStore _weightStore;

    public StatusController(ScaleDbContext db, ScaleWeightStore weightStore)
    {
        _db = db;
        _weightStore = weightStore;
    }

    // ===== HEALTH =====

    [HttpGet("api/status/health")]
    public IActionResult Health()
    {
        var scaleCount = _db.Scales.Count(s => s.Active);
        return Ok(new { status = "ok", activeScales = scaleCount });
    }

    // ===== LIVE WEIGHT =====

    /// <summary>
    /// Get the latest weight reading from a specific scale.
    /// </summary>
    [HttpGet("api/weight/{scaleId}")]
    public IActionResult GetWeight(string scaleId)
    {
        var reading = _weightStore.Get(scaleId);
        if (reading == null)
            return NotFound(new { error = $"No reading for scale '{scaleId}'. Scale may not be active or hasn't been polled yet." });
        return Ok(reading);
    }

    /// <summary>
    /// Diagnostic: get the last raw SMA response for all active scales.
    /// Shows raw text (with LF/CR markers), hex bytes, parsed values, and timing.
    /// </summary>
    [HttpGet("api/diagnostic")]
    public IActionResult GetDiagnostic()
    {
        var readings = _weightStore.GetAll();
        var result = readings.Values.OrderBy(r => r.ScaleId).Select(r => new
        {
            r.ScaleId,
            r.DisplayName,
            parsed = new { r.Weight, r.Motion, r.Ok, r.Status },
            raw = new { text = r.RawResponse, hex = r.RawHex },
            r.LastUpdate
        });
        return Ok(result);
    }

    /// <summary>
    /// Diagnostic: get the last raw SMA response for a specific scale.
    /// </summary>
    [HttpGet("api/diagnostic/{scaleId}")]
    public IActionResult GetDiagnosticForScale(string scaleId)
    {
        var reading = _weightStore.Get(scaleId);
        if (reading == null)
            return NotFound(new { error = $"No reading for scale '{scaleId}'." });
        return Ok(new
        {
            reading.ScaleId,
            reading.DisplayName,
            parsed = new { reading.Weight, reading.Motion, reading.Ok, reading.Status },
            raw = new { text = reading.RawResponse, hex = reading.RawHex },
            reading.LastUpdate
        });
    }

    // ===== SETTINGS =====

    [HttpGet("api/settings")]
    public IActionResult GetSettings()
    {
        var settings = _db.Settings.OrderBy(s => s.Id).FirstOrDefault();
        return Ok(settings);
    }

    [HttpPut("api/settings")]
    public IActionResult UpdateSettings([FromBody] ServiceSettings update)
    {
        var settings = _db.Settings.OrderBy(s => s.Id).FirstOrDefault();
        if (settings == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(update.ServiceId)) settings.ServiceId = update.ServiceId;
        if (!string.IsNullOrWhiteSpace(update.ServerUrl)) settings.ServerUrl = update.ServerUrl;
        if (!string.IsNullOrWhiteSpace(update.SignalRHub)) settings.SignalRHub = update.SignalRHub;
        if (update.BrandsUrl != null) settings.BrandsUrl = update.BrandsUrl;
        if (update.BrandsToken != null) settings.BrandsToken = update.BrandsToken;

        _db.SaveChanges();

        // RestartSignal is a SOFT signal — it cancels the worker's connect
        // loop so it picks up the new ServerUrl/BrandsUrl on the next pass.
        // It does NOT exit the host process; systemd doesn't see it as a
        // stop. Operators sometimes mistake the brief reconnect log gap
        // for the service dying; the log line below makes intent clear.
        var logger = HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("SettingsApi");
        logger?.LogInformation("Settings updated via API — signaling worker to refresh connection. Service stays running.");
        var restart = HttpContext.RequestServices.GetService<RestartSignal>();
        restart?.TriggerRestart();

        return Ok(settings);
    }

    // ===== SCALES CRUD =====

    [HttpGet("api/scales")]
    public IActionResult GetAll()
    {
        var scales = _db.Scales.OrderBy(s => s.ScaleId).ToList();
        return Ok(scales);
    }

    [HttpGet("api/scales/{scaleId}")]
    public IActionResult GetById(string scaleId)
    {
        var scale = _db.Scales.AsNoTracking().FirstOrDefault(s => s.ScaleId == scaleId);
        if (scale == null) return NotFound();
        return Ok(scale);
    }

    [HttpPost("api/scales")]
    public IActionResult Create([FromBody] ScaleConfigEntity scale)
    {
        if (string.IsNullOrWhiteSpace(scale.ScaleId))
            return BadRequest("ScaleId is required");

        if (_db.Scales.AsNoTracking().Any(s => s.ScaleId == scale.ScaleId))
            return Conflict($"Scale '{scale.ScaleId}' already exists");

        scale.Id = 0;
        _db.Scales.Add(scale);
        _db.SaveChanges();

        var announce = HttpContext.RequestServices.GetService<AnnounceSignal>();
        announce?.TriggerAnnounce();

        return Created($"/api/scales/{scale.ScaleId}", scale);
    }

    [HttpPut("api/scales/{scaleId}")]
    public IActionResult Update(string scaleId, [FromBody] ScaleConfigEntity update)
    {
        var existing = _db.Scales.FirstOrDefault(s => s.ScaleId == scaleId);
        if (existing == null) return NotFound();

        existing.DisplayName = update.DisplayName ?? existing.DisplayName;
        existing.ScaleBrand = update.ScaleBrand ?? existing.ScaleBrand;
        if (!string.IsNullOrWhiteSpace(update.ConnectionType)) existing.ConnectionType = update.ConnectionType;
        if (!string.IsNullOrWhiteSpace(update.Protocol)) existing.Protocol = update.Protocol;
        existing.IpAddress = update.IpAddress ?? existing.IpAddress;
        existing.Port = update.Port > 0 ? update.Port : existing.Port;
        existing.SerialPortName = update.SerialPortName ?? existing.SerialPortName;
        if (update.BaudRate > 0) existing.BaudRate = update.BaudRate;
        if (update.DataBits > 0) existing.DataBits = update.DataBits;
        if (!string.IsNullOrWhiteSpace(update.Parity)) existing.Parity = update.Parity;
        if (update.StopBits >= 0) existing.StopBits = update.StopBits;
        existing.RequestCommand = update.RequestCommand;
        existing.PollingIntervalMs = update.PollingIntervalMs > 0 ? update.PollingIntervalMs : existing.PollingIntervalMs;
        existing.TimeoutMs = update.TimeoutMs > 0 ? update.TimeoutMs : existing.TimeoutMs;
        existing.Active = update.Active;

        _db.SaveChanges();

        var announce = HttpContext.RequestServices.GetService<AnnounceSignal>();
        announce?.TriggerAnnounce();

        return Ok(existing);
    }

    [HttpDelete("api/scales/{scaleId}")]
    public IActionResult Delete(string scaleId)
    {
        var existing = _db.Scales.FirstOrDefault(s => s.ScaleId == scaleId);
        if (existing == null) return NotFound();

        _db.Scales.Remove(existing);
        _db.SaveChanges();

        var announce = HttpContext.RequestServices.GetService<AnnounceSignal>();
        announce?.TriggerAnnounce();

        return Ok(new { deleted = scaleId });
    }

    // ===== COMMISSIONING =====

    /// <summary>Serial ports this machine offers, for the setup screen's port picker.</summary>
    [HttpGet("api/serialports")]
    public IActionResult SerialPorts() => Ok(SerialScaleClient.ListPorts());

    /// <summary>
    /// Run format detection against frames you already captured, without touching
    /// hardware. This is the tool for working out an undocumented indicator: paste
    /// what it streams, see which columns hold the weight and the motion flag.
    /// Accepts either a list of text frames or a list of hex frames ("02-31-32-03").
    /// </summary>
    [HttpPost("api/detect")]
    public async Task<IActionResult> Detect([FromBody] DetectRequest body)
    {
        var frames = new List<string>();

        if (body?.Frames is { Count: > 0 })
            frames.AddRange(body.Frames);

        if (body?.HexFrames is { Count: > 0 })
        {
            foreach (var hex in body.HexFrames)
            {
                try
                {
                    var bytes = hex.Split(new[] { '-', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(h => Convert.ToByte(h, 16)).ToArray();
                    frames.Add(System.Text.Encoding.ASCII.GetString(bytes));
                }
                catch (Exception ex)
                {
                    return BadRequest(new { message = $"Could not read '{hex}' as hex: {ex.Message}" });
                }
            }
        }

        if (frames.Count == 0)
            return BadRequest(new { message = "Supply at least one frame in 'frames' or 'hexFrames'." });

        // Brand matching is best-effort: if the definitions can't be reached the
        // position detection is still perfectly useful on its own.
        List<ScaleBrandDefinition>? brands = null;
        try
        {
            var cache = HttpContext.RequestServices.GetRequiredService<BrandsCache>();
            brands = (await cache.RefreshAsync()).Brands;
        }
        catch { /* fall through with brands = null */ }

        return Ok(ScaleFormatDetector.Detect(frames, brands));
    }

    public class DetectRequest
    {
        public List<string>? Frames { get; set; }
        public List<string>? HexFrames { get; set; }
    }

    // ===== BRANDS =====

    /// <summary>
    /// Returns the current brand definitions. Each call attempts to refresh from
    /// the configured remote URL; on success the local cache is updated. The
    /// response carries Source ("remote"|"local") + Error so the caller can warn
    /// when a stale local fallback is being used.
    /// </summary>
    [HttpGet("api/status/brands")]
    public async Task<IActionResult> GetBrands()
    {
        var cache = HttpContext.RequestServices.GetRequiredService<Services.BrandsCache>();
        var result = await cache.RefreshAsync();
        return Ok(new
        {
            brands = result.Brands,
            source = result.Source,
            remoteUrl = result.RemoteUrl,
            error = result.Error
        });
    }
}
