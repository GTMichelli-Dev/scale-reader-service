using ScaleReaderService.Data;
using ScaleReaderService.Models;
using Microsoft.EntityFrameworkCore;

namespace ScaleReaderService.Services;

/// <summary>
/// Holds the most recent brands result. RefreshAsync re-fetches from the
/// configured remote URL (and persists it to the local cache); on failure
/// the BrandsLoadResult.Source is "local" and Error explains why.
/// Callers that want the live list (API endpoints, SignalR handlers) call
/// RefreshAsync so the user gets the freshest data; callers that just need
/// a snapshot (the worker's initial parse) can call Get.
/// </summary>
public class BrandsCache
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<BrandsCache> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BrandsLoadResult? _current;

    public BrandsCache(IServiceProvider sp, ILogger<BrandsCache> log)
    {
        _sp = sp;
        _log = log;
    }

    public BrandsLoadResult Get() =>
        _current ?? new BrandsLoadResult(new List<ScaleBrandDefinition>(), "local", null, null);

    public async Task<BrandsLoadResult> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            string? url = null;
            string? token = null;
            using (var scope = _sp.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
                var settings = await db.Settings.OrderBy(s => s.Id).FirstOrDefaultAsync();
                url = settings?.BrandsUrl;
                token = settings?.BrandsToken;
            }
            var localPath = Path.Combine(AppContext.BaseDirectory, "scale-models.json");
            _current = await ScaleBrandDefinition.LoadBrandsAsync(url, localPath, token, _log);
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }
}
