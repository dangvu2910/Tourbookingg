using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Tourbooking.Migration;

public static partial class HybridTransfer
{
    public static async Task BackupExpressAsync(string root, string directory)
    {
        var target = Path.GetFullPath(directory);
        var backupRoot = Path.GetFullPath(Path.Combine(root, "backups")) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Backup destination must be inside repository/backups.");
        Directory.CreateDirectory(target);
        await using var connection = new SqlConnection(NativeConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))";
        var serverPath = Path.Combine((string)(await command.ExecuteScalarAsync())!,
            "TourBookingDB-before-deploy-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".bak");
        command.CommandText = "BACKUP DATABASE [TourBookingDB] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        command.Parameters.AddWithValue("path", serverPath);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
        var copied = false;
        try
        {
            File.Copy(serverPath, Path.Combine(target, "TourBookingDB-before-deploy.bak"));
            copied = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // SQL's backup folder can intentionally be readable only by the SQL service.
            // Keep the verified backup there; do not weaken its ACLs or use OPENROWSET.
        }
        await File.WriteAllTextAsync(Path.Combine(target, "sql-backup-receipt.json"),
            JsonSerializer.Serialize(new { serverPath, copiedToRepository = copied, restoreVerifyOnly = true, completedAtUtc = DateTime.UtcNow }, Json));
        Console.WriteLine("SQL COPY_ONLY/CHECKSUM backup verified; its native path is recorded in sql-backup-receipt.json.");
    }
}
