using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class CuentaConClave : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Cuenta con correo y contraseña, además de Google o Microsoft (RF-40).
            migrationBuilder.AddColumn<string>(
                name: "ClaveHash",
                table: "usuario",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CorreoVerificado",
                table: "usuario",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaveHash",
                table: "usuario");

            migrationBuilder.DropColumn(
                name: "CorreoVerificado",
                table: "usuario");
        }
    }
}
