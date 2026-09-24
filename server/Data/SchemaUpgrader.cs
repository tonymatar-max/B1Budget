using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace B1Budget.Api.Data;

/// <summary>
/// EnsureCreated only builds a brand-new database. For an existing one, create any tables the model has gained
/// since, using EF's own generated DDL (so the schema matches what a fresh install gets), plus their indexes.
/// </summary>
public static partial class SchemaUpgrader
{
    [GeneratedRegex(@"^CREATE TABLE ""(?<t>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex CreateTable();

    [GeneratedRegex(@"^CREATE (UNIQUE )?INDEX ""[^""]+"" ON ""(?<t>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex CreateIndex();

    public static void CreateMissingTables(AppDbContext db)
    {
        var existing = db.Database.SqlQueryRaw<string>("select name as [Value] from sqlite_master where type = 'table'")
            .AsEnumerable().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in db.Database.GenerateCreateScript().Split(";", StringSplitOptions.RemoveEmptyEntries))
        {
            var sql = raw.Trim();
            if (sql.Length == 0) continue;
            var table = CreateTable().Match(sql) is { Success: true } t ? t.Groups["t"].Value : null;
            if (table != null)
            {
                if (existing.Contains(table)) continue;
                db.Database.ExecuteSqlRaw(sql);
                created.Add(table);
                continue;
            }
            // Indexes only for tables created just now (existing tables already have theirs).
            if (CreateIndex().Match(sql) is { Success: true } i && created.Contains(i.Groups["t"].Value))
                db.Database.ExecuteSqlRaw(sql);
        }
    }
}
