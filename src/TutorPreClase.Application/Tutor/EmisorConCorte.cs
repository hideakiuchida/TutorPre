using System.Text;
using TutorPreClase.Domain.Reglas;

namespace TutorPreClase.Application.Tutor;

/// <summary>
/// Decide qué parte de la respuesta del modelo puede ver ya el alumno (RNF-02) sin romper
/// el corte de la ampliación (SDD §6.4). Cuando la ampliación no está permitida, retiene
/// solo los últimos caracteres, los suficientes para contener la marca "Ampliación fuera
/// del material:" aunque llegue partida entre fragmentos. Como la marca siempre precede a
/// la ampliación, nada de lo que viene detrás llega al alumno.
/// </summary>
public sealed class EmisorConCorte(bool retenerAmpliacion)
{
    /// <summary>Holgura sobre el largo de la marca, por los espacios que admite la regex.</summary>
    private const int Ventana = 64;

    private readonly StringBuilder _texto = new();
    private int _emitido;

    /// <summary>Se encontró la marca: el resto de la respuesta no se muestra.</summary>
    public bool Cortado { get; private set; }

    /// <summary>Añade un fragmento del modelo y devuelve lo que se puede mostrar ya.</summary>
    public string Agregar(string fragmento)
    {
        if (Cortado || fragmento.Length == 0) return "";

        _texto.Append(fragmento);

        if (!retenerAmpliacion) return Emitir(_texto.Length);

        var texto = _texto.ToString();
        var marca = Salvaguardas.PosicionAmpliacion(texto);

        if (marca >= 0)
        {
            Cortado = true;
            return Emitir(texto[..marca].TrimEnd().Length);
        }

        return Emitir(texto.Length - Ventana);
    }

    /// <summary>Al terminar la respuesta: lo retenido, o el aviso estándar si hubo corte.</summary>
    public string Cerrar()
    {
        if (!Cortado) return Emitir(_texto.Length);

        return _emitido == 0
            ? Salvaguardas.SinCobertura
            : $"\n\n{Salvaguardas.SinCobertura}";
    }

    private string Emitir(int hasta)
    {
        if (hasta <= _emitido) return "";

        var salida = _texto.ToString(_emitido, hasta - _emitido);
        _emitido = hasta;
        return salida;
    }
}
