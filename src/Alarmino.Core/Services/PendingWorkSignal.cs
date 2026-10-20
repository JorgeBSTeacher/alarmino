namespace Alarmino.Core.Services;

/// <summary>
/// Señal de «hay trabajo pendiente» pensada para llamarse desde muchos sitios
/// (cada cambio de ajustes o de alarma) sin acumular trabajos de fondo: la señal
/// se levanta una vez y, mientras nadie la recoja, las peticiones repetidas se
/// agrupan en una sola.
/// <para>
/// El patrón es el de un pestillo de coalescencia: quien pide el trabajo llama a
/// <see cref="Request"/> (barato, sin bloqueos de espera) y quien lo ejecuta
/// llama a <see cref="TryTake"/> para saber si tiene algo que hacer. Varias
/// peticiones seguidas producen, como mucho, un trabajo por cada recojo: no hay
/// una cola que crezca sin límite, solo un bit.
/// </para>
/// <para>
/// Es seguro entre hilos: <see cref="Request"/> puede llamarse desde el hilo de
/// interfaz mientras <see cref="TryTake"/> se ejecuta en un hilo de fondo.
/// </para>
/// </summary>
public sealed class PendingWorkSignal
{
    private int _pending;

    /// <summary>
    /// Marca que hay trabajo pendiente. Las peticiones repetidas mientras sigue
    /// pendiente no cambian nada: el siguiente <see cref="TryTake"/> las agrupa.
    /// Devuelve <c>true</c> solo si esta llamada fue la que levantó la señal
    /// (la anterior no estaba pendiente).
    /// </summary>
    public bool Request() => Interlocked.Exchange(ref _pending, 1) == 0;

    /// <summary>
    /// True si había trabajo pendiente, y lo da por consumido en la misma
    /// operación atómica. Las peticiones que lleguen después de este recojo
    /// vuelven a levantar la señal y se atenderán en el siguiente recojo.
    /// </summary>
    public bool TryTake() => Interlocked.Exchange(ref _pending, 0) == 1;

    /// <summary>True si ahora mismo hay trabajo pendiente sin recoger.</summary>
    public bool IsPending => Volatile.Read(ref _pending) == 1;
}
