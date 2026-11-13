# Vận hành

Các phần bổ sung local: [metrics, load test, backup/restore và lab replication](local-distributed.md).

Monitoring tùy chọn: xem [Prometheus/Grafana và runbook](monitoring.md). Overlay
`compose.monitoring.yaml` chỉ bổ sung monitoring cho hybrid; không tự bật trên live.

`scripts/Initialize-Environment.ps1` tạo các khóa/mật khẩu thiếu trong `.env`; không đổi mật khẩu tài khoản đã tồn tại. Không commit `.env` hoặc backup.

| File | Vai trò |
| --- | --- |
| `compose.hybrid.yaml` | SQL Server/MongoDB/RabbitMQ và ứng dụng; một container mỗi service, một network |
| `compose.admin.yaml` | Cổng quản trị DB/broker chỉ trên loopback (SQL `14333`, Mongo `27018`, RabbitMQ `15672`) |
| `compose.monitoring.yaml` | Prometheus/blackbox/Grafana tùy chọn |

Stack SQLite cũ (`compose.yaml`, file `services/*/data/*.db`, công cụ chuyển SQLite) đã bỏ ngày 13/11/2026. Ngày 13/11/2026 đã đối chiếu từng bảng SQLite với SQL Server/MongoDB (đủ dữ liệu) rồi xoá các bản lưu SQLite.

Không có load balancer/edge riêng (HAProxy/Nginx đã bỏ); mỗi service một container, Kong là điểm vào duy nhất.

Khởi động: `scripts/Start-Hybrid.ps1 -AdminPorts` (thêm `-Monitoring` nếu cần). Kong là điểm vào duy nhất ở cổng 8080 (`HTTP_PORT`); backend chỉ mở trong mạng Docker và gọi nhau bằng `http://<service>:8080`.

## Trạng thái và dừng, giữ dữ liệu

```powershell
$files = @('-f','compose.hybrid.yaml','-f','compose.admin.yaml')
.\scripts\Compose.ps1 @files ps
.\scripts\Compose.ps1 @files logs --tail 50 booking notification gateway web-router
.\scripts\Compose.ps1 @files stop
```

Dùng đúng bộ file đã khởi động. Giữ nguyên tên project trong `.env`, volume, key ring và ảnh tour. Không dùng `down --volumes` trên stack đang dùng. Runtime không dùng SQL sa/Mongo admin.

## Backup và triển khai

Triển khai bình thường không chạy migration nhập dữ liệu, native-prune hoặc script xóa bảng.

```powershell
.\scripts\Backup.ps1                                              # backup online -> backups\hybrid-<timestamp>
.\scripts\Restore.ps1 -BackupDirectory backups\hybrid-<ts> -Drill # diễn tập khôi phục, không đụng live
```

Chi tiết, lịch tự động và khôi phục thật: [Sao lưu và phục hồi](backup-restore.md).
Sao chép backup ra thiết bị khác: volume/ổ cùng máy không thay thế backup.

Cập nhật ứng dụng local: backup như trên, build/test image, rồi `.\scripts\Compose.ps1 @files up -d --no-deps <service>`; không xóa hoặc nhập lại database. Rollout lỗi thì quay lại image/config trước; không tự restore dữ liệu đè lên database đang dùng.

## Kết nối và kiểm tra lỗi

- SSMS (cần `compose.admin.yaml`): `127.0.0.1,14333`, SQL Server Authentication, tick *Trust server certificate*.
- Khi bật AdminPorts: Mongo `localhost:27018` (Compass), RabbitMQ management `localhost:15672`. Credentials ở `.env`. Cổng SQL/Mongo không mở bằng trình duyệt được.
- `/health/live`: process; `/health/ready`: dữ liệu cần để phục vụ. Notification readiness không phụ thuộc broker, consumer vẫn reconnect.
- `X-Service-Node` cho biết container trả lời; `X-Correlation-ID` nối request và sự kiện.
- Chỉ thử dừng database/broker trong stack test riêng theo [hướng dẫn kiểm chứng](verification.md).
