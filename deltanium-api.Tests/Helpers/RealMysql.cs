using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using DeltaniumApi.Data;
using Pomelo.EntityFrameworkCore.MySql.Infrastructure;

namespace deltanium_api.Tests.Helpers
{
    public static class RealMysql
    {
        /// <summary>
        /// Only an explicit local database: DELTANIUM_MYSQL_CS pointing at localhost. Never the user-secrets
        /// connection string, which points at the production server (these tests write and delete rows).
        /// </summary>
        public static string? TryGetConnectionString(string databaseHint = "deltanium_api")
        {
            var cs = Environment.GetEnvironmentVariable("DELTANIUM_MYSQL_CS");
            if (string.IsNullOrWhiteSpace(cs)) return null;
            var host = new MySqlConnector.MySqlConnectionStringBuilder(cs).Server.Trim();
            return host is "localhost" or "127.0.0.1" or "::1" ? cs : null;
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
