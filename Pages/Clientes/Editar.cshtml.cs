using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Npgsql;

namespace Ferreteria.Pages.Clientes;

[Authorize]
public class EditarModel : PageModel
{
    private readonly IConfiguration _config;

    public EditarModel(IConfiguration config)
    {
        _config = config;
    }

    // Sin id es un cliente nuevo; con id se edita el existente
    public int? Id { get; set; }
    public string? Error { get; set; }

    [BindProperty]
    public DatosCliente Form { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        Id = id;
        if (id is null) return Page();

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        var c = await con.QueryFirstOrDefaultAsync<DatosCliente>(@"
            SELECT nombre_ferreteria AS NombreFerreteria,
                   nombre_cliente    AS NombreCliente,
                   direccion         AS Direccion,
                   ubicacion         AS Ubicacion,
                   horario           AS Horario,
                   celular_1         AS Celular1,
                   celular_2         AS Celular2
            FROM clientes
            WHERE id = @id", new { id });

        if (c is null) return RedirectToPage("/Clientes/Index");

        Form = c;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        Id = id;

        if (string.IsNullOrWhiteSpace(Form.NombreCliente))
            Error = "Escribe el nombre del cliente.";
        else if (string.IsNullOrWhiteSpace(Form.Direccion))
            Error = "Escribe la dirección del cliente.";
        else if (string.IsNullOrWhiteSpace(Form.Celular1))
            Error = "Escribe el celular del cliente.";
        else
            Error = Largo(Form.Celular1, 20, "El celular")
                 ?? Largo(Form.Celular2, 20, "El otro celular")
                 ?? Largo(Form.NombreCliente, 150, "El nombre del cliente")
                 ?? Largo(Form.NombreFerreteria, 150, "El nombre de la ferretería")
                 ?? Largo(Form.Direccion, 250, "La dirección")
                 ?? Largo(Form.Ubicacion, 250, "La ubicación")
                 ?? Largo(Form.Horario, 150, "El horario");

        if (Error is not null) return Page();

        await using var con = new NpgsqlConnection(_config.GetConnectionString("Ferreteria"));

        // Un celular identifica al cliente al hacer pedidos, así que no se puede repetir.
        var celulares = new[] { Form.Celular1.Trim(), Nulo(Form.Celular2) }
            .Where(c => c is not null).ToArray();

        var repetido = await con.QueryFirstOrDefaultAsync<string?>(@"
            SELECT nombre_cliente FROM clientes
            WHERE id <> @id AND (celular_1 = ANY(@celulares) OR celular_2 = ANY(@celulares))
            LIMIT 1", new { id = id ?? 0, celulares });

        if (repetido is not null)
        {
            Error = $"Ese celular ya pertenece al cliente {repetido}.";
            return Page();
        }

        var datos = new
        {
            id,
            nf = Nulo(Form.NombreFerreteria),
            nc = Form.NombreCliente.Trim(),
            dir = Form.Direccion.Trim(),
            ubi = Nulo(Form.Ubicacion),
            hor = Nulo(Form.Horario),
            c1 = Form.Celular1.Trim(),
            c2 = Nulo(Form.Celular2)
        };

        if (id is null)
        {
            await con.ExecuteAsync(@"
                INSERT INTO clientes
                    (nombre_ferreteria, nombre_cliente, direccion, ubicacion, horario, celular_1, celular_2)
                VALUES (@nf, @nc, @dir, @ubi, @hor, @c1, @c2)", datos);
            TempData["Mensaje"] = $"Cliente {datos.nc} creado.";
        }
        else
        {
            await con.ExecuteAsync(@"
                UPDATE clientes
                SET nombre_ferreteria = @nf, nombre_cliente = @nc, direccion = @dir,
                    ubicacion = @ubi, horario = @hor, celular_1 = @c1, celular_2 = @c2
                WHERE id = @id", datos);
            TempData["Mensaje"] = $"Datos de {datos.nc} guardados.";
        }

        return RedirectToPage("/Clientes/Index");
    }

    // Los mismos límites que tiene la tabla clientes, con un mensaje claro.
    private static string? Largo(string? texto, int maximo, string campo)
    {
        return (texto ?? "").Trim().Length > maximo ? $"{campo} es muy largo (máximo {maximo} letras)." : null;
    }

    private static string? Nulo(string? texto)
    {
        return string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();
    }

    public class DatosCliente
    {
        public string? NombreFerreteria { get; set; }
        public string NombreCliente { get; set; } = "";
        public string Direccion { get; set; } = "";
        public string? Ubicacion { get; set; }
        public string? Horario { get; set; }
        public string Celular1 { get; set; } = "";
        public string? Celular2 { get; set; }
    }
}
