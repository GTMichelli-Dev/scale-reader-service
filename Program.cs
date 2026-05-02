using ScaleReaderService;
using ScaleReaderService.Data;
using ScaleReaderService.Models;
using ScaleReaderService.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.OpenApi.Models;

var version = typeof(ScaleWorker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

// Show the version up-front so it's the first thing on the console — before
// framework startup logs flood in. Logger banner below repeats it for log files.
try { Console.Title = $"Scale Reader Service v{version}"; } catch { /* not a real console */ }
Console.WriteLine();
Console.WriteLine("============================================");
Console.WriteLine($"  Scale Reader Service v{version}");
Console.WriteLine("============================================");
Console.WriteLine();

var builder = WebApplication.CreateBuilder(args);

// Suppress noisy EF Core command/query warnings
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Error);

// Windows Service support
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BasicWeigh Scale Reader Service";
});

// SQLite database for scale configs and service settings
var dbPath = Path.Combine(AppContext.BaseDirectory, "scalereaderservice.db");
builder.Services.AddDbContext<ScaleDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Brands cache — refreshed on demand by API/SignalR callers, persists remote responses.
builder.Services.AddSingleton<BrandsCache>();

// HTTP client for calling web API
builder.Services.AddHttpClient("BasicWeighApi", client =>
{
    var serverUrl = builder.Configuration["Scale:ServerUrl"] ?? "http://localhost:5110";
    client.BaseAddress = new Uri(serverUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// SMA client (scale protocol handler)
builder.Services.AddSingleton<SmaClient>();

// Serial (RS-232) scale client — IQ355 streaming format
builder.Services.AddSingleton<SerialScaleClient>();

// Restart signal (triggered when settings change via API)
builder.Services.AddSingleton<RestartSignal>();

// Announce signal (triggered when scales change via API)
builder.Services.AddSingleton<AnnounceSignal>();

// In-memory weight store (latest reading per scale)
builder.Services.AddSingleton<ScaleWeightStore>();

// Controllers + Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Scale Reader Service API",
        Version = "v1",
        Description = "Health, status, configuration, and scale management endpoints for the Scale Reader Service."
    });
});

// Background worker
builder.Services.AddHostedService<ScaleWorker>();

// Event Log on Windows
if (OperatingSystem.IsWindows())
{
    builder.Logging.AddEventLog(new EventLogSettings
    {
        SourceName = "ScaleReaderService"
    });
}

var app = builder.Build();

// Auto-create/migrate database
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ScaleDbContext>();
    db.Database.EnsureCreated();

    // Add columns introduced after the initial schema. EnsureCreated() does not migrate
    // existing tables, so we need to add new fields by hand for upgrades.
    AddColumnIfMissing(db, "Scales", "ConnectionType", "TEXT NOT NULL DEFAULT 'TCP'");
    AddColumnIfMissing(db, "Scales", "Protocol", "TEXT NOT NULL DEFAULT 'SMA'");
    AddColumnIfMissing(db, "Scales", "SerialPortName", "TEXT NULL");
    AddColumnIfMissing(db, "Scales", "BaudRate", "INTEGER NOT NULL DEFAULT 9600");
    AddColumnIfMissing(db, "Scales", "DataBits", "INTEGER NOT NULL DEFAULT 8");
    AddColumnIfMissing(db, "Scales", "Parity", "TEXT NOT NULL DEFAULT 'None'");
    AddColumnIfMissing(db, "Scales", "StopBits", "INTEGER NOT NULL DEFAULT 1");

    // Seed from appsettings.json if no scales exist yet
    if (!db.Scales.Any())
    {
        var scalesConfig = builder.Configuration.GetSection("Scales").Get<List<ScaleOptions>>();
        if (scalesConfig != null)
        {
            foreach (var s in scalesConfig)
            {
                db.Scales.Add(new ScaleConfigEntity
                {
                    ScaleId = $"scale-{s.Id}",
                    DisplayName = s.Description,
                    ScaleBrand = "Generic SMA",
                    IpAddress = s.IpAddress,
                    Port = s.Port,
                    Active = true
                });
            }
            db.SaveChanges();
        }
    }

    // Update settings from appsettings if ServerUrl changed
    var settings = db.Settings.OrderBy(s => s.Id).FirstOrDefault();
    if (settings != null)
    {
        var configUrl = builder.Configuration["Scale:ServerUrl"];
        if (!string.IsNullOrWhiteSpace(configUrl) && settings.ServerUrl == "http://localhost:5110")
        {
            settings.ServerUrl = configUrl;
            db.SaveChanges();
        }
    }
}

// Warm the brands cache once on startup — failure is non-fatal because
// API/SignalR callers will retry the fetch on demand.
_ = Task.Run(() => app.Services.GetRequiredService<BrandsCache>().RefreshAsync());

// Swagger
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scale Reader Service API v1");
});

app.MapControllers();

// Startup banner
var urls = builder.Configuration["Urls"] ?? "http://localhost:5220";
var logger2 = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ScaleReaderService");
logger2.LogInformation("============================================");
logger2.LogInformation("  Scale Reader Service v{Version}", version);
logger2.LogInformation("  Swagger: {Urls}/swagger", urls);
logger2.LogInformation("============================================");

await app.RunAsync();

static void AddColumnIfMissing(ScaleDbContext db, string table, string column, string definition)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();

    using (var check = conn.CreateCommand())
    {
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            // PRAGMA table_info: cid, name, type, notnull, dflt_value, pk
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
    }

    using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
    alter.ExecuteNonQuery();
}
