using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class RestablecerClave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Enlace para restablecer la contraseña que genera el administrador (RF-42).
            migrationBuilder.AddColumn<string>(
                name: "RestablecerClaveHash",
                table: "usuario",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RestablecerClaveVence",
                table: "usuario",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestablecerClaveHash",
                table: "usuario");

            migrationBuilder.DropColumn(
                name: "RestablecerClaveVence",
                table: "usuario");
        }
    }
}
