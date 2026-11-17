using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeltaniumApi.Data;
using DeltaniumApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Small document store for economics state (escrows, proofs, subscriptions, redemptions, anchors).
    /// MySQL table <c>econ_docs</c> when Storage:Provider=MySql, else one JSON file per collection
    /// under <c>Economics:DataDir</c> (default ./wallet-data/econ).
    /// </summary>
    public class EconDocStore
    {
        private readonly ApiDbContext? _db;
        private readonly bool _useMySql;
        private readonly string _dir;
        private static readonly SemaphoreSlim JsonGate = new(1, 1);
        private static readonly JsonSerializerSettings JsonSettings = new() { NullValueHandling = NullValueHandling.Include };
        private bool _ready;

        public EconDocStore(IConfiguration config, ApiDbContext? db = null)
        {
            _db = db;
            _useMySql = string.Equals(config["Storage:Provider"], "MySql", StringComparison.OrdinalIgnoreCase) && db != null;
            var baseDir = config["Economics:DataDir"] is { Length: > 0 } d
                ? d
                : Path.Combine(Directory.GetCurrentDirectory(), "wallet-data");
            _dir = Path.Combine(baseDir, "econ");
        }

        public async Task EnsureReadyAsync()
        {
            if (_ready) return;
            if (_useMySql)
            {
                await _db!.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS econ_docs (
  Collection VARCHAR(48) NOT NULL,
  Id VARCHAR(200) NOT NULL,
  Json LONGTEXT NOT NULL,
  UpdatedAt DATETIME(6) NOT NULL,
  PRIMARY KEY (Collection, Id),
  INDEX IX_econ_docs_updated (Collection, UpdatedAt)
);");
            }
            else
            {
                Directory.CreateDirectory(_dir);
            }
            _ready = true;
        }

        public async Task<T?> GetAsync<T>(string collection, string id) where T : class
        {
            await EnsureReadyAsync();
            if (_useMySql)
            {
                var row = await _db!.EconDocs.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Collection == collection && d.Id == id);
                return row == null ? null : JsonConvert.DeserializeObject<T>(row.Json, JsonSettings);
            }
            await JsonGate.WaitAsync();
            try
            {
                var all = ReadCollection(collection);
                return all.TryGetValue(id, out var json) ? JsonConvert.DeserializeObject<T>(json, JsonSettings) : null;
            }
            finally { JsonGate.Release(); }
        }

        public async Task PutAsync<T>(string collection, string id, T doc)
        {
            await EnsureReadyAsync();
            var json = JsonConvert.SerializeObject(doc, JsonSettings);
            if (_useMySql)
            {
                var row = await _db!.EconDocs.FirstOrDefaultAsync(d => d.Collection == collection && d.Id == id);
                if (row == null)
                {
                    _db.EconDocs.Add(new EconDoc { Collection = collection, Id = id, Json = json, UpdatedAt = DateTime.UtcNow });
                }
                else
                {
                    row.Json = json;
                    row.UpdatedAt = DateTime.UtcNow;
                }
                await _db.SaveChangesAsync();
                return;
            }
            await JsonGate.WaitAsync();
            try
            {
                var all = ReadCollection(collection);
                all[id] = json;
                WriteCollection(collection, all);
            }
            finally { JsonGate.Release(); }
        }

        public async Task<List<T>> ListAsync<T>(string collection, Func<T, bool>? filter = null)
        {
            await EnsureReadyAsync();
            List<string> jsons;
            if (_useMySql)
            {
                jsons = await _db!.EconDocs.AsNoTracking()
                    .Where(d => d.Collection == collection)
                    .Select(d => d.Json)
                    .ToListAsync();
            }
            else
            {
                await JsonGate.WaitAsync();
                try { jsons = ReadCollection(collection).Values.ToList(); }
                finally { JsonGate.Release(); }
            }
            var items = jsons
                .Select(j => JsonConvert.DeserializeObject<T>(j, JsonSettings))
                .Where(x => x != null)
                .Select(x => x!);
            return (filter == null ? items : items.Where(filter)).ToList();
        }

        public async Task DeleteAsync(string collection, string id)
        {
            await EnsureReadyAsync();
            if (_useMySql)
            {
                var row = await _db!.EconDocs.FirstOrDefaultAsync(d => d.Collection == collection && d.Id == id);
                if (row != null)
                {
                    _db.EconDocs.Remove(row);
                    await _db.SaveChangesAsync();
                }
                return;
            }
            await JsonGate.WaitAsync();
            try
            {
                var all = ReadCollection(collection);
                if (all.Remove(id)) WriteCollection(collection, all);
            }
            finally { JsonGate.Release(); }
        }

        private Dictionary<string, string> ReadCollection(string collection)
        {
            var path = Path.Combine(_dir, collection + ".json");
            if (!File.Exists(path)) return new Dictionary<string, string>();
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? new Dictionary<string, string>();
        }

        private void WriteCollection(string collection, Dictionary<string, string> all)
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, collection + ".json"), JsonConvert.SerializeObject(all, Formatting.Indented));
        }
    }
}
