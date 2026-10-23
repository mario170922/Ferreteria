using Dapper;
using Npgsql;

namespace Ferreteria.Services;

// Cambios de la base de datos que el sistema aplica solo al arrancar (dotnet run).
// Cada paso se puede repetir sin dañar nada ("IF NOT EXISTS"), así que no importa
// cuántas veces se reinicie. Para un cambio nuevo, se agrega un paso al final.
public static class EsquemaBaseDatos
{
    private static readonly string[] Pasos =
    {
        // 2026-10-23: código de barras único por producto
        "ALTER TABLE productos ADD COLUMN IF NOT EXISTS codigo_barras varchar(32)",
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_productos_codigo_barras ON productos (codigo_barras) WHERE codigo_barras IS NOT NULL",
        "CREATE SEQUENCE IF NOT EXISTS seq_codigo_barras_interno START 1",
    };

    public static async Task ActualizarAsync(IConfiguration config, ILogger logger)
    {
        await using var conn = new NpgsqlConnection(config.GetConnectionString("Ferreteria"));
        await conn.OpenAsync();

        foreach (var paso in Pasos)
            await conn.ExecuteAsync(paso);

        await CodigoBarras.AsignarFaltantesAsync(conn);
        logger.LogInformation("Base de datos al día.");
    }
}
