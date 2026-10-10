using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class ExamenSinFechas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // El examen ya no tiene fechas propias: se rinde desde que su clase se habilita.
            migrationBuilder.DropColumn(name: "AbreEn", table: "examen");
            migrationBuilder.DropColumn(name: "CierraEn", table: "examen");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AbreEn",
                table: "examen",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CierraEn",
                table: "examen",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
