using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Models.Messages;

namespace Ferreteria.Services;

// Lee con inteligencia artificial (Claude, de Anthropic) una factura en PDF o en foto
// y la convierte en los mismos datos que se sacan del .xml de la factura electrónica.
// Solo se usa cuando no hay .xml: el .xml es exacto, la lectura con IA hay que revisarla.
//
// Para activarlo hay que poner la clave de la API (nunca en appsettings.json, que va a GitHub):
//   dotnet user-secrets set "IA:ClaveApi" "sk-ant-..."
// Cada factura leída tiene un costo pequeño en la cuenta de Anthropic.
public static class LectorFacturaIA
{
    public const string Modelo = "claude-opus-5-5";

    public static readonly string[] Extensiones = { ".pdf", ".jpg", ".jpeg", ".png", ".webp" };

    public static string? Clave(IConfiguration config) =>
        config["IA:ClaveApi"] is { Length: > 0 } c ? c : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    public static bool Configurado(IConfiguration config) => !string.IsNullOrWhiteSpace(Clave(config));

    private const string Instrucciones = """
        Eres un lector de facturas de compra de una ferretería en Colombia.
        Lee la factura adjunta y responde SOLO con un objeto JSON (sin texto antes ni después, sin ```),
        con esta forma exacta:
        {
          "numero": "número de la factura",
          "cufe": "CUFE si aparece, si no vacío",
          "fecha": "AAAA-MM-DD",
          "proveedor_nit": "NIT del vendedor sin dígito de verificación ni puntos",
          "proveedor_nombre": "razón social del vendedor",
          "moneda": "COP",
          "subtotal": 0, "impuestos": 0, "descuentos": 0, "total": 0,
          "lineas": [
            { "codigo": "código del producto del proveedor", "codigo_barras": "EAN si aparece, si no vacío",
              "descripcion": "descripción tal como aparece", "cantidad": 0, "unidad": "UND, CAJA, etc.",
              "precio_unitario": 0, "descuento": 0, "valor_sin_impuestos": 0, "impuestos": 0 }
          ]
        }
        Reglas: los números van sin separador de miles y con punto decimal. Copia los valores tal como están
        impresos; no inventes ni completes datos que no se ven: si un dato no aparece o no se lee, pon "" o 0.
        Incluye todas las líneas de productos, también las de obsequio en $0.
        """;

