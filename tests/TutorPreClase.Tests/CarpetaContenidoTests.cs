using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Infrastructure.Contenido;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

public class NombresCarpetaTests
{
    [Fact]
    public void El_curso_se_lee_como_codigo_y_nombre()
    {
        Assert.Equal(("MFEP", "Finanzas empresariales"), NombresCarpeta.Curso("MFEP - Finanzas empresariales"));
        Assert.Equal(("Contabilidad", "Contabilidad"), NombresCarpeta.Curso("Contabilidad"));
    }

    [Fact]
    public void La_clase_toma_el_orden_del_primer_numero_y_conserva_el_nombre_como_titulo()
    {
        Assert.Equal((4, "M4 - Capital de trabajo neto"), NombresCarpeta.Clase("M4 - Capital de trabajo neto", 1));
        Assert.Equal((3, "Repaso general"), NombresCarpeta.Clase("Repaso general", 3));
    }

    [Theory]
    [InlineData("Profe@UPC.edu.pe", "profe@upc.edu.pe")]
    [InlineData("MFEP - Finanzas empresariales", null)]
    [InlineData("@sin-usuario", null)]
    public void La_carpeta_del_profesor_es_su_correo(string carpeta, string? correo) =>
        Assert.Equal(correo, NombresCarpeta.Profesor(carpeta));

    [Theory]
    [InlineData("~$Anexo.xlsx", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("M1 Estados Financieros.pdf", false)]
    public void Se_ignoran_temporales_y_ocultos(string archivo, bool ignorable) =>
        Assert.Equal(ignorable, NombresCarpeta.EsIgnorable(archivo));
}

public class ExtractoresTests
{
    [Theory]
    [InlineData("Indicadores de Ges�ón", "Indicadores de Gestión")]
    [InlineData("reduce la u�lidad", "reduce la utilidad")]
    [InlineData("el poder del �empo", "el poder del tiempo")]
    [InlineData("gestión ﬁnanciera y ﬂujo", "gestión financiera y flujo")]
    public void El_PDF_recupera_ligaduras_y_el_ti_perdido(string extraido, string esperado) =>
        Assert.Equal(esperado, ExtractorPdf.Normalizar(extraido));

    [Fact]
    public void Un_caracter_perdido_que_no_precede_a_minuscula_no_se_inventa()
    {
        Assert.Equal("�Cómo?", ExtractorPdf.Normalizar("�Cómo?"));
    }

    [Fact]
    public void Cada_hoja_de_un_xlsx_es_una_pagina_con_sus_filas()
    {
        using var flujo = new MemoryStream();
        CrearLibro(flujo,
            ("Caso", [["Caso Tecnológica del Perú:"], ["Ventas netas", "1250.5"]]),
            ("Balance", [["Efectivo", "80"]]));
        flujo.Position = 0;

        var paginas = new ExtractorXlsx().Extraer(flujo);

        Assert.Equal(2, paginas.Count);
        Assert.Equal(1, paginas[0].Pagina);
        Assert.Contains("Hoja: Caso", paginas[0].Texto);
        Assert.Contains("Ventas netas | 1250.5", paginas[0].Texto);
        Assert.Contains("Efectivo | 80", paginas[1].Texto);
    }

    private static void CrearLibro(Stream destino, params (string Nombre, string[][] Filas)[] hojas)
    {
        using var libro = SpreadsheetDocument.Create(destino, SpreadsheetDocumentType.Workbook);
        var partes = libro.AddWorkbookPart();
        partes.Workbook = new Workbook(new Sheets());

        uint id = 1;
        foreach (var (nombre, filas) in hojas)
        {
            var hoja = partes.AddNewPart<WorksheetPart>();
            var datos = new SheetData();

            foreach (var fila in filas)
                datos.Append(new Row(fila.Select(v => double.TryParse(v,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
                    ? new Cell { CellValue = new CellValue(v), DataType = CellValues.Number }
                    : new Cell { InlineString = new InlineString(new Text(v)), DataType = CellValues.InlineString })));

            hoja.Worksheet = new Worksheet(datos);
            partes.Workbook.Sheets!.Append(new Sheet { Id = partes.GetIdOfPart(hoja), SheetId = id++, Name = nombre });
        }
    }
}

public sealed class AvisosRegistrados : IAvisosContenido
{
    public List<CambioContenido> Publicados { get; } = [];

