namespace Alarmino.Core.Services;

/// <summary>
/// Veredicto de arranque para una playlist: cuántas pistas hay descargadas en
/// disco para sonar sin esperar a la red.
/// </summary>
/// <param name="PlaylistId">Id de la playlist (la caché se indexa por id, no por URL).</param>
/// <param name="Downloaded">Pistas descargadas y presentes en disco.</param>
public readonly record struct TrackReadiness(string PlaylistId, int Downloaded)
{
    /// <summary>
    /// True si hay al menos una pista descargada: la alarma puede sonar sin
    /// descargar.
    /// </summary>
    public bool IsReady => Downloaded > 0;
}

/// <summary>
/// Traduce el estado de descargas de una playlist a la frase que se registra al
/// arrancar. Vive en Core, sin WPF, para poder probarse: la ventana solo aporta
/// el estado real de la caché y el id de la playlist.
/// <para>
/// El veredicto distingue «hay pistas descargadas» de «nada descargado» de forma
/// explícita, para que el log del arranque diga con claridad si hay algo para la
/// próxima alarma o si hay que esperar a la descarga.
/// </para>
/// </summary>
public static class BootReadiness
{
    /// <summary>Veredicto a partir del estado ya calculado.</summary>
    public static string Describe(TrackReadiness readiness) =>
        readiness.IsReady
            ? $"playlist {readiness.PlaylistId}: {readiness.Downloaded} pista(s) descargada(s)."
            : $"playlist {readiness.PlaylistId}: nada descargado (0); se descarga ahora.";
}
