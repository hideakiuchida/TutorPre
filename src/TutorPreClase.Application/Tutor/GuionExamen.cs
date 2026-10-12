using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TutorPreClase.Application.Tutor;

/// <summary>
/// Lo que el servidor escribe y comprueba por su cuenta durante el examen (SDD §6.4): la
/// pregunta la presenta el servidor tal como la guardó, y un modelo que dice "Registrada"
/// sin haber registrado, inventa una pregunta o cita el material no llega al alumno.
/// </summary>
public static partial class GuionExamen
{
    public const string Registrada = "Registrada.";

    public const string SoloLaLetra =
        "Durante el examen no puedo ayudarte con eso. Para responder, escribe la letra de la " +
        "alternativa que eliges (A, B, C o D).";

    public const string RecordatorioHerramientas =
        "(Recordatorio del servidor: durante el examen no afirmes que una respuesta quedó registrada " +
        "sin llamar a registrar_respuesta, no escribas tú las preguntas ni sus alternativas —las " +
        "muestra el servidor cuando llamas a obtener_siguiente_pregunta— y no cites ni resumas el " +
        "material. Si el alumno eligió una alternativa, llama ahora a registrar_respuesta; si pide " +
        "ayuda, dile en una línea que no puedes darla.)";

    [GeneratedRegex(@"\bregistr(?:ad[ao]s?|é)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexRegistrada();

    [GeneratedRegex(@"\bpregunta\s+\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexNumeroDePregunta();

    // Una alternativa al inicio de línea: "- A) …", "**B.** …", "C: …".
    [GeneratedRegex(@"^[\s\-\*•]*[A-D][\)\.:]", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RegexAlternativa();

    [GeneratedRegex(@"\[[^\[\]]*\bp{1,2}\.\s*\d[^\[\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex RegexCita();

    [GeneratedRegex(
        @"^\s*(?:(?:creo|pienso|dir[ií]a|me parece)\s+que\s+)?(?:(?:elijo|escojo|marco|respondo|voy con|me quedo con|es|ser[ií]a)\s+)?" +
        @"(?:(?:la|el)\s+)?(?:(?:opci[oó]n|alternativa|letra|respuesta)\s+)?\(?(?<letra>[A-Da-d])\)?[\s\.\!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexLetraDirecta();

    [GeneratedRegex(
        @"\b(?:termin\w*|finaliz\w*|entreg\w*|acab\w*|rendirme|me\s+rindo|abandon\w*|no\s+quiero\s+seguir)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RegexPideTerminar();

    /// <summary>
    /// Por qué el texto del modelo no puede mostrarse en pleno examen, o null si puede.
    /// `intentoRegistrar`: en este turno ya se llamó a registrar_respuesta.
    /// </summary>
    public static string? Falta(string texto, bool intentoRegistrar)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "respuesta vacía";
        if (RegexNumeroDePregunta().IsMatch(texto) || RegexAlternativa().Matches(texto).Count >= 3)
            return "presenta una pregunta que no entregó el servidor";

        if (intentoRegistrar) return null;

        if (RegexRegistrada().IsMatch(texto)) return "dice que registró sin llamar a registrar_respuesta";
        if (RegexCita().IsMatch(texto)) return "cita el material antes de registrar la respuesta";

        return null;
    }

    /// <summary>"B", "la b", "creo que es la C": una letra sin nada más que interpretar.</summary>
    public static string? LetraDirecta(string mensaje)
    {
        var coincidencia = RegexLetraDirecta().Match(mensaje);
        return coincidencia.Success ? coincidencia.Groups["letra"].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex RegexPalabra();

    // Con cualquiera de estas el mensaje no es una respuesta: es una duda o un pedido.
    private static readonly HashSet<string> NoEsRespuesta =
    [
        "no", "ni", "pista", "pistas", "ayuda", "ayudame", "explica", "explicame", "significa", "duda",
        "que", "cual", "como", "porque", "cuando", "donde", "quien", "sera", "seria", "tal", "vez", "quizas", "o"
    ];

    private static readonly HashSet<string> Opina = ["creo", "pienso", "parece", "diria", "supongo"];

    private static readonly HashSet<string> Relleno =
    [
        "a", "al", "con", "creo", "de", "del", "el", "en", "es", "esa", "ese", "eso", "la", "las", "lo", "los",
        "me", "mi", "para", "por", "se", "su", "sus", "un", "una", "y", "yo", "elijo", "escojo", "quedo", "voy",
        "respuesta", "alternativa", "opcion", "pienso", "parece", "diria", "marco", "respondo", "supongo"
    ];

    /// <summary>
    /// La alternativa que el alumno nombró por su contenido ("el estratégico", "la de la salud
    /// financiera"), o null si no es inequívoco. Registrar es irreversible, así que solo vale
    /// cuando todo lo que dijo está en una única alternativa y el mensaje no es una pregunta
    /// ni un pedido: lo dudoso lo interpreta el tutor, que puede pedir confirmación.
    /// </summary>
    public static string? LetraPorContenido(string mensaje, IEnumerable<(string Letra, string Texto)> alternativas)
    {
        if (mensaje.Contains('?') || mensaje.Contains('¿')) return null;

        // "Creo que es…" es una respuesta; un "que" suelto suele ser una pregunta sin tilde.
        var todas = Palabras(mensaje);
        var palabras = todas.Where((p, i) => !(p == "que" && i > 0 && Opina.Contains(todas[i - 1]))).ToList();
        if (palabras.Any(NoEsRespuesta.Contains)) return null;

        var dichas = palabras.Where(p => !Relleno.Contains(p)).ToHashSet();
        if (dichas.Count == 0 || dichas.Sum(p => p.Length) < 4) return null;

        var coinciden = alternativas
            .Where(a => dichas.IsSubsetOf(Palabras(a.Texto)))
            .Select(a => a.Letra)
            .ToList();

        return coinciden.Count == 1 ? coinciden[0] : null;
    }

    /// <summary>Palabras en minúsculas y sin tildes.</summary>
    private static List<string> Palabras(string texto)
    {
        var plano = new StringBuilder();
        foreach (var c in texto.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) plano.Append(c);

        return RegexPalabra().Matches(plano.ToString()).Select(m => m.Value).ToList();
    }

    /// <summary>El alumno pide cerrar el examen aunque le queden preguntas.</summary>
    public static bool PideTerminar(string mensaje) => RegexPideTerminar().IsMatch(mensaje);

    /// <summary>
    /// La pregunta tal como la devuelve obtener_siguiente_pregunta, o null si el resultado
    /// no trae una (error, o no quedan).
    /// </summary>
    public static string? Pregunta(string resultadoJson)
    {
        using var doc = JsonDocument.Parse(resultadoJson);
        var raiz = doc.RootElement;

        if (!raiz.TryGetProperty("enunciado", out var enunciado) || enunciado.ValueKind != JsonValueKind.String)
            return null;

        var sb = new StringBuilder();
        sb.Append($"**Pregunta {raiz.GetProperty("numero").GetInt32()} de {raiz.GetProperty("total").GetInt32()}**\n\n");
        sb.Append(enunciado.GetString()).Append("\n\n");

        foreach (var alternativa in raiz.GetProperty("alternativas").EnumerateArray())
            sb.Append($"- **{alternativa.GetProperty("letra").GetString()})** {alternativa.GetProperty("texto").GetString()}\n");

        sb.Append("\n¿Cuál es tu respuesta?");
        return sb.ToString();
    }

    /// <summary>La nota tal como la calculó el servidor al cerrar el intento.</summary>
    public static string Nota(string resultadoJson)
    {
        using var doc = JsonDocument.Parse(resultadoJson);
        var raiz = doc.RootElement;

        var nota = raiz.GetProperty("nota").GetDecimal().ToString("0.0", CultureInfo.InvariantCulture);
        return $"**Examen terminado.** Tu nota es **{nota}** sobre 20: " +
               $"{raiz.GetProperty("correctas").GetInt32()} de {raiz.GetProperty("total").GetInt32()} correctas.";
    }

    /// <summary>
    /// La retroalimentación que escribe el servidor cuando el tutor no llega a dar la suya: sale
    /// del resultado del intento (qué falló, cuál era la correcta y por qué), sin pasar por la IA.
    /// </summary>
    public static string Retroalimentacion(string resultadoJson)
    {
        using var doc = JsonDocument.Parse(resultadoJson);
        var raiz = doc.RootElement;

        var fallos = raiz.GetProperty("fallos").EnumerateArray().ToList();
        var sinResponder = raiz.GetProperty("total").GetInt32() - raiz.GetProperty("correctas").GetInt32() - fallos.Count;

        var sb = new StringBuilder("**Retroalimentación**\n\n");

        if (fallos.Count == 0 && sinResponder <= 0)
        {
            sb.Append("Acertaste todas las preguntas: llegas bien preparado a la clase. " +
                      "Si quieres profundizar en algún punto del tema, pregúntame.");
            return sb.ToString();
        }

        var temas = TemasAReforzar(resultadoJson);
        if (temas.Count > 0)
            sb.Append($"Te conviene reforzar: {string.Join(", ", temas.Select(t => $"**{t}**"))}.\n\n");

        for (var i = 0; i < fallos.Count; i++)
        {
            var fallo = fallos[i];
            sb.Append($"**{i + 1}. {fallo.GetProperty("enunciado").GetString()}**\n\n");
            sb.Append($"- Tu respuesta: {fallo.GetProperty("alternativa_elegida").GetString()}\n");
            sb.Append($"- La correcta: {fallo.GetProperty("alternativa_correcta").GetString()}\n");

            var justificacion = fallo.GetProperty("justificacion").GetString();
            if (!string.IsNullOrWhiteSpace(justificacion)) sb.Append($"- Por qué: {justificacion}\n");

            sb.Append('\n');
        }

        if (sinResponder > 0)
            sb.Append(sinResponder == 1
                ? "Dejaste 1 pregunta sin responder: cuenta como no acertada.\n\n"
                : $"Dejaste {sinResponder} preguntas sin responder: cuentan como no acertadas.\n\n");

        sb.Append(fallos.Count > 0
            ? "Cuando quieras, escribe «revisemos mis errores» y los repasamos uno por uno."
            : "Repasa el material de la clase y, si te queda una duda, pregúntame.");

        return sb.ToString();
    }

    /// <summary>Los temas de las preguntas falladas, sin repetir y sin el genérico "General".</summary>
    public static IReadOnlyList<string> TemasAReforzar(string resultadoJson)
    {
        using var doc = JsonDocument.Parse(resultadoJson);

        return doc.RootElement.GetProperty("fallos").EnumerateArray()
            .Select(f => f.TryGetProperty("tema", out var tema) && tema.ValueKind == JsonValueKind.String
                ? tema.GetString()!.Trim()
                : "")
            .Where(t => t.Length > 0 && !t.Equals("General", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool Verdadero(string resultadoJson, string propiedad)
    {
        using var doc = JsonDocument.Parse(resultadoJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object &&
               doc.RootElement.TryGetProperty(propiedad, out var valor) &&
               valor.ValueKind == JsonValueKind.True;
    }

    /// <summary>El mensaje de un resultado de error, para decírselo al alumno.</summary>
    public static string MensajeDeError(string resultadoJson)
    {
        using var doc = JsonDocument.Parse(resultadoJson);
        return doc.RootElement.TryGetProperty("mensaje", out var mensaje) && mensaje.ValueKind == JsonValueKind.String
            ? mensaje.GetString() ?? ""
            : "";
    }
}
