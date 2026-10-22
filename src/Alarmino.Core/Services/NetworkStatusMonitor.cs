namespace Alarmino.Core.Services;

/// <summary>
/// Vigila el estado de la conexión sin martillear la red: comprueba si hay
/// conexión cada cierto tiempo y, entre comprobaciones, responde con el último
/// estado conocido.
/// <para>
/// Existe como tipo aparte (y con el reloj inyectado) para poder probar el
/// ritmo de comprobación sin esperar de verdad en el reloj.
/// </para>
/// </summary>
public sealed class NetworkStatusMonitor
{
    private readonly IConnectivityProbe _probe;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _interval;

    /// <summary>
    /// Crea un monitor. <paramref name="interval"/> es el tiempo mínimo entre
    /// comprobaciones reales (15 s por defecto) y <paramref name="utcNow"/> el
    /// reloj (solo para pruebas).
    /// </summary>
    public NetworkStatusMonitor(
        IConnectivityProbe probe,
        TimeSpan? interval = null,
        Func<DateTime>? utcNow = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _interval = interval ?? TimeSpan.FromSeconds(15);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Estado de la última comprobación.</summary>
    public NetworkState State { get; private set; } = NetworkState.Unknown;

    /// <summary>Momento de la última comprobación real; null si nunca se comprobó.</summary>
    public DateTime? CheckedUtc { get; private set; }

    /// <summary>True si toca volver a comprobar la conexión.</summary>
    public bool IsStale => CheckedUtc is null || _utcNow() - CheckedUtc.Value >= _interval;

    /// <summary>
    /// Comprueba la conexión y actualiza el estado. Si no ha pasado el intervalo
    /// desde la última comprobación (y no se fuerza), devuelve el estado conocido
    /// sin tocar la red. Una comprobación fallida cuenta como «sin conexión».
    /// </summary>
    public async Task<NetworkState> RefreshAsync(bool force = false)
    {
        if (!force && !IsStale)
        {
            return State;
        }

        bool online;
        try
        {
            online = await _probe.IsOnlineAsync().ConfigureAwait(false);
        }
        catch
        {
            // Un fallo al comprobar no puede romper la app: se trata como sin red.
            online = false;
        }

        State = online ? NetworkState.Online : NetworkState.Offline;
        CheckedUtc = _utcNow();
        return State;
    }
}
