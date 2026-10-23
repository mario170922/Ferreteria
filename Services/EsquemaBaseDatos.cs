using Dapper;
using Npgsql;

namespace Ferreteria.Services;

// Cambios de la base de datos que el sistema aplica solo al arrancar (dotnet run).
// Cada cambio tiene un nombre y queda anotado en la tabla esquema_cambios, así se
// aplica una sola vez. Para un cambio nuevo, se agrega un paso al FINAL de la lista
// (nunca se modifica uno que ya se aplicó).
public static class EsquemaBaseDatos
{
    private static readonly (string Nombre, string Sql)[] Pasos =
    {
        ("2026-10-23 codigo de barras", @"
            ALTER TABLE productos ADD COLUMN IF NOT EXISTS codigo_barras varchar(32);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_productos_codigo_barras
                ON productos (codigo_barras) WHERE codigo_barras IS NOT NULL;
            CREATE SEQUENCE IF NOT EXISTS seq_codigo_barras_interno START 1;"),

        // Tornillería y Ferretería: todo lo que ya existía queda en Tornillería.
        // Los clientes se comparten entre los dos negocios.
        ("2026-10-23 dos negocios", @"
            ALTER TABLE productos  ADD COLUMN IF NOT EXISTS negocio varchar(15) NOT NULL DEFAULT 'tornilleria';
            ALTER TABLE categorias ADD COLUMN IF NOT EXISTS negocio varchar(15) NOT NULL DEFAULT 'tornilleria';
            ALTER TABLE pedidos    ADD COLUMN IF NOT EXISTS negocio varchar(15) NOT NULL DEFAULT 'tornilleria';
            ALTER TABLE usuarios   ADD COLUMN IF NOT EXISTS negocio varchar(15);

            ALTER TABLE productos  ADD CONSTRAINT productos_negocio_check  CHECK (negocio IN ('tornilleria', 'ferreteria'));
            ALTER TABLE categorias ADD CONSTRAINT categorias_negocio_check CHECK (negocio IN ('tornilleria', 'ferreteria'));
            ALTER TABLE pedidos    ADD CONSTRAINT pedidos_negocio_check    CHECK (negocio IN ('tornilleria', 'ferreteria'));

            -- El código interno y el nombre de categoría solo deben ser únicos dentro de cada negocio
            ALTER TABLE productos  DROP CONSTRAINT IF EXISTS productos_codigo_key;
            ALTER TABLE productos  ADD CONSTRAINT productos_negocio_codigo_key UNIQUE (negocio, codigo);
            ALTER TABLE categorias DROP CONSTRAINT IF EXISTS categorias_nombre_key;
            ALTER TABLE categorias ADD CONSTRAINT categorias_negocio_nombre_key UNIQUE (negocio, nombre);

            -- Perfiles: administrador global (los dos negocios) y, por negocio,
            -- administrador, vendedor, cajero y conductor.
            ALTER TABLE usuarios DROP CONSTRAINT IF EXISTS usuarios_rol_check;
            UPDATE usuarios SET rol = 'admin_global' WHERE rol = 'admin' AND lower(nombre_usuario) = 'admin';
            UPDATE usuarios SET negocio = 'tornilleria' WHERE rol <> 'admin_global' AND negocio IS NULL;
            ALTER TABLE usuarios ADD CONSTRAINT usuarios_rol_check
                CHECK (rol IN ('admin_global', 'admin', 'vendedor', 'cajero', 'conductor'));
            ALTER TABLE usuarios ADD CONSTRAINT usuarios_negocio_check
                CHECK ((rol = 'admin_global' AND negocio IS NULL)
                    OR (rol <> 'admin_global' AND negocio IN ('tornilleria', 'ferreteria')));"),
    };

    public static async Task ActualizarAsync(IConfiguration config, ILogger logger)
    {
        await using var conn = new NpgsqlConnection(config.GetConnectionString("Ferreteria"));
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS esquema_cambios (
                nombre     varchar(100) PRIMARY KEY,
                aplicado_en timestamp NOT NULL DEFAULT now()
            )");

        foreach (var (nombre, sql) in Pasos)
        {
            var yaAplicado = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM esquema_cambios WHERE nombre = @nombre)", new { nombre });
            if (yaAplicado) continue;

            // Cada cambio va completo o no va: si algo falla, la base queda como estaba.
            await using var tx = await conn.BeginTransactionAsync();
            await conn.ExecuteAsync(sql, transaction: tx);
            await conn.ExecuteAsync("INSERT INTO esquema_cambios (nombre) VALUES (@nombre)", new { nombre }, tx);
            await tx.CommitAsync();
            logger.LogInformation("Cambio de base de datos aplicado: {Nombre}", nombre);
        }

        await CodigoBarras.AsignarFaltantesAsync(conn);
    }
}
