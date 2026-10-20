namespace Alarmino.Core.Services;

/// <summary>
/// Traduce el estado del reproductor (descarga en curso, reproducción activa y
/// tiempo transcurrido) a la frase que se muestra al usuario.
/// <para>
/// Vive en Core, sin dependencias de WPF, para poder probarse: la ventana solo
/// aporta el estado y el contador de segundos. La descarga no oculta una
/// reproducción en curso, y un contador negativo se recorta a cero.
/// </para>
/// </summary>
public static class PlayerStatusFormatter
{
    /// <summary>Frase mostrada cuando no hay nada sonando ni descargándose.</summary>
    public const string IdleText = "En espera";

    /// <summary>Frase mostrada mientras una pista de YouTube se está descargando.</summary>
    public const string DownloadingText = "Descargando…";

    /// <summary>
    /// Descripción del estado: <c>En espera</c>, <c>Descargando…</c> o
    /// <c>Reproduciendo — mm:ss</c> (con las horas por delante cuando la
    /// reproducción pasa de una hora).
    /// </summary>
    /// <param name="isPlaying">Hay audio reproduciéndose ahora mismo.</param>
    /// <param name="isDownloading">Hay una descarga de YouTube en curso.</param>
    /// <param name="label">
    /// Qué está sonando («el botón ALARMA», «la alarma «Recreo»»…). Se acepta por
    /// si en el futuro se quiere enriquecer la frase; la frase actual no lo usa.
    /// </param>
    /// <param name="elapsed">Tiempo reproduciendo.</param>
    public static string Describe(bool isPlaying, bool isDownloading, string? label, TimeSpan elapsed)
    {
        if (isPlaying)
        {
            TimeSpan safe = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
            string counter = safe.TotalHours >= 1
                ? $"{(int)safe.TotalHours:00}:{safe.Minutes:00}:{safe.Seconds:00}"
                : $"{safe.Minutes:00}:{safe.Seconds:00}";
            return $"Reproduciendo — {counter}";
        }

        return isDownloading ? DownloadingText : IdleText;
    }
}
