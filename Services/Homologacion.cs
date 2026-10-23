using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ferreteria.Services;

// Homologación: decidir a qué producto nuestro corresponde una línea de la factura de un proveedor,
// con un % de confianza y la explicación. No toca la base de datos: recibe los datos ya cargados,
// así se puede probar sola (ver la carpeta Pruebas).
//
// Orden (gana la primera regla que encuentre algo):
//   1. Alias confirmado: mismo proveedor y mismo código de producto        -> 100 %
//   2. Código de barras igual al de un producto nuestro                     ->  98 %
//   3. Alias confirmado con la misma descripción normalizada del proveedor  ->  95 %
//   4. Comparación de la descripción con todos los productos del negocio    -> 0..89 % (siempre pide aprobación)
// Reglas duras en el paso 4: si las medidas, el color o el material son distintos, se descarta
// aunque el texto se parezca (un 6x1 nunca debe confundirse con un 6x1-1/2).
public static partial class Homologacion
{
    public const string Automatico = "automatico";
    public const string Aprobar = "aprobar";
    public const string SinCoincidencia = "sin";

    public record Producto(int Id, string Nombre, string? Medida, string? CodigoBarras);

    public record Alias(int ProductoId, string? CodigoProveedor, string? DescripcionNormalizada, int Factor);

    public record Resultado(int? ProductoId, int Confianza, string Motivo, string Estado, int Factor);

    public class Umbrales
    {
        public int Automatico { get; set; } = 90;
        public int Sugerir { get; set; } = 60;
    }

    public static Resultado Identificar(
        string? codigoProveedor, string? codigoBarras, string descripcion,
        IReadOnlyList<Alias> aliasDelProveedor, IReadOnlyList<Producto> productos, Umbrales umbrales)
    {
        string Estado(int c) => c >= umbrales.Automatico ? Automatico : c >= umbrales.Sugerir ? Aprobar : SinCoincidencia;
        var normal = Normalizar(descripcion);

        // 1. Mismo proveedor y mismo código, ya confirmado por una persona
        if (!string.IsNullOrWhiteSpace(codigoProveedor) &&
            aliasDelProveedor.FirstOrDefault(a => a.CodigoProveedor == codigoProveedor && productos.Any(p => p.Id == a.ProductoId)) is { } porCodigo)
            return new(porCodigo.ProductoId, 100, "Ya se compró antes a este proveedor con el mismo código.", Estado(100), porCodigo.Factor);

        // 2. Código de barras
        if (!string.IsNullOrWhiteSpace(codigoBarras) && productos.FirstOrDefault(p => p.CodigoBarras == codigoBarras) is { } porBarras)
            return new(porBarras.Id, 98, $"El código de barras {codigoBarras} es el mismo.", Estado(98), 1);

        // 3. Misma descripción (normalizada) ya confirmada con este proveedor
        if (normal.Length > 0 &&
            aliasDelProveedor.FirstOrDefault(a => a.DescripcionNormalizada == normal && productos.Any(p => p.Id == a.ProductoId)) is { } porDescripcion)
            return new(porDescripcion.ProductoId, 95, "Ya se confirmó antes esta misma descripción del proveedor.", Estado(95), porDescripcion.Factor);

        // 4. Comparar la descripción con los productos
        var fact = Analizar(descripcion);
        (Producto? Prod, int Puntaje, string Motivo) mejor = (null, 0, "No se parece a ningún producto del inventario.");
        string? descartado = null;

        foreach (var p in productos)
        {
            var prod = Analizar(p.Nombre + " " + p.Medida);
            if (Conflicto(fact, prod) is { } conflicto)
            {
                if (Similitud(fact.Palabras, prod.Palabras) >= 0.6) descartado ??= $"\"{p.Nombre} {p.Medida}\" se parece, pero {conflicto}.";
                continue;
            }

            var sim = Similitud(fact.Palabras, prod.Palabras);
            // Medidas iguales en los dos suman confianza; si solo uno tiene medida, resta
            var bonoMedida = fact.Medidas.Count > 0 && prod.Medidas.Count > 0 ? 0.15
                           : fact.Medidas.Count != prod.Medidas.Count ? -0.1 : 0;
            // Tope de 89 %: una coincidencia solo por el texto nunca entra sola al inventario;
            // la primera vez una persona la aprueba y desde ahí queda como alias (100 %).
            var puntaje = (int)Math.Round(Math.Clamp(sim + bonoMedida, 0, 1) * 89);
            if (puntaje > mejor.Puntaje)
            {
                var motivo = $"Se parece a \"{p.Nombre}{(string.IsNullOrWhiteSpace(p.Medida) ? "" : " " + p.Medida)}\" ({(int)Math.Round(sim * 100)} % de palabras en común)";
                if (fact.Medidas.Count > 0 && prod.Medidas.Count > 0) motivo += $"; misma medida {string.Join(", ", fact.Medidas)}";
                mejor = (p, puntaje, motivo + ".");
            }
        }

        if (mejor.Prod is null || mejor.Puntaje < umbrales.Sugerir)
        {
            var motivo = descartado is not null ? "Sin coincidencia segura: " + descartado : mejor.Motivo;
            return new(mejor.Puntaje >= 30 ? mejor.Prod?.Id : null, mejor.Puntaje, motivo, SinCoincidencia, 1);
        }
        var factor = fact.Presentacion is > 1 ? fact.Presentacion.Value : 1;
        var motivoFinal = factor > 1 ? mejor.Motivo + $" La factura dice x{factor}: se proponen {factor} unidades por cada una." : mejor.Motivo;
        return new(mejor.Prod.Id, mejor.Puntaje, motivoFinal, Estado(mejor.Puntaje), factor);
    }

