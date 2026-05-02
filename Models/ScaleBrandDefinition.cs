using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScaleReaderService.Models;

/// <summary>
/// Definition of a scale brand/model from scale-models.json. The shape mirrors the JSON file —
/// any field not present in the JSON deserializes as null/0 and is omitted in API responses.
/// </summary>
public class ScaleBrandDefinition
{
    [JsonPropertyName("brand")]
    public string Brand { get; set; } = "";

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>Connection type: "TCP" or "Serial"</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>Wire-level protocol: "Continuous", "Demand", "IQ355", "SDS", etc.</summary>
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    // ---- Serial defaults ----

    [JsonPropertyName("baudRate")]
    public int? BaudRate { get; set; }

    [JsonPropertyName("dataBits")]
    public int? DataBits { get; set; }

    [JsonPropertyName("parity")]
    public string? Parity { get; set; }

    [JsonPropertyName("stopBits")]
    public int? StopBits { get; set; }

    // ---- TCP defaults ----

    [JsonPropertyName("defaultPort")]
    public int? DefaultPort { get; set; }

    // ---- Protocol details ----

    /// <summary>Demand-mode command sent to request a weight reading.</summary>
    [JsonPropertyName("demandCommand")]
    public string? DemandCommand { get; set; }

    /// <summary>Print command (sent over the connection to trigger the scale's print function).</summary>
    [JsonPropertyName("printCommand")]
    public string? PrintCommand { get; set; }

    /// <summary>Zero command for the scale.</summary>
    [JsonPropertyName("zeroCommand")]
    public string? ZeroCommand { get; set; }

    /// <summary>Mettler Toledo SDS shared-data item name (e.g. "Weight.Net").</summary>
    [JsonPropertyName("sdsSharedDataName")]
    public string? SdsSharedDataName { get; set; }

    /// <summary>Regex to extract weight (and unit) from the response line.</summary>
    [JsonPropertyName("weightRegex")]
    public string? WeightRegex { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Loads brand definitions: tries the remote URL first, persists the response
    /// to the local file on success, and falls back to the local file on failure.
    /// Returns a rich result so callers can tell the user when the local fallback
    /// was used.
    /// </summary>
    public static async Task<BrandsLoadResult> LoadBrandsAsync(
        string? remoteUrl, string localPath, string? token, ILogger logger)
    {
        // Try remote first so the local file gets refreshed on every call.
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                if (!string.IsNullOrWhiteSpace(token))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var json = await http.GetStringAsync(remoteUrl);
                var remote = JsonSerializer.Deserialize<List<ScaleBrandDefinition>>(json);
                if (remote != null && remote.Count > 0)
                {
                    // Persist what we got back so subsequent offline starts use the latest.
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                        await File.WriteAllTextAsync(localPath, json);
                    }
                    catch (Exception writeEx)
                    {
                        logger.LogWarning("Fetched brands but failed to update local cache at {Path}: {Msg}",
                            localPath, writeEx.Message);
                    }
                    logger.LogInformation("Loaded {Count} scale brands from remote: {Url}", remote.Count, remoteUrl);
                    return new BrandsLoadResult(remote, "remote", remoteUrl, null);
                }
                return new BrandsLoadResult(
                    await ReadLocalAsync(localPath, logger), "local", remoteUrl,
                    "Remote returned no entries.");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not load remote scale brands from {Url}. Using local. Error: {Msg}",
                    remoteUrl, ex.Message);
                return new BrandsLoadResult(
                    await ReadLocalAsync(localPath, logger), "local", remoteUrl, ex.Message);
            }
        }

        return new BrandsLoadResult(await ReadLocalAsync(localPath, logger), "local", null, null);
    }

    private static async Task<List<ScaleBrandDefinition>> ReadLocalAsync(string localPath, ILogger logger)
    {
        if (!File.Exists(localPath)) return new List<ScaleBrandDefinition>();
        try
        {
            var json = await File.ReadAllTextAsync(localPath);
            var brands = JsonSerializer.Deserialize<List<ScaleBrandDefinition>>(json);
            logger.LogInformation("Loaded {Count} scale brands from local: {Path}", brands?.Count ?? 0, localPath);
            return brands ?? new List<ScaleBrandDefinition>();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to load local scale brands: {Msg}", ex.Message);
            return new List<ScaleBrandDefinition>();
        }
    }
}

/// <summary>
/// Result of a brands load — Source is "remote" when the GitHub fetch succeeded
/// (in which case the local file was refreshed), "local" when the fallback was
/// used. Error is set when the remote fetch failed so the caller can surface it.
/// </summary>
public record BrandsLoadResult(
    List<ScaleBrandDefinition> Brands,
    string Source,
    string? RemoteUrl,
    string? Error);
