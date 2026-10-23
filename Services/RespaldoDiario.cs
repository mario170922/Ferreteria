using System.Diagnostics;
using Npgsql;

namespace Ferreteria.Services;

// Copia de seguridad automática de la base de datos, una vez al día, mientras el sistema esté prendido.
// Usa pg_dump (viene con PostgreSQL) y guarda los archivos en C:\proyectos\respaldos
// (se puede cambiar en appsettings.json: "Respaldos": { "Carpeta": ..., "PgDump": ..., "Dias": 30 }).
// Se conservan los últimos 30 días; los más viejos se borran solos.
public class RespaldoDiario : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<RespaldoDiario> _log;

    public RespaldoDiario(IConfiguration config, ILogger<RespaldoDiario> log)
    {
        _config = config;
        _log = log;
    }

    private string Carpeta => _config["Respaldos:Carpeta"] is { Length: > 0 } c ? c : @"C:\proyectos\respaldos";
    private int Dias => int.TryParse(_config["Respaldos:Dias"], out var d) && d > 0 ? d : 30;

    protected override async Task ExecuteAsync(CancellationToken parar)
    {
        // Se espera un minuto para no demorar el arranque
        try { await Task.Delay(TimeSpan.FromMinutes(1), parar); } catch (TaskCanceledException) { return; }

        while (!parar.IsCancellationRequested)
        {
            try
            {
                if (!HayRespaldoDeHoy()) await RespaldarAsync(parar);
                BorrarViejos();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo hacer la copia de seguridad automática.");
            }

            try { await Task.Delay(TimeSpan.FromHours(1), parar); } catch (TaskCanceledException) { return; }
        }
    }

    private bool HayRespaldoDeHoy() =>
        Directory.Exists(Carpeta) &&
        Directory.GetFiles(Carpeta, $"ferreteria-auto-{DateTime.Today:yyyy-MM-dd}*.backup").Length > 0;

    private async Task RespaldarAsync(CancellationToken parar)
    {
        var pgDump = BuscarPgDump();
        if (pgDump is null)
        {
            _log.LogWarning("No se encontró pg_dump.exe; configure Respaldos:PgDump en appsettings.json.");
            return;
        }

        Directory.CreateDirectory(Carpeta);
        var datos = new NpgsqlConnectionStringBuilder(_config.GetConnectionString("Ferreteria"));
        var archivo = Path.Combine(Carpeta, $"ferreteria-auto-{DateTime.Now:yyyy-MM-dd-HHmm}.backup");

        var inicio = new ProcessStartInfo(pgDump)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[] { "-h", datos.Host ?? "localhost", "-p", datos.Port.ToString(), "-U", datos.Username ?? "postgres",
                                  "-d", datos.Database ?? "ferreteria", "-F", "c", "-f", archivo })
            inicio.ArgumentList.Add(a);
        inicio.Environment["PGPASSWORD"] = datos.Password ?? "";

        using var proceso = Process.Start(inicio)!;
        var errores = await proceso.StandardError.ReadToEndAsync(parar);
        await proceso.WaitForExitAsync(parar);

        if (proceso.ExitCode == 0 && File.Exists(archivo) && new FileInfo(archivo).Length > 0)
            _log.LogInformation("Copia de seguridad guardada: {Archivo}", archivo);
        else
        {
            if (File.Exists(archivo)) File.Delete(archivo);
            _log.LogWarning("pg_dump falló ({Codigo}): {Errores}", proceso.ExitCode, errores);
        }
    }

    private string? BuscarPgDump()
    {
        if (_config["Respaldos:PgDump"] is { Length: > 0 } ruta && File.Exists(ruta)) return ruta;
        var baseDir = @"C:\Program Files\PostgreSQL";
        if (!Directory.Exists(baseDir)) return null;
        return Directory.GetDirectories(baseDir)
            .OrderByDescending(d => d)
            .Select(d => Path.Combine(d, "bin", "pg_dump.exe"))
            .FirstOrDefault(File.Exists);
    }

    // Solo se borran las copias automáticas viejas; las hechas a mano no se tocan
    private void BorrarViejos()
    {
        if (!Directory.Exists(Carpeta)) return;
        foreach (var f in Directory.GetFiles(Carpeta, "ferreteria-auto-*.backup"))
        {
            if (File.GetCreationTime(f) < DateTime.Now.AddDays(-Dias)) File.Delete(f);
        }
    }
}
