namespace Ferreteria.Models;

public class Usuario
{
    public int Id { get; set; }
    public string NombreUsuario { get; set; } = "";
    public string ClaveHash { get; set; } = "";
    public string Rol { get; set; } = "";
    public string? Negocio { get; set; }   // null solo para el administrador global
    public bool Activo { get; set; }
}
