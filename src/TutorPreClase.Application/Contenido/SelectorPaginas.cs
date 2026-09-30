using System.Globalization;
using System.Text;

namespace TutorPreClase.Application.Contenido;

/// <summary>
/// Elige las páginas del material más relacionadas con la pregunta (SDD §6.2). Por debajo
/// del umbral devuelve el contexto completo: con poco material, enviarlo todo es igual de
/// rápido y no se arriesga a dejar fuera la página que importa.
/// </summary>
/// <remarks>
/// BM25 en memoria sobre las páginas ya limpias. Una clase tiene decenas de páginas, así
/// que puntuarlas en cada pregunta cuesta microsegundos y no hace falta un índice.
/// </remarks>
public static class SelectorPaginas
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int LargoMinimoTermino = 3;

    private static readonly HashSet<string> Vacias =
    [
        "que", "los", "las", "del", "por", "para", "con", "una", "uno", "unos", "unas", "como",
        "mas", "pero", "sus", "este", "esta", "estos", "estas", "ese", "esa", "eso", "esto",
        "hay", "son", "ser", "fue", "tiene", "entre", "sobre", "cual", "cuales", "donde",
        "cuando", "porque", "muy", "sin", "tambien", "puede", "hace", "nos", "les", "todo",
        "todos", "dice", "dime", "explica", "explicame", "quiero", "saber", "tengo", "duda",
        "dudas", "the", "and"
    ];

    public static ContextoClase Seleccionar(ContextoClase contexto, string consulta, OpcionesContextoClase opciones)
    {
        if (contexto.TokensTotales <= opciones.TokensParaSeleccionar) return contexto;

        var terminos = Terminos(consulta).Distinct().ToList();
        if (terminos.Count == 0) return contexto;

        var paginas = contexto.Paginas;
        var terminosPorPagina = paginas.Select(p => Terminos(p.Texto).ToList()).ToList();
        var largoMedio = Math.Max(1, terminosPorPagina.Average(t => t.Count));

        var puntajes = new double[paginas.Count];
        foreach (var termino in terminos)
        {
            var conTermino = terminosPorPagina.Count(t => t.Contains(termino));
            if (conTermino == 0) continue;

            var idf = Math.Log(1 + (paginas.Count - conTermino + 0.5) / (conTermino + 0.5));

            for (var i = 0; i < paginas.Count; i++)
            {
                var frecuencia = terminosPorPagina[i].Count(t => t == termino);
                if (frecuencia == 0) continue;

                var largo = terminosPorPagina[i].Count;
                puntajes[i] += idf * frecuencia * (K1 + 1) /
                               (frecuencia + K1 * (1 - B + B * largo / largoMedio));
            }
        }

        // Nada coincide: mejor el material completo que unas páginas al azar.
        if (puntajes.All(p => p == 0)) return contexto;

        var elegidas = new HashSet<int>();
        var tokens = 0;

        foreach (var i in Enumerable.Range(0, paginas.Count)
                     .Where(i => puntajes[i] > 0)
                     .OrderByDescending(i => puntajes[i]))
        {
            if (tokens + paginas[i].Tokens > opciones.TokensSeleccionados && elegidas.Count > 0) continue;

            elegidas.Add(i);
            tokens += paginas[i].Tokens;
        }

        var enviadas = elegidas.Order().Select(i => paginas[i]).ToList();

        return contexto with
        {
            Texto = ArmadorContextoClase.Bloque(contexto.Titulo, enviadas, seleccion: true),
            Tokens = tokens,
            Truncado = false,
            PaginasIncluidas = enviadas.Select(p => (p.Archivo, p.Pagina)).ToHashSet()
        };
    }

    /// <summary>Minúsculas, sin tildes, solo letras y dígitos, sin palabras vacías.</summary>
    internal static IEnumerable<string> Terminos(string texto)
    {
        var sb = new StringBuilder();

        foreach (var c in texto.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (sb.Length > 0)
            {
                var termino = sb.ToString();
                sb.Clear();
                if (termino.Length >= LargoMinimoTermino && !Vacias.Contains(termino)) yield return Singular(termino);
            }
        }

        if (sb.Length >= LargoMinimoTermino && !Vacias.Contains(sb.ToString())) yield return Singular(sb.ToString());
    }

    /// <summary>"costos" y "costo", "indicadores" e "indicador" cuentan como el mismo término.</summary>
    private static string Singular(string termino) => termino switch
    {
        { Length: > 5 } when termino.EndsWith("es", StringComparison.Ordinal) => termino[..^2],
        { Length: > 4 } when termino.EndsWith('s') => termino[..^1],
        _ => termino
    };
}
