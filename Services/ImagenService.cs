using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Ferreteria.Services;

// Guarda imágenes reducidas (máximo 800 px, JPG) dentro de wwwroot/img/<carpeta>/.
// Usa System.Drawing.Common (gratuita, solo Windows).
public static class ImagenService
{
    private const int TamanoMaximo = 800;
    private const long CalidadJpg = 80L;
    private const long TamanoMaximoBytes = 10L * 1024 * 1024;
    private const int TagOrientacionExif = 0x0112;

    private static readonly string[] ExtensionesPermitidas = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    // Devuelve la ruta relativa a wwwroot, por ejemplo: img/categorias/categoria-638000000000000000.jpg
    public static async Task<string> GuardarAsync(IFormFile archivo, string carpeta, string prefijo, IWebHostEnvironment entorno)
    {
        var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
        if (!ExtensionesPermitidas.Contains(extension))
            throw new InvalidOperationException("El archivo debe ser una imagen JPG, PNG, GIF o BMP.");

        if (archivo.Length > TamanoMaximoBytes)
            throw new InvalidOperationException("La imagen es muy grande. El máximo permitido es 10 MB.");

        using var entrada = new MemoryStream();
        await archivo.CopyToAsync(entrada);
        entrada.Position = 0;

        var nombreArchivo = $"{prefijo}-{DateTime.UtcNow.Ticks}.jpg";
        var carpetaFisica = Path.Combine(entorno.WebRootPath, "img", carpeta);
        Directory.CreateDirectory(carpetaFisica);
        var rutaFisica = Path.Combine(carpetaFisica, nombreArchivo);

        try
        {
            using var original = new Bitmap(entrada);
            CorregirOrientacion(original);

            using var reducida = Reducir(original);

            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parametros = new EncoderParameters(1);
            parametros.Param[0] = new EncoderParameter(Encoder.Quality, CalidadJpg);
            reducida.Save(rutaFisica, codec, parametros);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("No se pudo leer la imagen. Intente con otra foto.");
        }

        return $"img/{carpeta}/{nombreArchivo}";
    }

    // Borra un archivo de imagen guardado con GuardarAsync (si existe).
    public static void Eliminar(string? rutaRelativa, IWebHostEnvironment entorno)
    {
        if (string.IsNullOrWhiteSpace(rutaRelativa))
            return;

        var rutaFisica = Path.Combine(entorno.WebRootPath, rutaRelativa.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(rutaFisica))
            File.Delete(rutaFisica);
    }

    // Las fotos de celular suelen guardar la orientación en metadatos; se aplica antes de reducir.
    private static void CorregirOrientacion(Image imagen)
    {
        if (!imagen.PropertyIdList.Contains(TagOrientacionExif))
            return;

        var valor = imagen.GetPropertyItem(TagOrientacionExif).Value[0];
        switch (valor)
        {
            case 3:
                imagen.RotateFlip(RotateFlipType.Rotate180FlipNone);
                break;
            case 6:
                imagen.RotateFlip(RotateFlipType.Rotate90FlipNone);
                break;
            case 8:
                imagen.RotateFlip(RotateFlipType.Rotate270FlipNone);
                break;
        }
    }

    // Reduce la imagen manteniendo la proporción; el fondo queda blanco (para PNG con transparencia).
    private static Bitmap Reducir(Image original)
    {
        var escala = Math.Min(1.0, (double)TamanoMaximo / Math.Max(original.Width, original.Height));
        var ancho = Math.Max(1, (int)Math.Round(original.Width * escala));
        var alto = Math.Max(1, (int)Math.Round(original.Height * escala));

        var bitmap = new Bitmap(ancho, alto);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.DrawImage(original, 0, 0, ancho, alto);
        return bitmap;
    }
}