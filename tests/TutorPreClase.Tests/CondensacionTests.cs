using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TutorPreClase.Application.Contenido;
using TutorPreClase.Application.Llm;
using TutorPreClase.Domain.Entidades;
using TutorPreClase.Tests.Infraestructura;

namespace TutorPreClase.Tests;

/// <summary>
/// La IA condensa el material una vez, página por página y con la cuenta del profesor, y
/// el tutor lee esos apuntes; el examen sigue saliendo del original (SDD §6.2).
/// </summary>
public class CondensacionTests
{
    private static readonly string Larga1 = string.Concat(Enumerable.Repeat(
        "El costo es una inversión que genera beneficios futuros y se registra en el balance como activo. ", 5));
    private static readonly string Larga2 = string.Concat(Enumerable.Repeat(
        "El gasto es un consumo del periodo que se resta del ingreso y reduce la utilidad del ejercicio. ", 5));
    private static readonly string Larga3 = string.Concat(Enumerable.Repeat(
        "El desembolso es la salida de efectivo al comprar un activo o pagar un pasivo de la empresa. ", 5));

    private sealed record Montaje(BancoDePruebas Banco, CondensadorMaterial Condensador, ArchivoContenido Archivo, Guid DocenteId);

    private static Montaje Montar(bool profesorConectado = true, int paginas = 3)
    {
        var banco = new BancoDePruebas().Sembrar();

        var docente = new Usuario { Email = "profe@upc.edu.pe", Nombre = "Profe", Rol = RolUsuario.Docente };
        banco.Db.Usuarios.Add(docente);
        banco.Db.Cursos.Single().DocenteId = docente.Id;

        var archivo = new ArchivoContenido
        {
            ClaseId = banco.ClaseId,
            Nombre = "M1.pdf",
            Tipo = "pdf",
            BlobUrl = "cursos/x/M1.pdf",
            HashSha256 = "m1",
            Estado = EstadoArchivo.Listo
        };

        string[] textos = [Larga1, Larga2, Larga3];
        for (var n = 1; n <= paginas; n++)
        {
            var texto = textos[(n - 1) % 3];
            archivo.Paginas.Add(new PaginaContenido
            {
                ArchivoId = archivo.Id,
                ClaseId = banco.ClaseId,
                Pagina = n,
                Texto = texto,
                Tokens = ContadorTokens.Estimar(texto)
            });
        }

        banco.Db.Archivos.Add(archivo);
        banco.Db.SaveChanges();

        if (profesorConectado)
            banco.Boveda.ConectarAsync(docente.Id, "openrouter", "sk-or-v1-del-profesor-9999").GetAwaiter().GetResult();

        var condensador = new CondensadorMaterial(
            banco.Db, banco.Boveda, banco.Proveedor, banco.Contexto, NullLogger<CondensadorMaterial>.Instance);

        return new Montaje(banco, condensador, archivo, docente.Id);
    }

    private static string Json(params (int Pagina, string Apuntes)[] paginas) =>
        JsonSerializer.Serialize(new { paginas = paginas.Select(p => new { pagina = p.Pagina, apuntes = p.Apuntes }) });

    [Fact]
    public async Task Sin_la_cuenta_del_profesor_no_llama_a_la_IA_y_el_tutor_lee_el_original()
    {
        var m = Montar(profesorConectado: false);
        using var _ = m.Banco;

        await m.Condensador.CondensarAsync(m.Archivo.Id);

        Assert.Empty(m.Banco.Proveedor.Solicitudes);
        Assert.Equal(EstadoCondensado.SinCuenta, m.Archivo.Condensado);
        Assert.All(m.Archivo.Paginas, p => Assert.Null(p.TextoCondensado));
    }

