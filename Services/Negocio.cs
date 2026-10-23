using System.Security.Claims;

namespace Ferreteria.Services;

// Los dos negocios del sitio y los perfiles de usuario.
// Al ingresar, la persona elige el negocio y queda guardado en su sesión (claim "negocio").
// Todas las páginas muestran y guardan solo los datos de ese negocio.
public static class Negocio
{
    public const string Tornilleria = "tornilleria";
    public const string Ferreteria = "ferreteria";
    public static readonly string[] Todos = { Tornilleria, Ferreteria };

    public const string ClaimNegocio = "negocio";
    public const string ClaimPerfil = "perfil";

    // Perfiles guardados en usuarios.rol
    public const string AdminGlobal = "admin_global";
    public const string Admin = "admin";
    public const string Vendedor = "vendedor";
    public const string Cajero = "cajero";
    public const string Conductor = "conductor";
    public static readonly string[] Perfiles = { AdminGlobal, Admin, Vendedor, Cajero, Conductor };

    public static bool EsValido(string? negocio) => negocio is Tornilleria or Ferreteria;

    public static string Nombre(string? negocio) => negocio switch
    {
        Tornilleria => "Tornillería",
        Ferreteria => "Ferretería",
        _ => "Ambos negocios"
    };

    public static string Icono(string? negocio) => negocio == Ferreteria ? "bi-tools" : "bi-nut";

    public static string NombrePerfil(string? perfil) => perfil switch
    {
        AdminGlobal => "Administrador global",
        Admin => "Administrador",
        Vendedor => "Vendedor",
        Cajero => "Cajero",
        Conductor => "Conductor",
        _ => perfil ?? ""
    };

    // ---------- Lo que se sabe del usuario que ingresó ----------

    // Negocio en el que está trabajando ahora
    public static string NegocioActual(this ClaimsPrincipal usuario) =>
        usuario.FindFirstValue(ClaimNegocio) is { } n && EsValido(n) ? n : Tornilleria;

    public static string Perfil(this ClaimsPrincipal usuario) =>
        usuario.FindFirstValue(ClaimPerfil) ?? usuario.FindFirstValue(ClaimTypes.Role) ?? "";

    public static bool EsAdminGlobal(this ClaimsPrincipal usuario) => usuario.Perfil() == AdminGlobal;

    // El administrador global también cuenta como administrador (rol "admin") en cada negocio,
    // así todo lo que ya pedía [Authorize(Roles = "admin")] le sigue funcionando.
    public static bool EsAdmin(this ClaimsPrincipal usuario) => usuario.IsInRole(Admin);

    // Quién ve todos los pedidos del negocio (el vendedor solo ve los suyos)
    public static bool VeTodosLosPedidos(this ClaimsPrincipal usuario) =>
        usuario.IsInRole(Admin) || usuario.IsInRole(Cajero) || usuario.IsInRole(Conductor);
}