    public void Publicar(CambioContenido cambio) => Publicados.Add(cambio);

    public IAsyncEnumerable<CambioContenido> EscucharAsync(CancellationToken ct) => AsyncEnumerable.Empty<CambioContenido>();
}

public sealed class ColaRegistrada : IColaExtraccion
{
    public List<Guid> Encolados { get; } = [];

    public ValueTask EncolarAsync(Guid archivoId, CancellationToken ct = default)
    {
        Encolados.Add(archivoId);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<Guid> LeerAsync(CancellationToken ct) => AsyncEnumerable.Empty<Guid>();
}

public sealed class SincronizadorCarpetaTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "carpeta-" + Guid.NewGuid().ToString("N"));
    private readonly string _clase;
    private readonly BancoDePruebas _banco = new();
    private readonly ColaRegistrada _cola = new();
    private readonly AvisosRegistrados _avisos = new();
    private readonly SincronizadorCarpeta _sincronizador;

    public SincronizadorCarpetaTests()
    {
        _clase = Path.Combine(_raiz, "Profe@UPC.edu.pe", "MFEP - Finanzas empresariales", "M1 - Estados financieros");
        Directory.CreateDirectory(_clase);

        var extractores = new ExtractorTextoFactory([new ExtractorTextoPlano(), new ExtractorPdf(), new ExtractorXlsx()]);
        var contenido = new ServicioContenido(
            _banco.Db, new AlmacenArchivosLocal(_raiz + "-almacen"), extractores,
            _banco.Contexto, new ColaCondensacionEnMemoria(), _banco.Reloj, NullLogger<ServicioContenido>.Instance);

        _sincronizador = new SincronizadorCarpeta(
            _banco.Db, contenido, _cola, extractores, _banco.Reloj, _avisos,
            Options.Create(new OpcionesCarpetaContenido { Ruta = Path.Combine(_raiz), Periodo = "2026-2" }),
            NullLogger<SincronizadorCarpeta>.Instance);
    }

    [Fact]
    public async Task Crea_curso_y_clase_desde_los_nombres_y_encola_solo_lo_admitido()
    {
        File.WriteAllText(Path.Combine(_clase, "Resumen.md"), "El balance muestra la situación a una fecha.");
        File.WriteAllText(Path.Combine(_clase, "portada.png"), "no es texto");
        File.WriteAllText(Path.Combine(_clase, "~$Resumen.md"), "temporal de Office");

        var resumen = await _sincronizador.SincronizarAsync();

        var curso = Assert.Single(_banco.Db.Cursos);
        Assert.Equal(("MFEP", "Finanzas empresariales", "2026-2"), (curso.Codigo, curso.Nombre, curso.Periodo));

        var clase = Assert.Single(_banco.Db.Clases);
        Assert.Equal(("M1 - Estados financieros", 1), (clase.Titulo, clase.Orden));

        // Mañana a las 19:00 de Lima (UTC-5), aunque el servidor corra en UTC.
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 0, 0, TimeSpan.FromHours(-5)), clase.Inicio);

        // Y su examen pre-clase, publicado y sin fecha límite, sin preguntas: las
        // genera la IA para cada alumno (RF-04).
        var examen = Assert.Single(_banco.Db.Examenes);
        Assert.True(examen.Publicado);
        Assert.Null(examen.CierraEn);
        Assert.Empty(_banco.Db.Preguntas);

