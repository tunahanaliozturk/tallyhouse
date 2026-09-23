using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tallyhouse.Infrastructure.Catalog.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_revision",
                columns: table => new
                {
                    singleton = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_revision", x => x.singleton);
                    table.CheckConstraint("ck_catalog_revision_singleton", "singleton");
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    write_key_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    read_key_hash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_projects", x => x.id);
                    table.CheckConstraint("ck_projects_name_length", "length(name) BETWEEN 1 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "event_schemas",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    spec = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_event_schemas", x => new { x.project_id, x.event_name, x.version });
                    table.CheckConstraint("ck_event_schemas_version", "version BETWEEN 1 AND 65535");
                    table.ForeignKey(
                        name: "fk_event_schemas_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "catalog_revision",
                columns: new[] { "singleton", "revision" },
                values: new object[] { true, 0L });

            migrationBuilder.CreateIndex(
                name: "ix_projects_read_key_hash",
                table: "projects",
                column: "read_key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_projects_write_key_hash",
                table: "projects",
                column: "write_key_hash",
                unique: true);

            // Written by hand. The revision is bumped by the database in the same transaction as any change
            // to the catalog, so there is no code path that can change it and forget to announce it.
            migrationBuilder.Sql("""
                CREATE FUNCTION bump_catalog_revision() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE catalog_revision SET revision = revision + 1;
                    RETURN NULL;
                END
                $$;

                CREATE TRIGGER projects_changed
                    AFTER INSERT OR UPDATE OR DELETE ON projects
                    FOR EACH STATEMENT EXECUTE FUNCTION bump_catalog_revision();

                CREATE TRIGGER event_schemas_changed
                    AFTER INSERT OR UPDATE OR DELETE ON event_schemas
                    FOR EACH STATEMENT EXECUTE FUNCTION bump_catalog_revision();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER event_schemas_changed ON event_schemas;
                DROP TRIGGER projects_changed ON projects;
                DROP FUNCTION bump_catalog_revision();
                """);

            migrationBuilder.DropTable(
                name: "catalog_revision");

            migrationBuilder.DropTable(
                name: "event_schemas");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
