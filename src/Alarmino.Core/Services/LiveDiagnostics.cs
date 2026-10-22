using System.Globalization;

namespace Alarmino.Core.Services;

/// <summary>Estado de la conexión a Internet en el momento de la última comprobación.</summary>
public enum NetworkState
{
    /// <summary>Todavía no se ha comprobado en esta sesión.</summary>
    Unknown,

    /// <summary>Hay conexión: YouTube respondió a la comprobación.</summary>
    Online,

    /// <summary>No hay conexión, o YouTube no respondió a tiempo.</summary>
    Offline,
}

/// <summary>
/// Una pista guardada en la caché, vista desde el panel de diagnóstico.
/// </summary>
/// <param name="PlaylistId">Playlist a la que pertenece.</param>
/// <param name="VideoId">Vídeo de YouTube del que se descargó.</param>
/// <param name="Consumed">True si ya se ha reproducido (no sirve para la próxima alarma).</param>
/// <param name="FileExists">True si el archivo sigue en disco.</param>
/// <param name="Bytes">Tamaño real del archivo en disco.</param>
/// <param name="DownloadedAtUtc">Momento de la descarga.</param>
public readonly record struct CachedTrackInfo(
    string PlaylistId,
    string VideoId,
    bool Consumed,
    bool FileExists,
    long Bytes,
    DateTime DownloadedAtUtc);

/// <summary>
/// Foto del estado operativo que muestra el panel de diagnóstico en vivo: si hay
/// red, si hay alguna pista descargada y qué está haciendo el reproductor.
/// </summary>
/// <param name="Network">Estado de la conexión.</param>
/// <param name="NetworkCheckedUtc">Cuándo se comprobó la conexión por última vez.</param>
/// <param name="ReadyTracks">Pistas sin usar listas para la próxima alarma.</param>
/// <param name="TargetTracks">Objetivo de pistas del búfer.</param>
/// <param name="Tracks">Detalle de las pistas guardadas.</param>
/// <param name="DownloadActive">True si hay una descarga en curso.</param>
/// <param name="PlayerState">Texto del reproductor.</param>
public readonly record struct DiagnosticsSnapshot(
    NetworkState Network,
    DateTime? NetworkCheckedUtc,
    int ReadyTracks,
    int TargetTracks,
    IReadOnlyList<CachedTrackInfo> Tracks,
    bool DownloadActive,
    string PlayerState)
{
    /// <summary>True si hay al menos una pista lista para la próxima alarma.</summary>
    public bool HasReadyTrack => ReadyTracks > 0;
}

/// <summary>
/// Redacta el bloque de estado del panel de diagnóstico. Es texto plano (nada de
/// WPF aquí): el panel lo pinta y las pruebas comprueban las cadenas exactas.
/// </summary>
public static class DiagnosticsFormatter
{
    /// <summary>Redacta el bloque completo de estado (una línea por elemento).</summary>
    public static IReadOnlyList<string> Describe(DiagnosticsSnapshot snapshot)
    {
        var lines = new List<string>
        {
            DescribeNetwork(snapshot.Network, snapshot.NetworkCheckedUtc),
            DescribeTracks(snapshot.ReadyTracks, snapshot.TargetTracks),
        };

        if (snapshot.Tracks.Count == 0)
        {
            lines.Add("  · (no hay ninguna pista en caché)");
        }
        else
        {
            foreach (CachedTrackInfo track in snapshot.Tracks)
            {
                lines.Add("  · " + DescribeTrack(track));
            }
        }

        lines.Add($"Reproductor: {snapshot.PlayerState}");
        lines.Add(snapshot.DownloadActive ? "Descarga en curso: sí" : "Descarga en curso: no");

        return lines;
    }

    /// <summary>Estado de la red en una línea legible.</summary>
    public static string DescribeNetwork(NetworkState state, DateTime? checkedUtc)
    {
        string text = state switch
        {
            NetworkState.Online => "Red: con conexión",
            NetworkState.Offline => "Red: sin conexión",
            _ => "Red: sin comprobar",
        };

        return checkedUtc is null
            ? text
            : $"{text} (comprobada a las {checkedUtc.Value.ToLocalTime():HH:mm:ss})";
    }

    /// <summary>Disponibilidad de pistas descargadas en una línea legible.</summary>
    public static string DescribeTracks(int ready, int target)
    {
        return ready > 0
            ? $"Pista descargada: disponible ({ready} de {target} listas)"
            : $"Pista descargada: NINGUNA disponible (0 de {target})";
    }

    /// <summary>Detalle de una pista en caché.</summary>
    public static string DescribeTrack(CachedTrackInfo track)
    {
        string state = !track.FileExists
            ? "archivo ausente"
            : track.Consumed
                ? "ya reproducida"
                : "sin usar";

        string size = track.FileExists ? FormatBytes(track.Bytes) : "—";

        return $"{track.PlaylistId} · {track.VideoId} · {size} · {state} · " +
               $"descargada a las {track.DownloadedAtUtc.ToLocalTime():HH:mm:ss}";
    }

    /// <summary>Tamaño en bytes con una unidad legible (KB/MB).</summary>
    public static string FormatBytes(long bytes)
    {
        // Formato invariante: el log técnico debe leerse igual en cualquier
        // equipo, y con la cultura local el separador decimal cambiaría.
        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{(bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        }

        return $"{(bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }
}
