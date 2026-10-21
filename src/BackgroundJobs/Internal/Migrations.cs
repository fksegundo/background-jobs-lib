using System.Reflection;
using Npgsql;

namespace BackgroundJobs.Internal;

/// <summary>Applies embedded SQL migrations in version order, once, under the owner lock.</summary>
internal static class Migrations
{
    public static async Task ApplyAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var asm = typeof(Migrations).Assembly;
        var names = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        foreach (var name in names)
        {
            await using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
