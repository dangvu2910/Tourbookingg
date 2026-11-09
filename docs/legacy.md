# Stack và công cụ legacy

`compose.yaml` giữ nguyên SQLite cũ. Mỗi backend có file riêng dưới `services/<backend>/data`; không share một file cho nhiều replica. Dữ liệu SQLite cũ được giữ nguyên khi chạy SQL/Mongo hiện tại.

```powershell
.\scripts\Initialize-Environment.ps1
.\scripts\Compose.ps1 -f compose.yaml up -d --build
```

Không chạy legacy và hybrid cùng cổng 8080; dùng đúng project/volume của stack cần quản lý. `Backup.ps1`/`Restore.ps1` cũ dành cho legacy, không thay thế backup SQL Express/Mongo hiện tại.

`Migrate-SqliteToHybrid`, `Switch-ToSqlExpress` và `tools/HybridMigration` phục vụ migration có kiểm chứng. `Trim-SqlReadTables`, `Archive-LegacySqlTables`, `Remove-UnusedSqlTables`, native-copy/native-prune có thể đổi/xóa schema hoặc dữ liệu; không nằm trong quy trình chạy/deploy bình thường. Chỉ dùng trong bảo trì có backup và mục tiêu rõ ràng.

Các báo cáo kiểm thử/thuyết trình cũ đã được gộp để tránh nhầm số liệu/cấu hình hết hiệu lực. Bản gốc trước khi gộp ở `backups/docs-before-consolidation-*`; hình trong `docs/images` được giữ nguyên.
