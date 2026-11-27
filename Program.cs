using DeltaniumApi.Data;
using DeltaniumApi.Services;
using DeltaniumApi.Middleware;
using DeltaniumApi.Security;
using Microsoft.EntityFrameworkCore;
using Serilog;

// Server keygen for service keys (Central decision key, node keys): prints {"publicKey","privateKey":"dpapi:..."}.
// The private key is DPAPI-encrypted (LocalMachine) inside this process and never printed in plaintext.
// Used by scripts/deploy/New-DeltaniumTestnet.ps1. Windows only; does not start the API.
if (args.Any(a => string.Equals(a, "--new-dpapi-key", StringComparison.OrdinalIgnoreCase)))
{
    try
    {
        var key = new NBitcoin.Key();
        var protectedKey = DpapiSecretProtector.Protect(key.ToHex());
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { publicKey = key.PubKey.ToHex(), privateKey = protectedKey }));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }
    return;
}

// Explicit keygen (laptop) — does not start the API or write keys during normal run.
if (args.Any(a => string.Equals(a, "--generate-super-admin-keys", StringComparison.OrdinalIgnoreCase)))
{
    var overwrite = args.Any(a => string.Equals(a, "--overwrite", StringComparison.OrdinalIgnoreCase));
    string? outPath = null;
    var outIdx = Array.FindIndex(args, a => string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase));
    if (outIdx >= 0 && outIdx + 1 < args.Length)
        outPath = args[outIdx + 1];

    try
    {
        var (jsonPath, pdfPath) = KeyService.GenerateSuperAdminKeysFile(outPath, overwrite);
        var json = File.ReadAllText(jsonPath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var pub = doc.RootElement.GetProperty("PublicKey").GetString();
        Console.WriteLine($"Wrote Super Admin keypair JSON: {jsonPath}");
        Console.WriteLine($"Wrote Super Admin keypair PDF:  {pdfPath}");
        Console.WriteLine($"PublicKey={pub}");
        Console.WriteLine("Keep PrivateKey offline (JSON + PDF). Do not commit or upload to API host.");
        Console.WriteLine("On API + each blocker set only: SUPER_ADMIN_PUBLIC_KEY=<PublicKey>");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }

    return;
}

// ProgramData secrets survive MSDeploy; then decrypt dpapi:* into process env.
DpapiEnvironment.LoadSiteProgramDataSecrets();
DpapiEnvironment.ExpandProcessEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddDpapiDecryptedOverrides();

// Serilog daily-rolling file logger (aligned with Store)
var logsDir = Path.Combine(Directory.GetCurrentDirectory(), "logs");
Directory.CreateDirectory(logsDir);
var logsPath = Path.Combine(logsDir, "api-.log");

// Initialize manual logger for real-time file logging
ManualLogger.Initialize(logsDir, "api");
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.Mvc.Infrastructure", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        path: logsPath,
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        shared: true,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        buffered: false,
        flushToDiskInterval: TimeSpan.Zero,
        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Host.UseSerilog();
builder.Logging.ClearProviders();

var storageProvider = builder.Configuration["Storage:Provider"] ?? "Json";
var connectionString = builder.Configuration.GetConnectionString("Default");


