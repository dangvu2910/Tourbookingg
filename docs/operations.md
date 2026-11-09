# Vận hành

`scripts/Initialize-Environment.ps1` tạo các khóa/mật khẩu thiếu trong `.env`; không đổi mật khẩu tài khoản đã tồn tại. Không commit `.env` hoặc backup.

| File | Vai trò |
| --- | --- |
| `compose.hybrid.yaml` | SQL Server/MongoDB/RabbitMQ và ứng dụng |
| `compose.sqlexpress.yaml` | SQL Express trên Windows, giữ dữ liệu hiện có |
| `compose.admin.yaml` | Cổng quản trị DB/broker chỉ trên loopback |
| `compose.ha.yaml` | Hai replica, HAProxy public/private, bỏ public port trực tiếp của Kong |
| `compose.yaml` | SQLite legacy; không áp dụng HA |

Khởi động: `scripts/Start-Hybrid.ps1 -AdminPorts`. Với `SQL_DEPLOYMENT=sqlexpress`, script tự thêm override tương ứng. Website ở cổng 8080; backend chỉ mở trong mạng Docker. HAProxy cân bằng HTTP nội bộ qua cổng private 8101–8105, không publish chúng ra host.

## Trạng thái và dừng, giữ dữ liệu

```powershell
$files = @('-f','compose.hybrid.yaml','-f','compose.admin.yaml','-f','compose.sqlexpress.yaml','-f','compose.ha.yaml')
.\scripts\Compose.ps1 @files ps
.\scripts\Compose.ps1 @files logs --tail 50 booking notification kong edge
.\scripts\Compose.ps1 @files stop
```

Giữ nguyên tên project trong `.env`, volume, key ring và ảnh tour. Không dùng `down --volumes` trên stack đang dùng. Runtime không dùng SQL sa/Mongo admin.

## Backup và triển khai

Triển khai bình thường không chạy migration nhập dữ liệu, native-prune hoặc script xóa bảng.

```powershell
dotnet run --project tools/HybridMigration/HybridMigration.csproj -- native-backup . backups/pre-deploy-TIMESTAMP
```

Backup dùng Windows Authentication, SQL COPY_ONLY + CHECKSUM và RESTORE VERIFYONLY. Đường dẫn `.bak` được ghi trong `sql-backup-receipt.json`; nếu ACL của SQL cho phép, file cũng được sao chép dưới `backups`. Không nới ACL chỉ để sao chép. Mongo dùng `mongodump --archive --gzip`, xác minh bằng `mongorestore --dryRun`; backup cả key ring/ảnh tour và giữ manifest SHA256, config, image rollback. Sao chép ra thiết bị khác: volume cùng máy không thay thế backup.

`scripts/Deploy-HybridHA.ps1` dùng image đã build/test, tạo backup/rollback rồi cập nhật container, không xóa hoặc nhập lại database. Lần chuyển listener từ Kong sang HAProxy có gián đoạn ngắn. Rollout lỗi ứng dụng thì quay lại image/config trước; không tự restore dữ liệu đè lên database đang dùng.

## Kết nối và kiểm tra lỗi

- SSMS: `localhost\SQLEXPRESS`, Windows Authentication, `TourBookingDB`.
- Khi bật AdminPorts: Mongo `localhost:27018`, RabbitMQ management `localhost:15672`. Credentials ở `.env`.
- `/health/live`: process; `/health/ready`: dữ liệu cần để phục vụ. Notification readiness không phụ thuộc broker, consumer vẫn reconnect.
- `X-Service-Node`/`X-Edge-Upstream` đối chiếu replica; `X-Correlation-ID` nối request và sự kiện.
- Chỉ thử dừng database/broker trong stack test riêng theo [hướng dẫn kiểm chứng](verification.md).
