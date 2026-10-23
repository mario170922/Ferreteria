using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Ferreteria.Services;

// Lee la factura electrónica de un proveedor (formato UBL 2.1 de la DIAN).
// El correo del proveedor trae un .zip con un PDF (para leer) y un .xml (con los datos).
// El .xml suele ser un "AttachedDocument" que lleva la factura completa por dentro;
// también se acepta el .xml de la factura sola.
public static class FacturaElectronica
{
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    public class Factura
    {
        public string Numero { get; set; } = "";
        public string Cufe { get; set; } = "";
        public DateTime Fecha { get; set; }
        public string ProveedorNit { get; set; } = "";
        public string ProveedorNombre { get; set; } = "";
        public decimal Subtotal { get; set; }
        public decimal Impuestos { get; set; }
        public decimal Total { get; set; }
        public List<Linea> Lineas { get; set; } = new();
    }

    public class Linea
    {
        public int Numero { get; set; }
        public string CodigoProveedor { get; set; } = "";
        public string? CodigoBarras { get; set; }
        public string Descripcion { get; set; } = "";
        public decimal Cantidad { get; set; }
        public string? Unidad { get; set; }
        public decimal ValorSinImpuestos { get; set; }
        public decimal Impuestos { get; set; }
        public decimal ValorTotal => ValorSinImpuestos + Impuestos;
    }

    // Recibe el archivo tal como llegó (.zip o .xml) y devuelve la factura, o lanza
    // InvalidOperationException con un mensaje para mostrar a la persona.
    public static Factura Leer(Stream archivo, string nombreArchivo)
    {
        var xml = nombreArchivo.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? XmlDentroDelZip(archivo)
            : XDocument.Load(archivo);

        var factura = BuscarFactura(xml)
            ?? throw new InvalidOperationException("El archivo no tiene una factura electrónica. Use el .zip o el .xml que llegó en el correo del proveedor.");

        return Convertir(factura);
    }

    private static XDocument XmlDentroDelZip(Stream archivo)
    {
        try
        {
            using var zip = new ZipArchive(archivo, ZipArchiveMode.Read);
            var entradas = zip.Entries.Where(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entradas.Count == 0)
                throw new InvalidOperationException("El .zip no trae ningún archivo .xml de factura.");

            foreach (var entrada in entradas)
            {
                using var s = entrada.Open();
                var doc = XDocument.Load(s);
                if (BuscarFactura(doc) is not null) return doc;
            }
            throw new InvalidOperationException("El .zip no trae una factura electrónica que se pueda leer.");
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("El archivo .zip está dañado o no es un .zip.");
        }
    }

    // Devuelve el elemento <Invoice>, esté suelto o dentro de un AttachedDocument
    private static XElement? BuscarFactura(XDocument doc)
    {
        var raiz = doc.Root;
        if (raiz is null) return null;
        if (raiz.Name.LocalName == "Invoice") return raiz;

        if (raiz.Name.LocalName == "AttachedDocument")
        {
            // La factura va como texto dentro de cac:Attachment/cac:ExternalReference/cbc:Description
            foreach (var desc in raiz.Descendants(Cac + "Attachment").Descendants(Cbc + "Description"))
            {
                var texto = desc.Value.Trim();
                if (!texto.StartsWith("<")) continue;
                try
                {
                    var interno = XDocument.Parse(texto);
                    if (interno.Root?.Name.LocalName == "Invoice") return interno.Root;
                }
                catch (System.Xml.XmlException) { }
            }
        }
        return null;
    }

    private static Factura Convertir(XElement inv)
    {
        var proveedor = inv.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party");
        var esquema = proveedor?.Element(Cac + "PartyTaxScheme");
        var totales = inv.Element(Cac + "LegalMonetaryTotal");

        var factura = new Factura
        {
            Numero = Texto(inv.Element(Cbc + "ID")),
            Cufe = Texto(inv.Element(Cbc + "UUID")),
            Fecha = DateTime.TryParse(Texto(inv.Element(Cbc + "IssueDate")), CultureInfo.InvariantCulture, DateTimeStyles.None, out var f) ? f : DateTime.Today,
            ProveedorNit = Texto(esquema?.Element(Cbc + "CompanyID")),
            ProveedorNombre = Texto(esquema?.Element(Cbc + "RegistrationName"))
                is { Length: > 0 } nombre ? nombre
                : Texto(proveedor?.Element(Cac + "PartyName")?.Element(Cbc + "Name")),
            Subtotal = Numero(totales?.Element(Cbc + "LineExtensionAmount")),
            Total = Numero(totales?.Element(Cbc + "PayableAmount")),
            Impuestos = inv.Elements(Cac + "TaxTotal").Sum(t => Numero(t.Element(Cbc + "TaxAmount")))
        };

        var n = 0;
        foreach (var l in inv.Elements(Cac + "InvoiceLine"))
        {
            var item = l.Element(Cac + "Item");
            var cantidad = l.Element(Cbc + "InvoicedQuantity");
            n++;

            // El código de barras (GTIN/EAN) suele ir en StandardItemIdentification
            var estandar = Texto(item?.Element(Cac + "StandardItemIdentification")?.Element(Cbc + "ID"));
            var vendedor = Texto(item?.Element(Cac + "SellersItemIdentification")?.Element(Cbc + "ID"));

            factura.Lineas.Add(new Linea
            {
                Numero = n,
                CodigoProveedor = vendedor.Length > 0 ? vendedor : estandar,
                CodigoBarras = EsCodigoDeBarras(estandar) ? estandar : EsCodigoDeBarras(vendedor) ? vendedor : null,
                Descripcion = Texto(item?.Element(Cbc + "Description")),
                Cantidad = Numero(cantidad),
                Unidad = cantidad?.Attribute("unitCode")?.Value,
                ValorSinImpuestos = Numero(l.Element(Cbc + "LineExtensionAmount")),
                Impuestos = l.Elements(Cac + "TaxTotal").Sum(t => Numero(t.Element(Cbc + "TaxAmount")))
            });
        }

        if (factura.Lineas.Count == 0)
            throw new InvalidOperationException("La factura no trae productos.");
        if (factura.Total == 0) factura.Total = factura.Lineas.Sum(x => x.ValorTotal);
        return factura;
    }

    // EAN-8, UPC-A, EAN-13 o GTIN-14: solo números de 8, 12, 13 o 14 dígitos
    private static bool EsCodigoDeBarras(string codigo) =>
        codigo.Length is 8 or 12 or 13 or 14 && codigo.All(char.IsDigit);

    private static string Texto(XElement? e) => e?.Value.Trim() ?? "";

    private static decimal Numero(XElement? e) =>
        decimal.TryParse(e?.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}
