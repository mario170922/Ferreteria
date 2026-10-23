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

        // Compras cargadas desde la factura electrónica del proveedor
        ("2026-10-23 compras", @"
            CREATE TABLE proveedores (
                id     serial PRIMARY KEY,
                nit    varchar(20) NOT NULL UNIQUE,
                nombre varchar(200) NOT NULL
            );

            CREATE TABLE compras (
                id             serial PRIMARY KEY,
                negocio        varchar(15) NOT NULL CHECK (negocio IN ('tornilleria', 'ferreteria')),
                proveedor_id   int NOT NULL REFERENCES proveedores (id),
                numero_factura varchar(50) NOT NULL,
                cufe           varchar(120),
                fecha          date NOT NULL,
                subtotal       numeric(14, 2) NOT NULL,
                impuestos      numeric(14, 2) NOT NULL,
                total          numeric(14, 2) NOT NULL,
                usuario_id     int REFERENCES usuarios (id),
                creado_en      timestamp NOT NULL DEFAULT now(),
                UNIQUE (proveedor_id, numero_factura)
            );
            CREATE UNIQUE INDEX ux_compras_cufe ON compras (cufe) WHERE cufe IS NOT NULL AND cufe <> '';

            CREATE TABLE compra_items (
                id               serial PRIMARY KEY,
                compra_id        int NOT NULL REFERENCES compras (id) ON DELETE CASCADE,
                producto_id      int REFERENCES productos (id) ON DELETE SET NULL,
                codigo_proveedor varchar(60),
                descripcion      varchar(300) NOT NULL,
                cantidad         numeric(14, 3) NOT NULL,
                unidades_por     int NOT NULL DEFAULT 1,
                valor_total      numeric(14, 2) NOT NULL
            );

            -- Lo que el sistema aprende: qué producto nuestro es cada código del proveedor
            -- y cuántas unidades trae cada una (por ejemplo, una caja de 100).
            CREATE TABLE producto_codigos_proveedor (
                proveedor_id     int NOT NULL REFERENCES proveedores (id),
                codigo_proveedor varchar(60) NOT NULL,
                producto_id      int NOT NULL REFERENCES productos (id) ON DELETE CASCADE,
                unidades_por     int NOT NULL DEFAULT 1,
                PRIMARY KEY (proveedor_id, codigo_proveedor, producto_id)
            );"),

        // Entregas y caja: el pedido se asigna a un conductor, que registra cómo cobró.
        // Al final del día el cajero recibe la plata de cada conductor y cierra su caja.
        ("2026-10-23 entregas y caja", @"
            ALTER TABLE pedidos ADD COLUMN conductor_id    int REFERENCES usuarios (id);
            ALTER TABLE pedidos ADD COLUMN asignado_en     timestamp;
            ALTER TABLE pedidos ADD COLUMN entregado_en    timestamp;
            ALTER TABLE pedidos ADD COLUMN pago_metodo     varchar(15) CHECK (pago_metodo IN ('efectivo', 'transferencia'));
            ALTER TABLE pedidos ADD COLUMN pago_valor      numeric(14, 2);
            ALTER TABLE pedidos ADD COLUMN pago_referencia varchar(80);
            ALTER TABLE pedidos ADD COLUMN nota_entrega    varchar(300);
            CREATE INDEX ix_pedidos_conductor ON pedidos (conductor_id, estado);

            CREATE TABLE cierres_caja (
                id                 serial PRIMARY KEY,
                negocio            varchar(15) NOT NULL CHECK (negocio IN ('tornilleria', 'ferreteria')),
                fecha              date NOT NULL,
                conductor_id       int NOT NULL REFERENCES usuarios (id),
                efectivo_esperado  numeric(14, 2) NOT NULL,
                transferencias     numeric(14, 2) NOT NULL,
                efectivo_recibido  numeric(14, 2) NOT NULL,
                nota               varchar(300),
                cajero_id          int REFERENCES usuarios (id),
                creado_en          timestamp NOT NULL DEFAULT now(),
                UNIQUE (negocio, fecha, conductor_id)
            );"),
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
