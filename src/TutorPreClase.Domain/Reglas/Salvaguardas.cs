using System.Text.RegularExpressions;

namespace TutorPreClase.Domain.Reglas;

/// <summary>Cita [archivo, p. N] detectada en la respuesta del agente.</summary>
public readonly record struct Cita(string Archivo, int Pagina, bool Valida);

/// <summary>
/// Salvaguardas de la respuesta del tutor (SDD §6.4). Se aplican en el servidor: la
/// seguridad no depende de que el modelo obedezca el prompt.
/// </summary>
public static partial class Salvaguardas
{
    public const string MarcaAmpliacion = "Ampliación fuera del material:";

    public const string SinCobertura =
        "Esto no está en el material de la clase; pregúntalo en la sesión.";

    [GeneratedRegex(@"Ampliaci[óo]n\s+fuera\s+del\s+material\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexAmpliacion();

    // [Clase03_RedesProfundas.pdf, p. 12]
    [GeneratedRegex(@"\[\s*(?<archivo>[^\[\],]+?)\s*,\s*p\.\s*(?<pagina>\d{1,5})\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex RegexCita();

    public static bool ContieneAmpliacion(string texto) =>
        !string.IsNullOrEmpty(texto) && RegexAmpliacion().IsMatch(texto);

    /// <summary>Dónde empieza la marca de ampliación, o -1 si no está.</summary>
    public static int PosicionAmpliacion(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return -1;

        var match = RegexAmpliacion().Match(texto);
        return match.Success ? match.Index : -1;
    }

    /// <summary>
    /// Corta el texto en la marca de ampliación cuando no está permitida (modo Evaluación
    /// o clase con AmpliacionPermitida = false) y deja el aviso estándar.
    /// </summary>
    public static string CortarAmpliacion(string texto)
    {
        if (string.IsNullOrEmpty(texto)) return texto;

        var match = RegexAmpliacion().Match(texto);
        if (!match.Success) return texto;

        var previo = texto[..match.Index].TrimEnd();
        return previo.Length == 0 ? SinCobertura : $"{previo}\n\n{SinCobertura}";
    }

    /// <summary>Extrae las citas del texto y las resuelve contra las páginas de la clase.</summary>
    public static IReadOnlyList<Cita> ExtraerCitas(string texto, IReadOnlySet<(string Archivo, int Pagina)> paginasDeLaClase)
    {
        if (string.IsNullOrEmpty(texto)) return [];

        var vistas = new HashSet<(string, int)>();
        var citas = new List<Cita>();

        foreach (Match m in RegexCita().Matches(texto))
        {
            var archivo = m.Groups["archivo"].Value.Trim();
            if (!int.TryParse(m.Groups["pagina"].Value, out var pagina)) continue;
            if (!vistas.Add((archivo, pagina))) continue;

            citas.Add(new Cita(archivo, pagina, paginasDeLaClase.Contains((archivo, pagina))));
        }

        return citas;
    }

    /// <summary>
    /// En revisión la respuesta debe apoyarse en algo: una cita válida o una ampliación
    /// marcada. Si no, se considera no publicable y se pide al agente que rehaga.
    /// </summary>
    public static bool RespuestaSustentada(string texto, IReadOnlyList<Cita> citas) =>
        citas.Any(c => c.Valida) || ContieneAmpliacion(texto);
}