    // ---------- Normalización ----------

    // Abreviaturas comunes en facturas de ferretería
    private static readonly Dictionary<string, string> Abreviaturas = new()
    {
        ["torn"] = "tornillo", ["tornil"] = "tornillo", ["tor"] = "tornillo", ["tornillos"] = "tornillo",
        ["galv"] = "galvanizado", ["galvan"] = "galvanizado", ["galvanizada"] = "galvanizado",
        ["inox"] = "inoxidable", ["acer"] = "acero", ["hex"] = "hexagonal", ["hexag"] = "hexagonal",
        ["arand"] = "arandela", ["arandelas"] = "arandela", ["tuer"] = "tuerca", ["tuercas"] = "tuerca",
        ["chazos"] = "chazo", ["puntillas"] = "puntilla", ["drywal"] = "drywall", ["dw"] = "drywall",
        ["bco"] = "blanco", ["blanc"] = "blanco", ["ngr"] = "negro", ["neg"] = "negro",
        ["und"] = "", ["un"] = "", ["unid"] = "", ["unidad"] = "", ["unidades"] = "", ["ref"] = "", ["x"] = "",
        ["de"] = "", ["con"] = "", ["para"] = "", ["y"] = "", ["en"] = "", ["el"] = "", ["la"] = "",
        ["ptabroca"] = "broca", ["pta"] = "punta", ["autop"] = "autoperforante", ["autoperf"] = "autoperforante",
        ["estr"] = "estrella", ["phill"] = "estrella", ["philips"] = "estrella", ["phillips"] = "estrella",
    };

    private static readonly string[] Colores = { "blanco", "negro", "gris", "rojo", "azul", "verde", "amarillo", "cafe", "transparente", "dorado", "plateado" };
    private static readonly string[] Materiales = { "acero", "inoxidable", "galvanizado", "bronce", "zinc", "plastico", "nylon", "aluminio", "cobre", "hierro", "pvc", "madera" };

    public static string Normalizar(string texto) => string.Join(" ", Analizar(texto).Todas);

    public class Rasgos
    {
        public List<string> Todas { get; } = new();          // todas las palabras normalizadas, en orden
        public HashSet<string> Palabras { get; } = new();    // sin medidas, colores ni materiales
        public SortedSet<string> Medidas { get; } = new();
        public SortedSet<string> Colores { get; } = new();
        public SortedSet<string> Materiales { get; } = new();
        public int? Presentacion { get; set; }               // "x100", "caja x12": unidades que trae cada una
    }