        var archivo = Assert.Single(_banco.Db.Archivos);
        Assert.Equal("Resumen.md", archivo.Nombre);
        Assert.Equal([archivo.Id], _cola.Encolados);
        Assert.Equal(1, resumen.Nuevos);
    }

    [Fact]
    public async Task Sin_cambios_en_la_carpeta_no_se_vuelve_a_subir_ni_extraer()
    {
        File.WriteAllText(Path.Combine(_clase, "Resumen.md"), "Contenido");
        await _sincronizador.SincronizarAsync();

        var segunda = await _sincronizador.SincronizarAsync();

        Assert.False(segunda.HuboCambios);
        Assert.Single(_cola.Encolados);
        Assert.Single(_banco.Db.Clases);
    }

    [Fact]
    public async Task Un_archivo_modificado_reemplaza_al_anterior()
    {
        var ruta = Path.Combine(_clase, "Resumen.md");
        File.WriteAllText(ruta, "Versión 1");
        await _sincronizador.SincronizarAsync();

        File.WriteAllText(ruta, "Versión 2 corregida");
        var resumen = await _sincronizador.SincronizarAsync();

        Assert.Equal((1, 1), (resumen.Nuevos, resumen.Eliminados));
        Assert.Single(_banco.Db.Archivos);
        Assert.Equal(2, _cola.Encolados.Count);
    }

    [Fact]
    public async Task Lo_que_el_docente_quita_de_la_carpeta_desaparece_de_la_clase_pero_la_clase_queda()
    {
        var ruta = Path.Combine(_clase, "Resumen.md");
        File.WriteAllText(ruta, "Contenido");
        await _sincronizador.SincronizarAsync();

        File.Delete(ruta);
        var resumen = await _sincronizador.SincronizarAsync();

        Assert.Equal(1, resumen.Eliminados);
        Assert.Empty(_banco.Db.Archivos);
        Assert.Single(_banco.Db.Clases);
    }

    [Fact]
    public async Task Solo_se_avisa_a_la_web_cuando_el_docente_cambio_algo()
    {
        var ruta = Path.Combine(_clase, "Resumen.md");
        File.WriteAllText(ruta, "Versión 1");

        await _sincronizador.SincronizarAsync();
        var clase = _banco.Db.Clases.Single();
        Assert.Equal([clase.Id], Assert.Single(_avisos.Publicados).Clases);

        // Una pasada sin cambios no avisa: la web no se refresca por nada.
        await _sincronizador.SincronizarAsync();
        Assert.Single(_avisos.Publicados);

        File.WriteAllText(ruta, "Versión 2");
        await _sincronizador.SincronizarAsync();
        Assert.Equal(2, _avisos.Publicados.Count);
    }

    [Fact]
    public async Task Cada_curso_es_del_profesor_de_su_carpeta_y_llega_a_los_alumnos_que_lo_eligieron()
    {
        File.WriteAllText(Path.Combine(_clase, "Resumen.md"), "Contenido");
        await _sincronizador.SincronizarAsync();

        // El profesor queda registrado con su correo aunque nunca haya entrado.
        var profesor = _banco.Db.Usuarios.Single(u => u.Email == "profe@upc.edu.pe");
        Assert.Equal(RolUsuario.Docente, profesor.Rol);
        var curso = _banco.Db.Cursos.Single();
        Assert.Equal(profesor.Id, curso.DocenteId);
        Assert.Contains(_banco.Db.Matriculas, m => m.UsuarioId == profesor.Id && m.CursoId == curso.Id);

        // Un curso nuevo del profesor llega a sus alumnos; los de otro profesor no lo ven.
        var suyo = new Usuario { Email = "a@gmail.com", Nombre = "A", Rol = RolUsuario.Alumno, ProfesorId = profesor.Id };
        var ajeno = new Usuario { Email = "b@gmail.com", Nombre = "B", Rol = RolUsuario.Alumno, ProfesorId = Guid.NewGuid() };
        _banco.Db.Usuarios.AddRange(suyo, ajeno);
        _banco.Db.SaveChanges();

        Directory.CreateDirectory(Path.Combine(_raiz, "Profe@UPC.edu.pe", "MFEF - Finanzas II", "M1 - Repaso"));
        await _sincronizador.SincronizarAsync();

        var nuevo = _banco.Db.Cursos.Single(c => c.Codigo == "MFEF");
        Assert.Contains(_banco.Db.Matriculas, m => m.UsuarioId == suyo.Id && m.CursoId == nuevo.Id);
        Assert.DoesNotContain(_banco.Db.Matriculas, m => m.UsuarioId == ajeno.Id);
    }

    [Fact]
    public async Task Una_carpeta_de_primer_nivel_que_no_es_un_correo_se_omite()
    {
        Directory.CreateDirectory(Path.Combine(_raiz, "MFEP - Finanzas empresariales", "M1 - Estados financieros"));

        var resumen = await _sincronizador.SincronizarAsync();

        // Solo cuenta el curso de la carpeta del profesor.
        Assert.Equal(1, resumen.Cursos);
    }

    [Fact]
    public async Task Sin_carpeta_configurada_no_hace_nada()
    {
        var apagado = new SincronizadorCarpeta(
            _banco.Db, null!, _cola, new ExtractorTextoFactory([]), _banco.Reloj, _avisos,
            Options.Create(new OpcionesCarpetaContenido()), NullLogger<SincronizadorCarpeta>.Instance);

        var resumen = await apagado.SincronizarAsync();

        Assert.Equal(0, resumen.Cursos);
        Assert.Empty(_banco.Db.Cursos);
    }

    public void Dispose()
    {
        _banco.Dispose();
        foreach (var carpeta in new[] { _raiz, _raiz + "-almacen" })
        {
            // IOException cubre tambien la carpeta que no llego a crearse.
            try { Directory.Delete(carpeta, recursive: true); } catch (IOException) { }
        }
    }
}