builder.Services.AddSingleton<KeyService>();
builder.Services.AddScoped<SignatureAuthHelper>();
builder.Services.AddScoped<NodeRegistrationService>();
// Requests to TCP-only nodes go over DLTP; everything else over HTTP(S) as before.
builder.Services.AddTransient<DeltaniumApi.Services.Dltp.DltpRoutingHandler>();
builder.Services.ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<DeltaniumApi.Services.Dltp.DltpRoutingHandler>());
builder.Services.AddHttpClient("blocker-tx", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("explorer", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<IBlockerTxSubmitter, BlockerTxSubmitter>();
builder.Services.AddScoped<StorageTermPublisher>();
builder.Services.AddScoped<LedgerRowsPublisher>();
builder.Services.AddScoped<IChainExplorerClient, ChainExplorerClient>();
builder.Services.AddScoped<INodeHealthService, NodeHealthService>();

if (string.Equals(storageProvider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "MySQL enabled but ConnectionStrings:Default is empty. " +
            "Set it via User Secrets (id: deltanium-api-local-secrets), e.g. " +
            "dotnet user-secrets set \"ConnectionStrings:Default\" \"Server=...;Database=deltanium_api;...\"");
    }

    var serverVersion = new MySqlServerVersion(new Version(8, 4, 0));
    builder.Services.AddDbContext<ApiDbContext>(options =>
        options.UseMySql(connectionString, serverVersion));
    builder.Services.AddScoped<IStorageService, MySqlStorageService>();
    builder.Services.AddScoped<JsonImportService>();
    builder.Services.AddScoped<IWalletService, WalletService>();
    builder.Services.AddScoped<EconDocStore>();
    builder.Services.AddScoped<EconomicsService>();
}
else
{
    builder.Services.AddSingleton<IStorageService, StorageService>();
    builder.Services.AddSingleton<IWalletService>(sp =>
        new WalletService(sp.GetRequiredService<IConfiguration>(), db: null));
    builder.Services.AddSingleton(sp => new EconDocStore(sp.GetRequiredService<IConfiguration>(), db: null));
    builder.Services.AddSingleton<EconomicsService>();
}
builder.Services.AddHttpClient("econ", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<EmailVerificationService>();
builder.Services.AddHostedService<EconomicsWorker>();
builder.Services.AddHostedService<NodeLivenessWorker>();

builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
        options.SerializerSettings.Formatting = Newtonsoft.Json.Formatting.Indented;
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // Config / ProgramData (Cors__AllowedOrigins__N) wins; else safe public defaults.
        var configured = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        var origins = configured
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (origins.Length == 0)
        {
            origins = new[]
            {
                "https://app.deltanium.com",
                "https://deltanium.com",
                "https://www.deltanium.com",
            };
        }

        if (builder.Environment.IsDevelopment())
        {
            origins = origins
                .Concat(new[]
                {
                    "http://localhost:5000",
                    "http://127.0.0.1:5000",
                    "http://localhost:8080",
                    "http://127.0.0.1:8080",
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        Log.Information("CORS AllowedOrigins: {Origins}", string.Join(", ", origins));
        policy.WithOrigins(origins)
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();
Log.Information("Central API {Version} starting on {Network}", BuildInfo.Summary, NetworkScope.Name);
Log.Information("API starting up at {Started} with Storage:Provider={Provider}",
    DateTime.UtcNow.ToString("HH:mm:ss.fff"), storageProvider);

if (string.Equals(storageProvider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    db.Database.EnsureCreated();
    // Upgrades of tables created by earlier versions (ignore "already applied" errors).
    foreach (var sql in new[]
    {
        "ALTER TABLE store_nodes ADD COLUMN TcpEndpoint VARCHAR(255) NULL;",
        "ALTER TABLE block_creator_nodes ADD COLUMN TcpEndpoint VARCHAR(255) NULL;",
    })
    {
        try { db.Database.ExecuteSqlRaw(sql); } catch { /* already applied */ }
    }

    var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
    await wallet.EnsureReadyAsync();
    Log.Information("Wallet ledger ready; treasury balance={Balance}", await wallet.GetBalanceAsync(
        app.Configuration["Economics:TreasuryPublicKey"] ?? WalletService.DefaultTreasuryKey));

    if (args.Contains("--import-json", StringComparer.OrdinalIgnoreCase))
    {
        var importDir = ".";
        var dirIndex = Array.FindIndex(args, a => string.Equals(a, "--import-dir", StringComparison.OrdinalIgnoreCase));
        if (dirIndex >= 0 && dirIndex + 1 < args.Length)
            importDir = args[dirIndex + 1];

        var importer = scope.ServiceProvider.GetRequiredService<JsonImportService>();
        await importer.ImportFromDirectoryAsync(importDir);
        Log.Information("Import finished; exiting because --import-json was passed");
        return;
    }
}
else
{
    using var scope = app.Services.CreateScope();
    var wallet = scope.ServiceProvider.GetRequiredService<IWalletService>();
    await wallet.EnsureReadyAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();
app.UseRequestResponseLogging();
app.MapControllers();
app.MapGet("/api/version", () => Results.Ok(BuildInfo.Describe("api")));
app.Run();
