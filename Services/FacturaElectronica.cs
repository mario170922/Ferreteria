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
        public string Moneda { get; set; } = "COP";
        public decimal Subtotal { get; set; }
        public decimal Impuestos { get; set; }
        public decimal Descuentos { get; set; }      // descuentos a nivel de factura
        public decimal Cargos { get; set; }          // cargos adicionales (fletes, etc.)
        public decimal TotalConImpuestos { get; set; }
        public decimal Anticipos { get; set; }
        public decimal Total { get; set; }           // valor a pagar
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
        public decimal PrecioUnitario { get; set; }
        public decimal Descuento { get; set; }
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
            Moneda = Texto(inv.Element(Cbc + "DocumentCurrencyCode")) is { Length: > 0 } m ? m : "COP",
            Subtotal = Numero(totales?.Element(Cbc + "LineExtensionAmount")),
            Descuentos = Numero(totales?.Element(Cbc + "AllowanceTotalAmount")),
            Cargos = Numero(totales?.Element(Cbc + "ChargeTotalAmount")),
            TotalConImpuestos = Numero(totales?.Element(Cbc + "TaxInclusiveAmount")),
            Anticipos = Numero(totales?.Element(Cbc + "PrepaidAmount")),
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
                PrecioUnitario = Numero(l.Element(Cac + "Price")?.Element(Cbc + "PriceAmount")),
                Descuento = l.Elements(Cac + "AllowanceCharge")
                    .Where(a => Texto(a.Element(Cbc + "ChargeIndicator")) == "false")
                    .Sum(a => Numero(a.Element(Cbc + "Amount"))),
                ValorSinImpuestos = Numero(l.Element(Cbc + "LineExtensionAmount")),
                Impuestos = l.Elements(Cac + "TaxTotal").Sum(t => Numero(t.Element(Cbc + "TaxAmount")))
            });
        }

        if (factura.Lineas.Count == 0)
            throw new InvalidOperationException("La factura no trae productos.");
        if (factura.Total == 0) factura.Total = factura.Lineas.Sum(x => x.ValorTotal);
        return factura;
    }

    // Revisa que la factura cuadre: suma de líneas, impuestos y total, con una tolerancia
    // pequeña por los redondeos. Devuelve las alertas para mostrar (vacía si todo cuadra).
    public static List<string> Validar(Factura f)
    {
        var alertas = new List<string>();
        var tol = 1m + f.Lineas.Count * 0.5m;   // redondeos a peso en cada línea

        var sumaLineas = f.Lineas.Sum(l => l.ValorSinImpuestos);
        if (Math.Abs(sumaLineas - f.Subtotal) > tol)
            alertas.Add($"La suma de las líneas ({N(sumaLineas)}) no coincide con el subtotal de la factura ({N(f.Subtotal)}).");

        var sumaImpLineas = f.Lineas.Sum(l => l.Impuestos);
        if (sumaImpLineas > 0 && Math.Abs(sumaImpLineas - f.Impuestos) > tol)
            alertas.Add($"Los impuestos de las líneas ({N(sumaImpLineas)}) no coinciden con los impuestos de la factura ({N(f.Impuestos)}).");

        if (f.TotalConImpuestos > 0 && Math.Abs(f.Subtotal + f.Impuestos - f.TotalConImpuestos) > tol)
            alertas.Add($"Subtotal + impuestos ({N(f.Subtotal + f.Impuestos)}) no coincide con el total con impuestos ({N(f.TotalConImpuestos)}).");

        var esperado = (f.TotalConImpuestos > 0 ? f.TotalConImpuestos : f.Subtotal + f.Impuestos) - f.Descuentos + f.Cargos - f.Anticipos;
        if (Math.Abs(esperado - f.Total) > tol)
            alertas.Add($"El total a pagar ({N(f.Total)}) no coincide con lo calculado ({N(esperado)}).");

        foreach (var l in f.Lineas.Where(l => l.PrecioUnitario > 0))
        {
            var calculado = l.Cantidad * l.PrecioUnitario - l.Descuento;
            if (Math.Abs(calculado - l.ValorSinImpuestos) > 1m)
                alertas.Add($"Línea {l.Numero}: {N(l.Cantidad, 3)} × {N(l.PrecioUnitario, 2)} − descuento {N(l.Descuento)} = {N(calculado)}, pero la factura dice {N(l.ValorSinImpuestos)}.");
        }
        return alertas;
    }

    private static readonly CultureInfo Co = new("es-CO");
    private static string N(decimal v, int decimales = 0) => v.ToString(decimales == 0 ? "N0" : "0." + new string('#', decimales), Co);

    // EAN-8, UPC-A, EAN-13 o GTIN-14: solo números de 8, 12, 13 o 14 dígitos
    private static bool EsCodigoDeBarras(string codigo) =>
        codigo.Length is 8 or 12 or 13 or 14 && codigo.All(char.IsDigit);

    private static string Texto(XElement? e) => e?.Value.Trim() ?? "";

    private static decimal Numero(XElement? e) =>
        decimal.TryParse(e?.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;
}
