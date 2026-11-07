using DeltaniumApi.Data;
using Microsoft.EntityFrameworkCore;

namespace deltanium_api.Tests.Helpers
{
    public static class InMemoryDb
    {
        public static ApiDbContext CreateContext(string? name = null)
        {
            var options = new DbContextOptionsBuilder<ApiDbContext>()
                .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N"))
                .Options;
            var db = new ApiDbContext(options);
            db.Database.EnsureCreated();
            return db;
        }
    }
}
