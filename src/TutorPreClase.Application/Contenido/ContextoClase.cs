using System.Text;
using System.Text.RegularExpressions;
using TutorPreClase.Domain.Entidades;

namespace TutorPreClase.Application.Contenido;

/// <summary>Una página del material ya limpia, con su cita [archivo, p. N].</summary>
public sealed record PaginaContexto(string Archivo, int Pagina, string Texto, int Tokens);

/// <summary>
/// Contexto de clase armado (SDD §6.2), listo para el prompt de sistema. `Texto` son las
/// páginas que se envían; `PaginasDeLaClase`, todas: contra ellas se validan las citas,
/// aunque para una pregunta concreta solo se hayan enviado algunas.
/// </summary>
public sealed record ContextoClase(
    string Titulo,
    string Texto,
    int Tokens,
    bool Truncado,
    IReadOnlySet<(string Archivo, int Pagina)> PaginasIncluidas,
    IReadOnlySet<(string Archivo, int Pagina)> PaginasDeLaClase,
    IReadOnlyList<PaginaContexto> Paginas)
{
    public static ContextoClase Vacio { get; } =
        new("", "", 0, false, new HashSet<(string, int)>(), new HashSet<(string, int)>(), []);

    /// <summary>Tokens de todo el material, se envíe completo o no.</summary>
    public int TokensTotales => Paginas.Sum(p => p.Tokens);
}

public sealed class OpcionesContextoClase
{
    public const string Seccion = "ContextoClase";

    /// <summary>Tope duro del contexto de clase (SDD §6.1).</summary>
    public int TokensMaximos { get; set; } = 150_000;

    /// <summary>A partir de aquí se avisa al docente en la pantalla de la clase.</summary>
    public int TokensAviso { get; set; } = 120_000;

    /// <summary>
    /// Por encima de esto, en Consulta y Revisión solo van las páginas pertinentes a la
    /// pregunta (SDD §6.2): menos tokens de entrada, respuesta más rápida.
    /// </summary>
    public int TokensParaSeleccionar { get; set; } = 4_000;

    /// <summary>Cuánto material se envía, como máximo, cuando se seleccionan páginas.</summary>
    public int TokensSeleccionados { get; set; } = 3_000;
}

/// <summary>
/// Arma el bloque &lt;contenido&gt; concatenando las páginas de la clase en orden de
/// archivo y página, cada una con su marca [archivo, p. N] — la misma cita que debe
/// usar el agente. Antes limpia el texto extraído (LimpiadorTexto). Con `condensado`,
/// cada página va con sus apuntes de la IA si los tiene y el profesor no pidió el original.
/// </summary>
public static class ArmadorContextoClase
{
    public static ContextoClase Armar(
        string tituloClase,
        IEnumerable<ArchivoContenido> archivos,
        OpcionesContextoClase opciones,
        bool condensado = false)
    {
        var paginas = archivos
            .Where(a => a.Estado == EstadoArchivo.Listo)
            .OrderBy(a => a.Nombre, StringComparer.OrdinalIgnoreCase)
            .SelectMany(a => LimpiadorTexto.Limpiar(a.Nombre, a.Paginas
                .OrderBy(p => p.Pagina)
                .Select(p => (p.Pagina, condensado && !a.UsarOriginal && p.TextoCondensado is not null ? p.TextoCondensado : p.Texto))))
            .ToList();

        if (paginas.Count == 0) return ContextoClase.Vacio;

        var todas = paginas.Select(p => (p.Archivo, p.Pagina)).ToHashSet();
        var enviadas = new List<PaginaContexto>();
        var tokens = 0;
        var truncado = false;

        foreach (var p in paginas)
        {
            if (tokens + p.Tokens > opciones.TokensMaximos)
            {
                truncado = true;
                break;
            }

            enviadas.Add(p);
            tokens += p.Tokens;
        }

        return new ContextoClase(
            tituloClase,
            Bloque(tituloClase, enviadas, seleccion: false),
            tokens,
            truncado,
            enviadas.Select(p => (p.Archivo, p.Pagina)).ToHashSet(),
            todas,
            paginas);
    }

