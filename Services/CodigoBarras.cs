using System.Data;
using System.Text.RegularExpressions;
using Dapper;

namespace Ferreteria.Services;

// Códigos de barras de los productos.
// - Si el producto trae código del proveedor, se guarda tal cual (se valida que no se repita).
// - Si no trae, se crea uno propio tipo EAN-13 que empieza por "20": ese rango está
//   reservado para uso interno de cada almacén, así nunca choca con un código de fábrica.
public static partial class CodigoBarras
{
    public const int LargoMaximo = 32;

    // Lo que lee un lector USB: números, letras y guiones (EAN, UPC o Code 128).
    [GeneratedRegex("^[0-9A-Za-z-]{4,32}$")]
    private static partial Regex Formato();

    public static string Limpiar(string? codigo) => (codigo ?? "").Trim().Replace(" ", "");

    // Devuelve el mensaje de error, o null si el código sirve.
    public static string? Validar(string codigo)
    {
        if (codigo.Length == 0) return null;
        if (!Formato().IsMatch(codigo))
            return "El código de barras solo puede tener números, letras y guiones (de 4 a 32).";
        return null;
    }

    // Crea un código interno que todavía no esté usado por ningún producto.
    public static async Task<string> GenerarAsync(IDbConnection conn, IDbTransaction? tx = null)
    {
        while (true)
        {
            var numero = await conn.ExecuteScalarAsync<long>("SELECT nextval('seq_codigo_barras_interno')", transaction: tx);
            var codigo = Ean13("20" + numero.ToString("D10"));
            var existe = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM productos WHERE codigo_barras = @codigo)", new { codigo }, tx);
            if (!existe) return codigo;
        }
    }

    // Agrega el dígito de control a 12 dígitos para formar un EAN-13 válido.
    public static string Ean13(string doceDigitos)
    {
        var suma = 0;
        for (var i = 0; i < 12; i++)
            suma += (doceDigitos[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return doceDigitos + ((10 - suma % 10) % 10);
    }

    // Al arrancar: los productos que aún no tienen código reciben uno interno.
    public static async Task AsignarFaltantesAsync(IDbConnection conn)
    {
        var ids = (await conn.QueryAsync<int>(
            "SELECT id FROM productos WHERE codigo_barras IS NULL OR codigo_barras = '' ORDER BY id")).ToList();

        foreach (var id in ids)
        {
            var codigo = await GenerarAsync(conn);
            await conn.ExecuteAsync("UPDATE productos SET codigo_barras = @codigo WHERE id = @id", new { codigo, id });
        }
    }
}