public class AvisosContenidoTests
{
    [Fact]
    public async Task Cada_oyente_recibe_los_avisos_publicados_despues_de_conectarse()
    {
        var avisos = new AvisosContenidoEnMemoria();
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var oyente = avisos.EscucharAsync(fin.Token).GetAsyncEnumerator(fin.Token);
        var siguiente = oyente.MoveNextAsync();

        var cambio = new CambioContenido(Guid.NewGuid(), [Guid.NewGuid()]);
        avisos.Publicar(cambio);

        Assert.True(await siguiente);
        Assert.Equal(cambio, oyente.Current);
    }
}

public class NovedadesApiTests
{
    [Fact]
    public async Task El_alumno_recibe_por_sse_solo_los_cambios_de_sus_cursos()
    {
        using var api = new ApiDePruebas();
        var (_, alumnoId, cursoId, claseId) = api.Sembrar();
        var avisos = api.Services.GetRequiredService<IAvisosContenido>();

        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/api/v1/alumno/novedades");
        using var respuesta = await api.Como(alumnoId, "Alumno")
            .SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, fin.Token);

        Assert.Equal("text/event-stream", respuesta.Content.Headers.ContentType?.MediaType);

        using var lector = new StreamReader(await respuesta.Content.ReadAsStreamAsync(fin.Token));
        Assert.StartsWith(": conectado", await lector.ReadLineAsync(fin.Token));

        // Un curso ajeno no le llega; el suyo sí.
        avisos.Publicar(new CambioContenido(Guid.NewGuid(), [Guid.NewGuid()]));
        avisos.Publicar(new CambioContenido(cursoId, [claseId]));

        string? linea;
        do linea = await lector.ReadLineAsync(fin.Token);
        while (linea is not null && !linea.StartsWith("event:"));

        Assert.Equal("event: contenido", linea);
        var datos = await lector.ReadLineAsync(fin.Token);
        Assert.Contains(cursoId.ToString(), datos);
        Assert.Contains(claseId.ToString(), datos);
    }
}
