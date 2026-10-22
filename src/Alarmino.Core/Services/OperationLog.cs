namespace Alarmino.Core.Services;

/// <summary>
/// Registro de operaciones de la sesión en %APPDATA%\Alarmino\logs\alarmino-yyyyMMdd.log.
/// Cada línea lleva la hora con milisegundos y el origen: <c>HH:mm:ss.fff [origen] mensaje</c>.
/// <para>
/// Es "best-effort": cualquier fallo al registrar se traga en silencio para que una
/// avería del log jamás impida que suene la alarma. También guarda en memoria las
/// últimas líneas (<see cref="RecentLines"/>) para la vista de diagnóstico.
/// </para>
/// </summary>
public static class OperationLog
{
    /// <summary>Número máximo de líneas recientes conservadas en memoria.</summary>
    public const int MaxRecentLines = 200;

    private static readonly object Gate = new();
    private static readonly Queue<string> Recent = new(MaxRecentLines);
    private static string? _rootOverride;

    /// <summary>
    /// Carpeta raíz del log (por defecto %APPDATA%\Alarmino). La sobreescriben las
    /// pruebas para no escribir en la carpeta real del usuario.
    /// </summary>
    public static string RootDirectory =>
        _rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Alarmino");

    /// <summary>Carpeta de logs del sistema actual.</summary>
    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>Archivo del día actual.</summary>
    public static string CurrentFilePath => Path.Combine(LogDirectory, $"alarmino-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Últimas líneas registradas (las más recientes al final), para mostrarlas en
    /// la vista de diagnóstico. Nunca supera <see cref="MaxRecentLines"/>.
    /// </summary>
    public static IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (Gate)
            {
                return Recent.ToList();
            }
        }
    }

    /// <summary>
    /// Se dispara con cada línea escrita para que el panel de diagnóstico pueda
    /// mostrar el log en vivo. Se invoca fuera del lock y un suscriptor que falle
    /// no impide que el resto se registren.
    /// </summary>
    public static event Action<string>? LineWritten;

    /// <summary>Escribe una línea con el origen indicado. Si algo falla, no hace nada.</summary>
    public static void Write(string source, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{source}] {message}";
        try
        {
            lock (Gate)
            {
                Recent.Enqueue(line);
                while (Recent.Count > MaxRecentLines)
                {
                    Recent.Dequeue();
                }

                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // El log nunca debe romper el funcionamiento de la aplicación.
        }

        NotifyLineWritten(line);
    }

    /// <summary>Avisa a los suscriptores de una línea nueva (sin dejar que fallen).</summary>
    private static void NotifyLineWritten(string line)
    {
        Action<string>? handlers = LineWritten;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(line);
            }
            catch
            {
                // Un panel de diagnóstico roto no puede impedir que se registre el log.
            }
        }
    }

    /// <summary>
    /// Cambia la carpeta raíz del log. Solo para pruebas: pasar null restaura el
    /// valor por defecto (%APPDATA%\Alarmino).
    /// </summary>
    public static void ResetRoot(string? root)
    {
        lock (Gate)
        {
            _rootOverride = root;
            Recent.Clear();
        }
    }
}
