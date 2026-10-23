using System.Text;

namespace Ferreteria.Services;

// Dibuja un código de barras como SVG, para imprimir etiquetas sin internet.
// - 13 números con dígito de control correcto: EAN-13 (el de los productos de fábrica y los internos "20...").
// - Cualquier otro código (letras, guiones): Code 128.
public static class CodigoBarrasSvg
{
    public static string Dibujar(string codigo, double alto = 40)
    {
        var barras = EsEan13(codigo) ? Ean13(codigo) : Code128(codigo);
        var ancho = barras.Length;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {ancho + 20} {alto}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"Código de barras {System.Net.WebUtility.HtmlEncode(codigo)}\">");
        sb.Append($"<rect width=\"{ancho + 20}\" height=\"{alto}\" fill=\"#fff\"/>");
        // Las barras negras seguidas se dibujan como un solo rectángulo
        for (var i = 0; i < ancho; i++)
        {
            if (barras[i] != '1') continue;
            var inicio = i;
            while (i + 1 < ancho && barras[i + 1] == '1') i++;
            sb.Append($"<rect x=\"{inicio + 10}\" y=\"0\" width=\"{i - inicio + 1}\" height=\"{alto}\" fill=\"#000\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static bool EsEan13(string codigo) =>
        codigo.Length == 13 && codigo.All(char.IsDigit) && CodigoBarras.Ean13(codigo[..12]) == codigo;

    // ---------- EAN-13 ----------
    private static readonly string[] L = { "0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011" };
    private static readonly string[] G = { "0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111" };
    private static readonly string[] R = { "1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100" };
    private static readonly string[] Paridad = { "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG", "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL" };

    private static string Ean13(string c)
    {
        var sb = new StringBuilder("101");
        var p = Paridad[c[0] - '0'];
        for (var i = 1; i <= 6; i++) sb.Append(p[i - 1] == 'L' ? L[c[i] - '0'] : G[c[i] - '0']);
        sb.Append("01010");
        for (var i = 7; i <= 12; i++) sb.Append(R[c[i] - '0']);
        sb.Append("101");
        return sb.ToString();
    }

    // ---------- Code 128 (juego B) ----------
    private static readonly string[] Patrones =
    {
        "212222","222122","222221","121223","121322","131222","122213","122312","132212","221213","221312","231212","112232","122132","122231","113222","123122","123221","223211","221132","221231","213212","223112","312131","311222","321122","321221","312212","322112","322211","212123","212321","232121","111323","131123","131321","112313","132113","132311","211313","231113","231311","112133","112331","132131","113123","113321","133121","313121","211331","231131","213113","213311","213131","311123","311321","331121","312113","312311","332111","314111","221411","431111","111224","111422","121124","121421","141122","141221","112214","112412","122114","122411","142112","142211","241211","221114","413111","241112","134111","111242","121142","121241","114212","124112","124211","411212","421112","421211","212141","214121","412121","111143","111341","131141","114113","114311","411113","411311","113141","114131","311141","411131","211412","211214","211232","2331112"
    };

    private static string Code128(string texto)
    {
        var valores = new List<int> { 104 }; // inicio juego B
        foreach (var ch in texto)
            valores.Add(ch >= 32 && ch <= 126 ? ch - 32 : 0);
        var suma = valores[0];
        for (var i = 1; i < valores.Count; i++) suma += valores[i] * i;
        valores.Add(suma % 103);
        valores.Add(106); // fin

        var sb = new StringBuilder();
        foreach (var v in valores)
        {
            var patron = Patrones[v];
            for (var i = 0; i < patron.Length; i++)
                sb.Append(i % 2 == 0 ? '1' : '0', patron[i] - '0');
        }
        return sb.ToString();
    }
}