    /// <summary>El bloque &lt;contenido&gt; con las páginas dadas, en orden de archivo y página.</summary>
    public static string Bloque(string tituloClase, IEnumerable<PaginaContexto> paginas, bool seleccion)
    {
        var sb = new StringBuilder();
        sb.Append("<contenido clase=\"").Append(Escapar(tituloClase)).Append('"');

        // El modelo debe saber que no ve todo, para no afirmar que algo no está en el material.
        if (seleccion) sb.Append(" seleccion=\"páginas más relacionadas con la pregunta\"");
        sb.Append(">\n");

        foreach (var p in paginas)
        {
            sb.Append('[').Append(p.Archivo).Append(", p. ").Append(p.Pagina).Append("]\n");
            sb.Append(p.Texto).Append("\n\n");
        }

        sb.Append("</contenido>");
        return sb.ToString();
    }

    private static string Escapar(string valor) => valor.Replace("\"", "'");
}

/// <summary>
/// Limpia el texto extraído de un archivo antes de enviarlo al modelo: quita los
/// encabezados y pies que se repiten en casi todas las páginas de un PDF o una
/// presentación, los números de página sueltos y los espacios de sobra. Menos tokens por
/// pregunta, el mismo contenido. En hojas de cálculo y documentos no quita repetidas: ahí
/// una línea que se repite ("Total") es dato.
/// </summary>
public static partial class LimpiadorTexto
{
    /// <summary>Una línea en al menos esta fracción de las páginas es encabezado o pie.</summary>
    private const double FraccionRepetida = 0.6;

    private const int PaginasMinimas = 3;
    private const int LargoMaximoRepetida = 120;

    private static readonly string[] ConEncabezados = [".pdf", ".pptx"];

    // "12", "Página 12", "pág. 12 de 59", "12 / 59"
    [GeneratedRegex(@"^(?<pag>p[aá]g(ina)?\.?\s*)?(?<n>\d{1,4})(\s*(de|/)\s*\d{1,4})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexNumeroPagina();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex RegexEspacios();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RegexSaltos();

    public static IReadOnlyList<PaginaContexto> Limpiar(string archivo, IEnumerable<(int Pagina, string Texto)> paginas)
    {
        var lista = paginas.ToList();

        var repetidas = new HashSet<string>(StringComparer.Ordinal);
        var conEncabezados = ConEncabezados.Contains(Path.GetExtension(archivo).ToLowerInvariant());

        if (conEncabezados && lista.Count >= PaginasMinimas)
        {
            var apariciones = lista
                .SelectMany(p => Lineas(p.Texto).Where(l => l.Length is > 0 and <= LargoMaximoRepetida).Distinct())
                .GroupBy(l => l)
                .Where(g => g.Count() >= Math.Max(PaginasMinimas, lista.Count * FraccionRepetida));

            foreach (var g in apariciones) repetidas.Add(g.Key);
        }

        return lista
            .Select(p =>
            {
                var lineas = Lineas(p.Texto)
                    .Where(l => !repetidas.Contains(l) && !EsNumeroDePagina(l, p.Pagina));

                var texto = RegexSaltos().Replace(string.Join('\n', lineas), "\n\n").Trim();
                return new PaginaContexto(archivo, p.Pagina, texto, ContadorTokens.Estimar(texto));
            })
            .ToList();
    }

    /// <summary>Un número suelto solo es número de página si coincide con ella o lo dice.</summary>
    private static bool EsNumeroDePagina(string linea, int pagina)
    {
        var m = RegexNumeroPagina().Match(linea);
        return m.Success && (m.Groups["pag"].Success || int.Parse(m.Groups["n"].Value) == pagina);
    }

    private static IEnumerable<string> Lineas(string texto) =>
        texto.Replace("\r\n", "\n").Split('\n').Select(l => RegexEspacios().Replace(l, " ").Trim());
}
