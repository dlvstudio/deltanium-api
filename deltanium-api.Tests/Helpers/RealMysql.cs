using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DeltaniumApi.Data;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace deltanium_api.Tests.Helpers
{
    public static class RealMysql
    {
        public static string? TryGetConnectionString(string databaseHint = "deltanium_api")
        {
            var env = Environment.GetEnvironmentVariable("DELTANIUM_MYSQL_CS");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            var secretsId = databaseHint switch
            {
                "deltanium_blocker" => "deltanium-blocker-local-secrets",
                "deltanium_store" => "deltanium-store-local-secrets",
                _ => "deltanium-api-local-secrets"
            };

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".microsoft", "usersecrets", secretsId, "secrets.json");

            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("ConnectionStrings:Default", out var cs))
                return cs.GetString();

            return null;
        }

        public static ApiDbContext? CreateApiContextOrNull()
        {
            var cs = TryGetConnectionString("deltanium_api");
            if (string.IsNullOrWhiteSpace(cs))
                return null;

            var options = new DbContextOptionsBuilder<ApiDbContext>()
                .UseMySql(cs, new MySqlServerVersion(new Version(8, 4, 0)))
                .Options;
            var db = new ApiDbContext(options);
            db.Database.EnsureCreated();
            return db;
        }
    }
}
