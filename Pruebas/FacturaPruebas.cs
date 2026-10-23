using System.IO.Compression;
using System.Text;
using Ferreteria.Services;
using Xunit;

namespace Ferreteria.Pruebas;

// Lectura y revisión de totales de facturas electrónicas (formato UBL de la DIAN)
public class FacturaPruebas
{
    private static string Linea(int n, decimal cant, decimal precio, decimal valor, decimal iva, string desc, string codigo, decimal descuento = 0) => $"""
        <cac:InvoiceLine><cbc:ID>{n}</cbc:ID><cbc:InvoicedQuantity unitCode="94">{cant}</cbc:InvoicedQuantity>
        <cbc:LineExtensionAmount currencyID="COP">{valor}</cbc:LineExtensionAmount>
        {(descuento > 0 ? $"<cac:AllowanceCharge><cbc:ChargeIndicator>false</cbc:ChargeIndicator><cbc:Amount currencyID=\"COP\">{descuento}</cbc:Amount></cac:AllowanceCharge>" : "")}
        <cac:TaxTotal><cbc:TaxAmount currencyID="COP">{iva}</cbc:TaxAmount></cac:TaxTotal>
        <cac:Item><cbc:Description>{desc}</cbc:Description><cac:SellersItemIdentification><cbc:ID>{codigo}</cbc:ID></cac:SellersItemIdentification></cac:Item>
        <cac:Price><cbc:PriceAmount currencyID="COP">{precio}</cbc:PriceAmount></cac:Price></cac:InvoiceLine>
        """;

    private static string Factura(string lineas, decimal subtotal, decimal iva, decimal total) => $"""
        <Invoice xmlns="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
        <cbc:ID>F-1</cbc:ID><cbc:UUID>cufe-1</cbc:UUID><cbc:IssueDate>2026-10-20</cbc:IssueDate><cbc:DocumentCurrencyCode>COP</cbc:DocumentCurrencyCode>
        <cac:AccountingSupplierParty><cac:Party><cac:PartyTaxScheme><cbc:RegistrationName>PROVEEDOR SAS</cbc:RegistrationName><cbc:CompanyID>900123456</cbc:CompanyID></cac:PartyTaxScheme></cac:Party></cac:AccountingSupplierParty>
        <cac:TaxTotal><cbc:TaxAmount currencyID="COP">{iva}</cbc:TaxAmount></cac:TaxTotal>
        <cac:LegalMonetaryTotal><cbc:LineExtensionAmount currencyID="COP">{subtotal}</cbc:LineExtensionAmount><cbc:TaxInclusiveAmount currencyID="COP">{subtotal + iva}</cbc:TaxInclusiveAmount><cbc:PayableAmount currencyID="COP">{total}</cbc:PayableAmount></cac:LegalMonetaryTotal>
        {lineas}
        </Invoice>
        """;

    private static FacturaElectronica.Factura Leer(string xml) =>
        FacturaElectronica.Leer(new MemoryStream(Encoding.UTF8.GetBytes(xml)), "f.xml");

    [Fact]
    public void Factura_que_cuadra_no_tiene_alertas()
    {
        var xml = Factura(Linea(1, 10, 100, 1000, 190, "TORNILLO 6X1", "A1") + Linea(2, 5, 200, 900, 171, "CHAZO 1/4", "B2", 100), 1900, 361, 2261);
        var f = Leer(xml);
        Assert.Equal(2, f.Lineas.Count);
        Assert.Equal(100, f.Lineas[1].Descuento);
        Assert.Empty(FacturaElectronica.Validar(f));
    }

    [Fact]
    public void Precios_descuentos_e_impuestos_incorrectos_generan_alertas()
    {
        // Línea 1: 10 × 100 debería dar 1000 y dice 1500; el total a pagar tampoco cuadra
        var xml = Factura(Linea(1, 10, 100, 1500, 190, "TORNILLO 6X1", "A1"), 1500, 300, 9999);
        var alertas = FacturaElectronica.Validar(Leer(xml));
        Assert.Contains(alertas, a => a.StartsWith("Línea 1"));
        Assert.Contains(alertas, a => a.Contains("impuestos de las líneas"));
        Assert.Contains(alertas, a => a.Contains("total a pagar"));
    }

    [Fact]
    public void Lee_el_zip_con_AttachedDocument()
    {
        var inv = Factura(Linea(1, 2, 50, 100, 19, "ARANDELA 3/8", "C3"), 100, 19, 119);
        var adj = $"""
            <AttachedDocument xmlns="urn:oasis:names:specification:ubl:schema:xsd:AttachedDocument-2" xmlns:cac="urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2" xmlns:cbc="urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2">
            <cac:Attachment><cac:ExternalReference><cbc:Description><![CDATA[{inv}]]></cbc:Description></cac:ExternalReference></cac:Attachment></AttachedDocument>
            """;
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(z.CreateEntry("ad.xml").Open())) w.Write(adj);
        ms.Position = 0;

        var f = FacturaElectronica.Leer(ms, "f.zip");
        Assert.Equal(("F-1", "900123456", 119m), (f.Numero, f.ProveedorNit, f.Total));
    }

    [Fact]
    public void Archivo_que_no_es_factura_da_mensaje_claro()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Leer("<otra/>"));
        Assert.Contains("factura electrónica", ex.Message);
    }

    // Factura real de Corbeta, si está guardada en la carpeta de ejemplos (no se sube a GitHub)
    [Fact]
    public void Factura_real_de_Corbeta_cuadra()
    {
        const string ruta = @"C:\proyectos\facturas-ejemplo\ad0890900943006260163B24B.zip";
        if (!File.Exists(ruta)) return;
        using var s = File.OpenRead(ruta);
        var f = FacturaElectronica.Leer(s, ruta);
        Assert.Equal("P0624829694", f.Numero);
        Assert.Equal(11, f.Lineas.Count);
        Assert.Empty(FacturaElectronica.Validar(f));
    }
}
