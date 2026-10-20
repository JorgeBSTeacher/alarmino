namespace Alarmino.Core.Services;

/// <summary>
/// Veredicto del respaldo sin conexión tras resolverlo al arrancar. Existe para
/// que el log del arranque diga las DOS cosas: que el respaldo ya configurado se
/// validó (el caso normal, que antes no registraba nada) o que no hay ninguno.
/// <para>
/// El caso «el archivo configurado sigue siendo válido» es el camino habitual de
/// arranque: sin una línea propia, el log nunca confirma que hay respaldo.
/// </para>
/// </summary>
/// <param name="ChosenPath">Respaldo elegido, o null si no hay ninguno válido.</param>
/// <param name="WasConfiguredPathKept">
/// True si el archivo configurado se conservó por seguir siendo válido.
/// </param>
/// <param name="Examined">Rutas examinadas (para el diagnóstico cuando falla).</param>
public readonly record struct OfflineFallbackOutcome(
    string? ChosenPath,
    bool WasConfiguredPathKept,
    IReadOnlyList<string> Examined);

/// <summary>Traduce el resultado de <see cref="OfflineFallbackSelector"/> a la línea de log del arranque.</summary>
public static class OfflineFallbackReadiness
{
    /// <summary>Línea de log que describe el resultado, siempre presente (ambos casos).</summary>
    public static string Describe(OfflineFallbackOutcome outcome)
    {
        if (outcome.ChosenPath is null)
        {
            return $"sin respaldo sin conexión; examinados: {string.Join(", ", outcome.Examined)}";
        }

        return outcome.WasConfiguredPathKept
            ? $"respaldo sin conexión validado: {outcome.ChosenPath}"
            : $"respaldo sin conexión establecido: {outcome.ChosenPath}";
    }
}
