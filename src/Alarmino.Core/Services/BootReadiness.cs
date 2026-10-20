namespace Alarmino.Core.Services;

/// <summary>
/// Veredicto de arranque para una playlist: si hay o no una pista lista para
/// sonar y cuántas hay frente al objetivo del búfer.
/// </summary>
/// <param name="PlaylistId">Id de la playlist (la caché se indexa por id, no por URL).</param>
/// <param name="Ready">Pistas sin consumir listas para el próximo arranque.</param>
/// <param name="Target">Objetivo del búfer de descargas.</param>
public readonly record struct TrackReadiness(string PlaylistId, int Ready, int Target)
{
    /// <summary>
    /// True si hay al menos una pista lista: la alarma puede sonar sin descargar.
    /// El búfer puede estar incompleto (falta rellenar) y aun así estar preparado.
    /// </summary>
    public bool IsReady => Ready > 0;
}

/// <summary>
/// Traduce el estado del búfer de una playlist a la frase que se registra al
/// arrancar. Vive en Core, sin WPF, para poder probarse: la ventana solo aporta
/// el estado real de la caché y el id de la playlist.
/// <para>
/// El veredicto distingue «lista» de «nada listo» de forma explícita, para que el
/// log del arranque diga con claridad si hay algo descargado para la próxima
/// alarma o si hay que esperar a la descarga.
/// </para>
/// </summary>
public static class BootReadiness
{
    /// <summary>Veredicto a partir del id de la playlist y el estado del búfer.</summary>
    public static string Describe(string playlistId, BufferState state) => Describe(new TrackReadiness(playlistId, state.Ready, state.Target));

    /// <summary>Veredicto a partir del estado ya calculado.</summary>
    public static string Describe(TrackReadiness readiness) =>
        readiness.IsReady
            ? $"playlist {readiness.PlaylistId}: {readiness.Ready}/{readiness.Target} pistas listas."
            : $"playlist {readiness.PlaylistId}: nada descargado ({readiness.Ready}/{readiness.Target} pistas listas); se descarga ahora.";
}
