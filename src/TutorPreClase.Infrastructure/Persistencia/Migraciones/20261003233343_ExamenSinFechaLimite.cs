using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ExamenSinFechaLimite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CierraEn",
                table: "examen",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // Los exámenes que ya existen cerraban al empezar su clase: quedan sin fecha límite.
            // La fecha de la clase no cambia.
            migrationBuilder.Sql("UPDATE examen SET \"CierraEn\" = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "CierraEn",
                table: "examen",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
