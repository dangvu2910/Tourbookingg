using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tourbooking.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContactReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContactInquiryReplies",
                columns: table => new
                {
                    ContactInquiryReplyId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContactInquiryId = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsFromAdmin = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactInquiryReplies", x => x.ContactInquiryReplyId);
                    table.ForeignKey(
                        name: "FK_ContactInquiryReplies_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ContactInquiryReplies_ContactInquiries_ContactInquiryId",
                        column: x => x.ContactInquiryId,
                        principalTable: "ContactInquiries",
                        principalColumn: "ContactInquiryId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactInquiryReplies_ContactInquiryId",
                table: "ContactInquiryReplies",
                column: "ContactInquiryId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactInquiryReplies_UserId",
                table: "ContactInquiryReplies",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactInquiryReplies");
        }
    }
}