    [Fact]
    public async Task Condensa_pagina_por_pagina_y_el_tutor_lee_los_apuntes_con_sus_citas()
    {
        var m = Montar();
        using var _ = m.Banco;

        m.Banco.Proveedor.RespondeTexto(Json(
            (1, "- Costo: inversión con beneficio futuro; activo en el balance."),
            (2, "- Gasto: consumo del periodo; reduce la utilidad.")));

        await m.Condensador.CondensarAsync(m.Archivo.Id);

        var solicitud = m.Banco.Proveedor.Solicitudes.Single();
        Assert.Equal(NivelRazonamiento.Desactivado, solicitud.Razonamiento);
        Assert.Empty(solicitud.Herramientas);

        Assert.Equal(EstadoCondensado.Listo, m.Archivo.Condensado);
        var paginas = m.Archivo.Paginas.OrderBy(p => p.Pagina).ToList();
        Assert.NotNull(paginas[0].TextoCondensado);
        Assert.True(paginas[0].TokensCondensado < paginas[0].Tokens);
        Assert.Null(paginas[2].TextoCondensado); // la IA no la devolvió: se queda el original

        var tutor = await m.Banco.Contexto.ObtenerAsync(m.Banco.ClaseId);
        Assert.Contains("[M1.pdf, p. 1]\n- Costo: inversión con beneficio futuro", tutor.Texto);
        Assert.Contains("[M1.pdf, p. 3]\nEl desembolso es la salida", tutor.Texto);
        Assert.DoesNotContain("se registra en el balance como activo", tutor.Texto);

        var original = await m.Banco.Contexto.ObtenerOriginalAsync(m.Banco.ClaseId);
        Assert.Contains("se registra en el balance como activo", original.Texto);
        Assert.True(tutor.TokensTotales < original.TokensTotales);
    }

    [Fact]
    public async Task Un_apunte_mas_largo_que_el_original_se_descarta()
    {
        var m = Montar(paginas: 1);
        using var _ = m.Banco;

        m.Banco.Proveedor.RespondeTexto(Json((1, Larga1 + Larga1)));

        await m.Condensador.CondensarAsync(m.Archivo.Id);

        Assert.Null(m.Archivo.Paginas.Single().TextoCondensado);
    }

    [Fact]
    public async Task Si_el_profesor_prefiere_el_original_el_tutor_lo_lee()
    {
        var m = Montar(paginas: 1);
        using var _ = m.Banco;

        m.Banco.Proveedor.RespondeTexto(Json((1, "- Costo: inversión con beneficio futuro.")));
        await m.Condensador.CondensarAsync(m.Archivo.Id);

        m.Archivo.UsarOriginal = true;
        m.Banco.Db.SaveChanges();
        m.Banco.Contexto.Invalidar(m.Banco.ClaseId);

        var tutor = await m.Banco.Contexto.ObtenerAsync(m.Banco.ClaseId);
        Assert.Contains("se registra en el balance como activo", tutor.Texto);
    }

    [Fact]
    public async Task Con_el_modelo_saturado_queda_pendiente_para_reintentar_y_el_tutor_sigue_con_el_original()
    {
        var m = Montar();
        using var _ = m.Banco;

        m.Banco.Proveedor.Responde(new ErrorProveedor("429", true, LimiteDeUso: true, Saturado: true));

        var resultado = await m.Condensador.CondensarAsync(m.Archivo.Id);

        Assert.Equal(ResultadoCondensacion.Reintentar, resultado);
        Assert.Equal(EstadoCondensado.Pendiente, m.Archivo.Condensado);
        Assert.Contains("saturado", m.Archivo.ErrorCondensado);
        Assert.All(m.Archivo.Paginas, p => Assert.Null(p.TextoCondensado));
    }

    [Fact]
    public async Task Sin_cupo_propio_queda_en_error_con_el_motivo()
    {
        var m = Montar();
        using var _ = m.Banco;

        m.Banco.Proveedor.Responde(new ErrorProveedor("429", true, LimiteDeUso: true));

        var resultado = await m.Condensador.CondensarAsync(m.Archivo.Id);

        Assert.Equal(ResultadoCondensacion.Error, resultado);
        Assert.Equal(EstadoCondensado.Error, m.Archivo.Condensado);
        Assert.Contains("cupo", m.Archivo.ErrorCondensado);
    }

