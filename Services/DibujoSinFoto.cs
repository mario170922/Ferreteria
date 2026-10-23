using System.Globalization;
using System.Text;

namespace Ferreteria.Services;

// Elige el dibujo que se muestra cuando un producto no tiene foto, según el tipo de
// producto que se adivina por su nombre (y su categoría, si se conoce).
// Los dibujos están en wwwroot/img/sin-foto/. Si no se reconoce el tipo, sale una caja.
public static class DibujoSinFoto
{
    // Gana la palabra que aparece primero en el nombre: "Tornillo drywall punta broca"
    // es un tornillo, y "Broca para concreto" es una broca.
    private static readonly (string Tipo, string[] Palabras)[] Tipos =
    {
        ("tuerca",   new[] { "tuerca" }),
        ("arandela", new[] { "arandela", "guasa" }),
        ("chazo",    new[] { "chazo", "taco", "anclaje", "expansivo" }),
        ("broca",    new[] { "broca" }),
        ("puntilla", new[] { "puntilla", "clavo" }),
        ("tornillo", new[] { "tornillo", "perno", "drywall", "autoperforante", "tirafondo", "esparrago", "varilla roscada", "remache" })
    };

    public static string Ruta(string? nombre, string? categorias = null)
    {
        var texto = SinTildes($"{nombre} {categorias}".ToLowerInvariant());

        string elegido = "otro";
        int primeraPosicion = int.MaxValue;

        foreach (var (tipo, palabras) in Tipos)
        {
            foreach (var palabra in palabras)
            {
                var posicion = texto.IndexOf(palabra, StringComparison.Ordinal);
                if (posicion >= 0 && posicion < primeraPosicion)
                {
                    primeraPosicion = posicion;
                    elegido = tipo;
                }
            }
        }

        return $"/img/sin-foto/{elegido}.svg";
    }

    private static string SinTildes(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
