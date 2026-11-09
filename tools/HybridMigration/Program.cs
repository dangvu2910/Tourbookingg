using Tourbooking.Migration;

try
{
    if (args.Length >= 2 && args[0] is "native-copy" or "native-check" or "native-prune" or "enable-reservations" or "runtime-check" or "native-backup")
        HybridTransfer.ConfigureNative(args[1]);
    if (args is ["snapshot", var root, var output]) await HybridTransfer.SnapshotAsync(root, output);
    else if (args is ["apply", var source, var report]) await HybridTransfer.ApplyAsync(source, report);
    else if (args is ["storage", var outputPath]) await HybridTransfer.StorageAsync(outputPath);
    else if (args is ["verify", var snapshotDirectory, var verificationReport]) await HybridTransfer.VerifyAsync(snapshotDirectory, verificationReport);
    else if (args is ["native-copy", var repository, var backup]) await HybridTransfer.CopyToExpressAsync(repository, backup);
    else if (args is ["native-check", var repositoryPath, var targetReport]) await HybridTransfer.CheckExpressAsync(repositoryPath, targetReport);
    else if (args is ["native-backup", var backupRoot, var backupDirectory]) await HybridTransfer.BackupExpressAsync(backupRoot, backupDirectory);
    else if (args is ["native-prune", var pruneRoot, var pruneBackup]) await HybridTransfer.PruneExpressReadTablesAsync(pruneRoot, pruneBackup);
    else if (args is ["enable-reservations", var reservationRoot, var reservationBackup]) await HybridTransfer.EnableSeatReservationsAsync(reservationRoot, reservationBackup);
    else if (args is ["runtime-check", var runtimeRoot, var runtimeReport]) await HybridTransfer.VerifyRuntimeAsync(runtimeRoot, runtimeReport);
    else throw new ArgumentException("Use: snapshot <repository-root> <backup-directory> OR apply <snapshot-directory> <report-path>.");
}
catch (Exception ex)
{
    // Never emit connection strings, source rows, hashes of passwords, or raw DB exception text.
    Console.Error.WriteLine($"Migration stopped ({ex.GetType().Name}). {ex.Data["SafeMessage"] ?? "Sources and previous stack remain unchanged. Check the failing stage before continuing."}");
    if (ex is Microsoft.Data.SqlClient.SqlException sql)
    {
        var identifier = System.Text.RegularExpressions.Regex.Match(sql.Message, "(?:constraint|column|object) [\\\"']([A-Za-z0-9_.]+)[\\\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        Console.Error.WriteLine($"SQL error={sql.Number}, state={sql.State}, object={(identifier.Success ? identifier.Groups[1].Value : "not reported")}");
    }
    Environment.ExitCode = 1;
}
