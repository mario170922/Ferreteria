using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Números: los campos del navegador envían "1428.57". En español de Colombia el punto es
// separador de miles, así que se usa el punto como decimal en los números (los precios en
// pesos se siguen mostrando a la colombiana: $ 1.428). Ver Services/DecimalModelBinder.cs.
var culturaSitio = (System.Globalization.CultureInfo)new System.Globalization.CultureInfo("es-CO").Clone();
culturaSitio.NumberFormat.NumberDecimalSeparator = ".";
culturaSitio.NumberFormat.NumberGroupSeparator = ",";
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culturaSitio;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culturaSitio;

builder.Services.AddRazorPages()
    .AddMvcOptions(o => o.ModelBinderProviders.Insert(0, new Ferreteria.Services.DecimalModelBinderProvider()));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opciones =>
    {
        opciones.LoginPath = "/Login";
        opciones.LogoutPath = "/Salir";
        opciones.AccessDeniedPath = "/SinPermiso";
        opciones.ExpireTimeSpan = TimeSpan.FromHours(12);
        opciones.SlidingExpiration = true;
        opciones.Cookie.HttpOnly = true;

        // Las sesiones abiertas antes de separar Tornillería y Ferretería no dicen el negocio:
        // se cierran para que la persona vuelva a entrar eligiendo el negocio.
        // También se cierra la sesión si al usuario lo desactivaron o le cambiaron el perfil o el negocio.
        opciones.Events.OnValidatePrincipal = async contexto =>
        {
            var principal = contexto.Principal;
            bool valida = principal?.FindFirst(Ferreteria.Services.Negocio.ClaimNegocio) is not null;

            if (valida && int.TryParse(principal!.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id))
            {
                var config = contexto.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
                await using var conn = new NpgsqlConnection(config.GetConnectionString("Ferreteria"));
                var actual = await conn.QueryFirstOrDefaultAsync<(string Rol, string? Negocio, bool Activo)?>(
                    "SELECT rol, negocio, activo FROM usuarios WHERE id = @id", new { id });

                var perfil = principal.FindFirst(Ferreteria.Services.Negocio.ClaimPerfil)?.Value;
                var negocio = principal.FindFirst(Ferreteria.Services.Negocio.ClaimNegocio)?.Value;
                valida = actual is { Activo: true } u && u.Rol == perfil
                         && (u.Rol == Ferreteria.Services.Negocio.AdminGlobal || u.Negocio == negocio);
            }

            if (!valida)
            {
                contexto.RejectPrincipal();
                await contexto.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

await Ferreteria.Services.EsquemaBaseDatos.ActualizarAsync(app.Configuration, app.Logger);
await CrearAdminInicialAsync(app.Configuration);

app.Run();

static async Task CrearAdminInicialAsync(IConfiguration config)
{
    var usuario = config["PrimerAdmin:Usuario"];
    var clave = config["PrimerAdmin:Clave"];

    if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(clave))
        return;

    await using var conn = new NpgsqlConnection(config.GetConnectionString("Ferreteria"));

    var hayUsuarios = await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM usuarios)");
    if (hayUsuarios)
        return;

    var hash = BCrypt.Net.BCrypt.HashPassword(clave);

    await conn.ExecuteAsync(
        "INSERT INTO usuarios (nombre_usuario, clave_hash, rol) VALUES (@usuario, @hash, 'admin_global')",
        new { usuario, hash });
}