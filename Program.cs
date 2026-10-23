using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();

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
        opciones.Events.OnValidatePrincipal = async contexto =>
        {
            if (contexto.Principal?.FindFirst(Ferreteria.Services.Negocio.ClaimNegocio) is null)
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