    public static Rasgos Analizar(string? texto)
    {
        var r = new Rasgos();
        var t = SinTildes((texto ?? "").ToLowerInvariant());

        // Medidas como "6x1", "6 x 1-1/2", "1/4", "1 1/2", "25kg", "3.5mm", "60x40"
        t = Regex.Replace(t, @"(\d)\s*[-]\s*(\d+/\d+)", "$1 $2");            // 1-1/2 -> 1 1/2
        t = Regex.Replace(t, @"(\d)\s+(\d+/\d+)", "$1+$2");                  // 1 1/2 -> 1+1/2 (una sola medida)
        t = Regex.Replace(t, @"(\d[\d.,+/]*)\s*x\s*(\d[\d.,+/]*)", "$1x$2"); // 6 x 1 -> 6x1
        t = Regex.Replace(t, @"(^|\s)x\s+(\d+)\b", "$1x$2");                     // "caja x 100" -> "caja x100" (la x sola)
        t = Regex.Replace(t, @"(\d[\d.,]*)\s*(mm|cm|m|kg|kgs|k|gr|g|lb|lbs|gal|ml|lt|l|ft|in|pulg)\b", "$1$2");
        t = Regex.Replace(t, @"[^a-z0-9.,+/x ]", " ");

        foreach (var crudo in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = crudo.Trim('.', ',');
            if (w.Length == 0) continue;
            if (Abreviaturas.TryGetValue(w, out var reemplazo)) w = reemplazo;
            if (w.Length == 0) continue;

            // Presentación: "x100", "x12" = cada una trae esas unidades
            if (Regex.Match(w, @"^x(\d{1,5})$") is { Success: true } pres)
            {
                r.Presentacion = int.Parse(pres.Groups[1].Value);
                r.Todas.Add(w);
                continue;
            }

            if (Regex.IsMatch(w, @"^\d[\d.,+/x]*[a-z]{0,4}$"))
            {
                var medida = w.Replace(',', '.').Replace("kgs", "kg").Replace("lbs", "lb");
                medida = Regex.Replace(medida, @"(\d)k$", "$1kg");
                r.Medidas.Add(medida);
                r.Todas.Add(medida);
                continue;
            }
            r.Todas.Add(w);
            if (Colores.Contains(w)) r.Colores.Add(w);
            else if (Materiales.Contains(w)) r.Materiales.Add(w);
            else if (w.Length > 1) r.Palabras.Add(Raiz(w));
        }
        return r;
    }

    // Diferencias que impiden que sean el mismo producto
    public static string? Conflicto(Rasgos a, Rasgos b)
    {
        if (a.Medidas.Count > 0 && b.Medidas.Count > 0 && !a.Medidas.SetEquals(b.Medidas))
            return $"la medida es distinta ({string.Join(" ", a.Medidas)} contra {string.Join(" ", b.Medidas)})";
        if (a.Colores.Count > 0 && b.Colores.Count > 0 && !a.Colores.Overlaps(b.Colores))
            return $"el color es distinto ({string.Join(" ", a.Colores)} contra {string.Join(" ", b.Colores)})";
        if (a.Materiales.Count > 0 && b.Materiales.Count > 0 && !a.Materiales.Overlaps(b.Materiales))
            return $"el material es distinto ({string.Join(" ", a.Materiales)} contra {string.Join(" ", b.Materiales)})";
        return null;
    }

    // Parecido entre dos grupos de palabras (0 a 1), tolerante a errores de escritura u OCR
    public static double Similitud(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        double comunes = 0;
        foreach (var x in a)
        {
            if (b.Contains(x)) comunes += 1;
            else if (b.Any(y => x.Length >= 4 && y.Length >= 4 && (y.StartsWith(x) || x.StartsWith(y) || Distancia(x, y) <= 1)))
                comunes += 0.8;
        }
        return 2 * comunes / (a.Count + b.Count);
    }

    // Plural simple: "tornillos" -> "tornillo", "tuercas" -> "tuerca"
    private static string Raiz(string w) => w.Length > 4 && w.EndsWith("es") ? w[..^2] : w.Length > 3 && w.EndsWith('s') ? w[..^1] : w;

    private static int Distancia(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    private static string SinTildes(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }
}
