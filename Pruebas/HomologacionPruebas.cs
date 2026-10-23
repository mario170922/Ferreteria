using Ferreteria.Services;
using Xunit;
using static Ferreteria.Services.Homologacion;

namespace Ferreteria.Pruebas;

// Casos pedidos por Sebastián para la homologación de productos de facturas.
public class HomologacionPruebas
{
    private static readonly Umbrales U = new();

    // Inventario de ejemplo
    private static readonly List<Producto> Inventario = new()
    {
        new(1, "Tornillo drywall punta broca", "6x1", "2000000000015"),
        new(2, "Tornillo drywall punta broca", "6x1 1/2", null),
        new(3, "Tornillo drywall punta broca", "8x2", null),
        new(4, "Tuerca hexagonal galvanizada", "1/2", null),
        new(5, "Tuerca hexagonal acero inoxidable", "1/2", null),
        new(6, "Chazo plástico", "1/4", "7707000000011"),
        new(7, "Pintura vinilo blanco", "galón", null),
        new(8, "Pintura vinilo negro", "galón", null),
    };

    private static readonly List<Alias> SinAlias = new();

    [Fact]
    public void Mismo_producto_de_tres_proveedores_con_nombres_distintos()
    {
        var a = Identificar("A-100", null, "TORN DRYWALL PTA BROCA 6X1", SinAlias, Inventario, U);
        var b = Identificar("998877", null, "Tornillo Drywal Punta Broca 6 x 1 UND", SinAlias, Inventario, U);
        var c = Identificar("TDW61", null, "tornillos drywall broca 6x1\"", SinAlias, Inventario, U);

        Assert.All(new[] { a, b, c }, r => Assert.Equal(1, r.ProductoId));
        Assert.All(new[] { a, b, c }, r => Assert.NotEqual(SinCoincidencia, r.Estado));
    }

    [Fact]
    public void Codigos_distintos_del_mismo_producto_por_alias_confirmados()
    {
        var aliasProvA = new List<Alias> { new(6, "CH-14", null, 1) };
        var aliasProvB = new List<Alias> { new(6, "00451", null, 1) };

        var r1 = Identificar("CH-14", null, "lo que sea", aliasProvA, Inventario, U);
        var r2 = Identificar("00451", null, "otra descripcion", aliasProvB, Inventario, U);

        Assert.Equal((6, 100, Automatico), (r1.ProductoId!.Value, r1.Confianza, r1.Estado));
        Assert.Equal((6, 100, Automatico), (r2.ProductoId!.Value, r2.Confianza, r2.Estado));
    }

    [Fact]
    public void Productos_parecidos_con_medidas_distintas_no_se_confunden()
    {
        var r = Identificar(null, null, "Tornillo drywall punta broca 6x1-1/2", SinAlias, Inventario, U);
        Assert.Equal(2, r.ProductoId);   // el de 6x1 1/2, nunca el de 6x1

        var sinMedidaIgual = Identificar(null, null, "Tornillo drywall punta broca 10x3", SinAlias, Inventario, U);
        Assert.Equal(SinCoincidencia, sinMedidaIgual.Estado);
        Assert.Contains("medida", sinMedidaIgual.Motivo);
    }

    [Fact]
    public void Material_y_color_distintos_se_descartan()
    {
        var inox = Identificar(null, null, "TUERCA HEX INOX 1/2", SinAlias, Inventario, U);
        Assert.Equal(5, inox.ProductoId);

        var negro = Identificar(null, null, "Pintura vinilo NEGRO galón", SinAlias, Inventario, U);
        Assert.Equal(8, negro.ProductoId);
    }

    [Fact]
    public void Compra_por_cajas_propone_el_factor()
    {
        var r = Identificar(null, null, "Tornillo drywall punta broca 6x1 caja x 100", SinAlias, Inventario, U);
        Assert.Equal(1, r.ProductoId);
        Assert.Equal(100, r.Factor);

        // Un alias confirmado recuerda su factor
        var alias = new List<Alias> { new(1, "CJ100", null, 100) };
        Assert.Equal(100, Identificar("CJ100", null, "x", alias, Inventario, U).Factor);
    }

    [Fact]
    public void Descripcion_incompleta_o_con_errores_de_OCR()
    {
        // "Tornlllo" y "dryvall" mal leídos por OCR
        var r = Identificar(null, null, "Tornlllo dryvall punta broca 6x1", SinAlias, Inventario, U);
        Assert.Equal(1, r.ProductoId);

        // Muy incompleta: no debe entrar sola al inventario
        var incompleta = Identificar(null, null, "6x1", SinAlias, Inventario, U);
        Assert.NotEqual(Automatico, incompleta.Estado);
    }

    [Fact]
    public void Producto_sin_coincidencia()
    {
        var r = Identificar("Z9", null, "Manguera jardín 1/2 x 20 m", SinAlias, Inventario, U);
        Assert.Equal(SinCoincidencia, r.Estado);
    }

    [Fact]
    public void Codigo_de_barras_gana()
    {
        var r = Identificar("XYZ", "7707000000011", "algo distinto", SinAlias, Inventario, U);
        Assert.Equal((6, 98), (r.ProductoId!.Value, r.Confianza));
    }

    [Fact]
    public void Alias_por_descripcion_normalizada()
    {
        var alias = new List<Alias> { new(4, null, Normalizar("TUERCA HEX GALV 1/2"), 1) };
        var r = Identificar(null, null, "tuerca  hex. galv 1/2", alias, Inventario, U);
        Assert.Equal((4, 95), (r.ProductoId!.Value, r.Confianza));
    }

    [Fact]
    public void Umbrales_configurables()
    {
        var estricto = new Umbrales { Automatico = 99, Sugerir = 95 };
        var r = Identificar(null, null, "TORN DRYWALL PTA BROCA 6X1", SinAlias, Inventario, estricto);
        Assert.NotEqual(Automatico, r.Estado);
    }
}
