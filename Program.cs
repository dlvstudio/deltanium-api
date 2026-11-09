using DeltaniumApi.Data;
using DeltaniumApi.Services;
using DeltaniumApi.Middleware;
using DeltaniumApi.Security;
using Microsoft.EntityFrameworkCore;
using Serilog;

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

// Allow INTERNAL_API_TOKEN env to override empty appsettings value
var envInternalToken = Environment.GetEnvironmentVariable("INTERNAL_API_TOKEN");
if (!string.IsNullOrWhiteSpace(envInternalToken))
    builder.Configuration["InternalApi:Token"] = envInternalToken;

builder.Services.AddSingleton<KeyService>();
builder.Services.AddScoped<SignatureAuthHelper>();

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
}
else
{
    builder.Services.AddSingleton<IStorageService, StorageService>();
}

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
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        if (origins != null && origins.Length > 0)
        {
            policy.WithOrigins(origins)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
        else if (builder.Environment.IsDevelopment())
        {
            // Dev-only fallback
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
        }
        else
        {
            // Fail closed in non-dev if not configured
            policy.WithOrigins("https://localhost").AllowAnyMethod().AllowAnyHeader();
        }
    });
});

var app = builder.Build();
Log.Information("API starting up at {Started} with Storage:Provider={Provider}",
    DateTime.UtcNow.ToString("HH:mm:ss.fff"), storageProvider);

if (string.Equals(storageProvider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
    db.Database.EnsureCreated();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();
app.UseRequestResponseLogging();
app.MapControllers();
app.Run();