    [Fact]
    public async Task Agrupa_las_paginas_para_gastar_pocas_llamadas_del_cupo()
    {
        // ~120 tokens por página: 60 páginas son ~7200 tokens, tres lotes de hasta 3000.
        var m = Montar(paginas: 60);
        using var _ = m.Banco;

        await m.Condensador.CondensarAsync(m.Archivo.Id);

        Assert.Equal(3, m.Banco.Proveedor.Solicitudes.Count);
    }

    [Fact]
    public void Lee_el_json_aunque_venga_entre_bloques_de_codigo()
    {
        var apuntes = CondensadorMaterial.Interpretar(
            "Aquí tienes:\n```json\n{\"paginas\":[{\"pagina\":4,\"apuntes\":\"- ROE = utilidad / patrimonio\"}]}\n```");

        Assert.Equal("- ROE = utilidad / patrimonio", apuntes[4]);
        Assert.Empty(CondensadorMaterial.Interpretar("no es json"));
    }

    [Fact]
    public async Task El_examen_se_genera_con_el_texto_original()
    {
        var m = Montar(paginas: 1);
        using var _ = m.Banco;

        m.Banco.Proveedor.RespondeTexto(Json((1, "- Costo: inversión con beneficio futuro.")));
        await m.Condensador.CondensarAsync(m.Archivo.Id);

        var conversacion = await m.Banco.Tutor.AbrirConversacionAsync(m.Banco.ClaseId, m.Banco.AlumnoId, "openrouter");
        await m.Banco.Tutor.IniciarExamenAsync(conversacion.Id);

        Assert.Contains("se registra en el balance como activo", m.Banco.Generador.UltimoMaterial!.Texto);
    }
}

public class CondensacionApiTests
{
    [Fact]
    public async Task El_profesor_pide_condensar_y_elige_el_original_solo_en_sus_clases()
    {
        using var api = new ApiDePruebas();
        var (docenteId, _, _, claseId) = api.Sembrar();

        using (var db = api.NuevoContexto())
        {
            var otro = new Usuario { Email = "otro@upc.edu.pe", Nombre = "Otro", Rol = RolUsuario.Docente };
            db.Usuarios.Add(otro);

            var material = new ArchivoContenido
            {
                ClaseId = claseId, Nombre = "M1.pdf", Tipo = "pdf", BlobUrl = "x", HashSha256 = "m1", Estado = EstadoArchivo.Listo
            };
            material.Paginas.Add(new PaginaContenido
            {
                ArchivoId = material.Id, ClaseId = claseId, Pagina = 1, Texto = "El costo genera beneficios futuros.", Tokens = 9
            });
            db.Archivos.Add(material);
            db.SaveChanges();
        }

        var profesor = api.Como(docenteId, "Docente");

        var condensar = await profesor.PostAsJsonAsync($"/api/v1/clases/{claseId}/condensar", new { forzar = true });
        Assert.Equal(HttpStatusCode.Accepted, condensar.StatusCode);

        Guid archivoId;
        using (var db = api.NuevoContexto()) archivoId = db.Archivos.First(a => a.ClaseId == claseId).Id;

        var original = await profesor.PutAsJsonAsync($"/api/v1/contenido/{archivoId}/condensado", new { usarOriginal = true });
        Assert.True(original.IsSuccessStatusCode, await original.Content.ReadAsStringAsync());

        var contenido = await profesor.GetFromJsonAsync<JsonElement>($"/api/v1/clases/{claseId}/contenido");
        var archivo = contenido.GetProperty("archivos")[0];
        Assert.True(archivo.GetProperty("usarOriginal").GetBoolean());
        Assert.True(contenido.GetProperty("contextoClase").GetProperty("tokensOriginal").GetInt32() > 0);

        var paginas = await profesor.GetFromJsonAsync<JsonElement>($"/api/v1/contenido/{archivoId}/paginas");
        Assert.True(paginas.GetArrayLength() > 0);

        Guid otroId;
        using (var db = api.NuevoContexto()) otroId = db.Usuarios.First(u => u.Email == "otro@upc.edu.pe").Id;

        var ajeno = await api.Como(otroId, "Docente").PostAsJsonAsync($"/api/v1/clases/{claseId}/condensar", new { });
        Assert.Equal(HttpStatusCode.Forbidden, ajeno.StatusCode);
    }
}
