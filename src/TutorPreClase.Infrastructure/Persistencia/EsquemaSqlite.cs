using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace TutorPreClase.Infrastructure.Persistencia;

/// <summary>
/// La base local (SQLite) se crea con EnsureCreated, que no toca una base que ya existe.
/// Cuando una columna pasa a opcional, aquí se le quita el NOT NULL sin borrar los datos:
/// SQLite no tiene ALTER COLUMN, pero admite corregir la definición guardada de la tabla
/// para cambios que no alteran cómo se almacenan las filas (sqlite.org/lang_altertable.html).
/// En producción (PostgreSQL) esto lo hacen las migraciones.
/// </summary>
public static class EsquemaSqlite
{
    /// <summary>Devuelve true si la columna era obligatoria y dejó de serlo.</summary>
    public static async Task<bool> HacerOpcionalAsync(
        DbContext db, string tabla, string columna, CancellationToken ct = default)
    {
        var conexion = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        try
        {
            await using var leer = conexion.CreateCommand();
            leer.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $tabla";
            AgregarParametro(leer, "$tabla", tabla);

            if (await leer.ExecuteScalarAsync(ct) is not string definicion) return false;

            var corregida = Regex.Replace(
                definicion, $"(\"{Regex.Escape(columna)}\" \\w+) NOT NULL", "$1 NULL");
            if (corregida == definicion) return false;

            await using var version = conexion.CreateCommand();
            version.CommandText = "PRAGMA schema_version";
            var actual = Convert.ToInt64(await version.ExecuteScalarAsync(ct));

            await using var escribir = conexion.CreateCommand();
            escribir.CommandText =
                "PRAGMA writable_schema = ON; " +
                "UPDATE sqlite_master SET sql = $sql WHERE type = 'table' AND name = $tabla; " +
                $"PRAGMA schema_version = {actual + 1}; " +
                "PRAGMA writable_schema = OFF;";
            AgregarParametro(escribir, "$sql", corregida);
            AgregarParametro(escribir, "$tabla", tabla);
            await escribir.ExecuteNonQueryAsync(ct);

            return true;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Agrega una columna nueva a una tabla que ya existía. True si la agregó.</summary>
    public static async Task<bool> AgregarColumnaAsync(
        DbContext db, string tabla, string columna, string definicion, CancellationToken ct = default)
    {
        var conexion = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);

        try
        {
            await using var leer = conexion.CreateCommand();
            leer.CommandText = "SELECT COUNT(*) FROM pragma_table_info($tabla) WHERE name = $columna";
            AgregarParametro(leer, "$tabla", tabla);
            AgregarParametro(leer, "$columna", columna);

            if (Convert.ToInt64(await leer.ExecuteScalarAsync(ct)) > 0) return false;

            // Los nombres vienen del código, no de fuera: no hay nada que escapar.
            await using var agregar = conexion.CreateCommand();
            agregar.CommandText = $"ALTER TABLE \"{tabla}\" ADD COLUMN \"{columna}\" {definicion}";
            await agregar.ExecuteNonQueryAsync(ct);

            return true;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void AgregarParametro(System.Data.Common.DbCommand comando, string nombre, object valor)
    {
        var parametro = comando.CreateParameter();
        parametro.ParameterName = nombre;
        parametro.Value = valor;
        comando.Parameters.Add(parametro);
    }
}
