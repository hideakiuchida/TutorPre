using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TutorPreClase.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class TokenMcp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "token_mcp",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ultimos4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    CreadoEn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UltimoUsoEn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocadoEn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_token_mcp", x => x.Id);
                    table.ForeignKey(
                        name: "FK_token_mcp_usuario_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "usuario",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_token_mcp_Hash",
                table: "token_mcp",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_token_mcp_UsuarioId",
                table: "token_mcp",
                column: "UsuarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "token_mcp");
        }
    }
}
