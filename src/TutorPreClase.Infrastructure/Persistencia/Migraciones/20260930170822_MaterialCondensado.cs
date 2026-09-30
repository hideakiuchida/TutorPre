using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class MaterialCondensado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextoCondensado",
                table: "pagina_contenido",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TokensCondensado",
                table: "pagina_contenido",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Condensado",
                table: "archivo_contenido",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // El material que ya existía también se condensa al arrancar.
                defaultValue: "Pendiente");

            migrationBuilder.AddColumn<string>(
                name: "ErrorCondensado",
                table: "archivo_contenido",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UsarOriginal",
                table: "archivo_contenido",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextoCondensado",
                table: "pagina_contenido");

            migrationBuilder.DropColumn(
                name: "TokensCondensado",
                table: "pagina_contenido");

            migrationBuilder.DropColumn(
                name: "Condensado",
                table: "archivo_contenido");

            migrationBuilder.DropColumn(
                name: "ErrorCondensado",
                table: "archivo_contenido");

            migrationBuilder.DropColumn(
                name: "UsarOriginal",
                table: "archivo_contenido");
        }
    }
}
