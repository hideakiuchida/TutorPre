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

    // Lo que va entre corchetes y menciona una página: [Clase03_RedesProfundas.pdf, p. 12]
    [GeneratedRegex(@"\[(?<contenido>[^\[\]]*\bp{1,2}\.\s*\d[^\[\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex RegexCita();

    // Una fuente dentro del corchete: "archivo, p. 39, 44" o "archivo, pp. 3-5".
    [GeneratedRegex(@"^\s*(?<archivo>.+?)\s*,\s*p{1,2}\.\s*(?<paginas>\d{1,5}(?:\s*(?:,|y|-|–)\s*\d{1,5})*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RegexFuente();

    [GeneratedRegex(@"\d{1,5}", RegexOptions.CultureInvariant)]
    private static partial Regex RegexPagina();

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

    /// <summary>
    /// Extrae las citas del texto y las resuelve contra las páginas de la clase. Los modelos
    /// suelen juntar varias en un corchete: `[a.pdf, p. 44; b.pdf, p. 1]` o `[a.pdf, p. 39, 44]`.
    /// </summary>
    public static IReadOnlyList<Cita> ExtraerCitas(string texto, IReadOnlySet<(string Archivo, int Pagina)> paginasDeLaClase)
    {
        if (string.IsNullOrEmpty(texto)) return [];

        var vistas = new HashSet<(string, int)>();
        var citas = new List<Cita>();

        foreach (Match corchete in RegexCita().Matches(texto))
        {
            foreach (var fuente in corchete.Groups["contenido"].Value.Split(';'))
            {
                var m = RegexFuente().Match(fuente);
                if (!m.Success) continue;

                var citado = m.Groups["archivo"].Value.Trim();

                foreach (Match numero in RegexPagina().Matches(m.Groups["paginas"].Value))
                {
                    var pagina = int.Parse(numero.Value);
                    var archivo = Resolver(citado, pagina, paginasDeLaClase);
                    if (!vistas.Add((archivo ?? citado, pagina))) continue;

                    citas.Add(new Cita(archivo ?? citado, pagina, archivo is not null));
                }
            }
        }

        return citas;
    }

    /// <summary>
    /// El archivo de la clase al que apunta la cita, o null si no hay ninguno. Algunos modelos
    /// abrevian el nombre ("M1" por "M1 Estados Financieros.pdf"): vale si esa página existe
    /// en un único archivo cuyo nombre empieza así; si hay dos candidatos, no se adivina.
    /// </summary>
    private static string? Resolver(string citado, int pagina, IReadOnlySet<(string Archivo, int Pagina)> paginasDeLaClase)
    {
        if (paginasDeLaClase.Contains((citado, pagina))) return citado;

        var candidatos = paginasDeLaClase
            .Where(p => p.Pagina == pagina && p.Archivo.StartsWith(citado, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Archivo)
            .Distinct()
            .Take(2)
            .ToList();

        return candidatos.Count == 1 ? candidatos[0] : null;
    }

    /// <summary>
    /// En revisión la respuesta debe apoyarse en algo: una cita válida o una ampliación
    /// marcada. Si no, se considera no publicable y se pide al agente que rehaga.
    /// </summary>
    public static bool RespuestaSustentada(string texto, IReadOnlyList<Cita> citas) =>
        citas.Any(c => c.Valida) || ContieneAmpliacion(texto);
}
