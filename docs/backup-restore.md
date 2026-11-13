# Sao lưu và phục hồi (stack hybrid)

Áp dụng cho stack `compose.hybrid.yaml` (project trong `.env`, mặc định `tourbooking-live`). Backup chạy
**online**: không dừng service nào. Kết quả nằm trong `backups/` (đã gitignore) và chứa dữ liệu người dùng
cùng key ring đăng nhập: **không commit, không gửi công khai**.

## Sao lưu những gì

| Thành phần | Cách làm | File |
| --- | --- | --- |
| SQL Server: `IdentityDB`, `CatalogDB`, `BookingDB`, `CustomerDB` | `BACKUP DATABASE ... WITH COPY_ONLY, CHECKSUM` (+ `COMPRESSION` nếu edition hỗ trợ), rồi `RESTORE VERIFYONLY` | `<DB>.bak` |
| MongoDB: `ChatDB`, `NotificationDB` | `mongodump --gzip --archive` | `<DB>.archive.gz` |
| Volume `hybrid-keys` (key ring cookie) và `hybrid-images` (ảnh tour) | `tar czf` từ volume chỉ đọc | `files.tar.gz` |
| Thông tin kiểm chứng | Số dòng từng bảng, số document từng collection, commit Git, kích thước | `manifest.json`, `SHA256SUMS` |

RabbitMQ không cần backup: tin nhắn chỉ là tạm thời, outbox trong SQL là nguồn gốc và được gửi lại khi broker lên.
Mật khẩu không ra khỏi container: `sqlcmd`/`mongodump` đọc biến môi trường sẵn có trong container.

## Lệnh

```powershell
# Backup ngay (giữ 14 ngày, tự xóa bản hybrid-* cũ hơn)
.\scripts\Backup.ps1
.\scripts\Backup.ps1 -KeepDays 0                     # không xóa bản cũ

# Diễn tập khôi phục (an toàn): restore thành *_RestoreDrill, so số dòng với manifest, rồi xóa bản sao
.\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Drill

# Khôi phục thật (ghi đè dữ liệu live) - chỉ khi có sự cố
.\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Force
.\scripts\Restore.ps1 -BackupDirectory backups\hybrid-20261113-103721 -Force -Only sql -Database BookingDB
```

`-Force` lần lượt: kiểm tra SHA256 → **tự backup trạng thái hiện tại** vào `backups\pre-restore-<ts>` (bỏ qua bằng
`-SkipSafetyBackup`) → dừng gateway/web-router/các service → restore SQL `WITH REPLACE` và map lại user `*_app`
với login → `mongorestore --drop` → giải nén key/ảnh vào volume → bật lại service và chờ healthy. Dữ liệu ghi sau
thời điểm backup sẽ mất khỏi live (vẫn còn trong bản pre-restore).

## Tự động hằng ngày (Windows Task Scheduler)

```powershell
.\scripts\Register-BackupTask.ps1                    # 02:00 mỗi ngày, giữ 14 ngày
.\scripts\Register-BackupTask.ps1 -At 21:30 -KeepDays 7 -RunNow
Get-ScheduledTaskInfo -TaskName 'Tourbooking Hybrid Backup'   # LastRunTime / LastTaskResult (0 = OK)
.\scripts\Register-BackupTask.ps1 -Unregister
```

Task chạy bằng user hiện tại khi đang đăng nhập (Docker Desktop cần phiên của bạn); máy tắt/ngủ thì chạy bù ngay
khi có thể. Log mỗi lần chạy: `backups\logs\backup-<ts>.log`.

## Quy tắc 3-2-1 và lên cloud

- Giữ ít nhất một bản ở **thiết bị khác** (ổ ngoài, OneDrive/Google Drive riêng tư). Backup cùng ổ với Docker không bảo vệ khi hỏng máy.
- Định kỳ chạy `-Drill`: backup chưa khôi phục thử thì chưa chắc dùng được.

## Kết quả kiểm chứng (13/11/2026)

- `Backup.ps1` trên live: 4 DB SQL (85/92/169/76 dòng), ChatDB (37 document), NotificationDB (163), 32 file key/ảnh; 9,3 MB, ~12 giây, service vẫn chạy.
- `Restore.ps1 -Drill` trên live: cả 6 database khớp số dòng/document; bản `_RestoreDrill` đã xóa.
- `Restore.ps1 -Force` trên project cô lập `tourbooking-restoretest` (SQL/Mongo mới, chỉ chạy provision): restore đủ, user `*_app` map đúng login, volume có 1 key + 31 ảnh; project test đã xóa sau đó.

Các bản sao lưu SQLite đã xoá ngày 13/11/2026 sau khi đối chiếu: user/role/tour/booking/payment/review khớp với SQL Server (7 user-role mồ côi nằm ở `identity_read.LegacyOrphanRows`), 6 liên hệ + 11 phản hồi khớp với `ChatDB.conversations`, 129 thông báo nằm trong `NotificationDB.notifications`.