    // Devuelve la factura, o lanza InvalidOperationException con un mensaje para la persona
    public static async Task<FacturaElectronica.Factura> LeerAsync(IConfiguration config, byte[] archivo, string extension, CancellationToken cancelar = default)
    {
        var clave = Clave(config) ?? throw new InvalidOperationException(
            "Para leer PDF o fotos falta configurar la clave de la inteligencia artificial. Mientras tanto, usa el .zip o .xml del correo.");

        AnthropicClient cliente = new() { ApiKey = clave };
        var datos = Convert.ToBase64String(archivo);

        ContentBlockParam adjunto = extension == ".pdf"
            ? new DocumentBlockParam { Source = new Base64PdfSource { Data = datos } }
            : new ImageBlockParam
            {
                Source = new Base64ImageSource
                {
                    Data = datos,
                    MediaType = extension switch
                    {
                        ".png" => MediaType.ImagePng,
                        ".webp" => MediaType.ImageWebP,
                        _ => MediaType.ImageJpeg
                    }
                }
            };

        Message respuesta;
        try
        {
            respuesta = await cliente.Messages.Create(new MessageCreateParams
            {
                Model = Modelo,
                MaxTokens = 16000,
                OutputConfig = new OutputConfig { Effort = Effort.Medium },
                System = Instrucciones,
                Messages =
                [
                    new UserMessageParam
                    {
                        Content = new List<ContentBlockParam> { adjunto, new TextBlockParam { Text = "Extrae los datos de esta factura." } }
                    }
                ]
            }, cancelar);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("No se pudo leer la factura con la inteligencia artificial (" + ex.GetType().Name + "). Intenta de nuevo o usa el .xml.");
        }

        if (respuesta.StopReason == "refusal")
            throw new InvalidOperationException("La inteligencia artificial no quiso leer este archivo. Usa el .xml de la factura.");

        var texto = string.Concat(respuesta.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        var inicio = texto.IndexOf('{');
        var fin = texto.LastIndexOf('}');
        if (inicio < 0 || fin <= inicio)
            throw new InvalidOperationException("La inteligencia artificial no devolvió datos de factura. Revisa que la foto o el PDF se vean bien.");

        Leida? leida;
        try { leida = JsonSerializer.Deserialize<Leida>(texto[inicio..(fin + 1)], Json); }
        catch (JsonException) { leida = null; }
        if (leida is null || leida.Lineas is not { Count: > 0 })
            throw new InvalidOperationException("No se encontraron productos en la factura. Revisa que la foto o el PDF se vean bien.");

        return Convertir(leida);
    }

    // Lo leído por la IA se guarda junto al archivo, para no volver a pagar al guardar la compra
    public static string ComoJson(FacturaElectronica.Factura f) => JsonSerializer.Serialize(f, Json);
    public static FacturaElectronica.Factura DesdeJson(string json) =>
        JsonSerializer.Deserialize<FacturaElectronica.Factura>(json, Json) ?? throw new InvalidOperationException("Datos de factura dañados.");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static FacturaElectronica.Factura Convertir(Leida l)
    {
        var f = new FacturaElectronica.Factura
        {
            Numero = (l.Numero ?? "").Trim(),
            Cufe = (l.Cufe ?? "").Trim(),
            Fecha = DateTime.TryParse(l.Fecha, out var fecha) ? fecha : DateTime.Today,
            ProveedorNit = new string((l.ProveedorNit ?? "").Where(char.IsDigit).ToArray()),
            ProveedorNombre = (l.ProveedorNombre ?? "").Trim(),
            Moneda = string.IsNullOrWhiteSpace(l.Moneda) ? "COP" : l.Moneda.Trim(),
            Subtotal = l.Subtotal, Impuestos = l.Impuestos, Descuentos = l.Descuentos, Total = l.Total
        };
        if (f.Numero.Length == 0)
            throw new InvalidOperationException("No se pudo leer el número de la factura.");

        var n = 0;
        foreach (var x in l.Lineas!)
        {
            n++;
            var barras = (x.CodigoBarras ?? "").Trim();
            f.Lineas.Add(new FacturaElectronica.Linea
            {
                Numero = n,
                CodigoProveedor = (x.Codigo ?? "").Trim(),
                CodigoBarras = barras.Length is 8 or 12 or 13 or 14 && barras.All(char.IsDigit) ? barras : null,
                Descripcion = (x.Descripcion ?? "").Trim(),
                Cantidad = x.Cantidad,
                Unidad = string.IsNullOrWhiteSpace(x.Unidad) ? null : x.Unidad.Trim(),
                PrecioUnitario = x.PrecioUnitario,
                Descuento = x.Descuento,
                ValorSinImpuestos = x.ValorSinImpuestos,
                Impuestos = x.Impuestos
            });
        }
        if (f.Total == 0) f.Total = f.Lineas.Sum(x => x.ValorTotal);
        return f;
    }

    private class Leida
    {
        [JsonPropertyName("numero")] public string? Numero { get; set; }
        [JsonPropertyName("cufe")] public string? Cufe { get; set; }
        [JsonPropertyName("fecha")] public string? Fecha { get; set; }
        [JsonPropertyName("proveedor_nit")] public string? ProveedorNit { get; set; }
        [JsonPropertyName("proveedor_nombre")] public string? ProveedorNombre { get; set; }
        [JsonPropertyName("moneda")] public string? Moneda { get; set; }
        [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
        [JsonPropertyName("impuestos")] public decimal Impuestos { get; set; }
        [JsonPropertyName("descuentos")] public decimal Descuentos { get; set; }
        [JsonPropertyName("total")] public decimal Total { get; set; }
        [JsonPropertyName("lineas")] public List<LineaLeida>? Lineas { get; set; }
    }

    private class LineaLeida
    {
        [JsonPropertyName("codigo")] public string? Codigo { get; set; }
        [JsonPropertyName("codigo_barras")] public string? CodigoBarras { get; set; }
        [JsonPropertyName("descripcion")] public string? Descripcion { get; set; }
        [JsonPropertyName("cantidad")] public decimal Cantidad { get; set; }
        [JsonPropertyName("unidad")] public string? Unidad { get; set; }
        [JsonPropertyName("precio_unitario")] public decimal PrecioUnitario { get; set; }
        [JsonPropertyName("descuento")] public decimal Descuento { get; set; }
        [JsonPropertyName("valor_sin_impuestos")] public decimal ValorSinImpuestos { get; set; }
        [JsonPropertyName("impuestos")] public decimal Impuestos { get; set; }
    }
}
