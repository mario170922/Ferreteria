namespace Ferreteria.Models;

public class Producto
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Medida { get; set; }
    public decimal PrecioCompra { get; set; }
    public decimal PrecioVenta { get; set; }
    public int Stock { get; set; }
    public int StockMinimo { get; set; }

    // Alerta: solo si hay un mínimo definido y el stock llegó a ese mínimo o está por debajo
    public bool StockBajo => StockMinimo > 0 && Stock <= StockMinimo;
}