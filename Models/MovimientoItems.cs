namespace Ferreteria.Models;

public class MovimientoItem
{
    public int Id { get; set; }
    public int Cantidad { get; set; }
    public string? Motivo { get; set; }
    public DateTime Fecha { get; set; }
}