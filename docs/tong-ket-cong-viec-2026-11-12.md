# Tổng kết công việc đã thực hiện cho Tourbooking

> Ghi chú 13/11/2026: HAProxy/Nginx, overlay HA (`compose.ha.yaml`, `compose.single.yaml`, `compose.nginx*.yaml`), TLS local và `Deploy-HybridHA`/`Test-Proxy` đã được gỡ; local chạy một container mỗi service, Kong là điểm vào duy nhất, cân bằng tải/replica chuyển sang AWS (ALB → Kong). Kết quả bên dưới là lịch sử của cấu hình cũ; chạy lại bằng `scripts/Test-Hybrid.ps1`.

Ngày tổng kết: **12/11/2026**.

Tài liệu này ghi rõ phần đã triển khai, tác dụng, file liên quan, kết quả kiểm thử
và phần chưa làm. Kết quả bên dưới lấy từ lần kiểm chứng đã ghi trong
[verification.md](verification.md); không phải kết quả chạy lại tại thời điểm viết
bản tổng kết này.

## 1. Phạm vi thực hiện

- Đọc và tuân thủ `AI-rules/AGENTS.md`, `AI-rules/api-guidelines.md` và quy định AWS.
- Hoàn thiện cấu hình và mã phục vụ môi trường phân tán local bằng Docker Compose.
- Dùng môi trường test riêng để thử ghi dữ liệu, dừng service, replication và restore.
- Giữ nguyên stack SQLite legacy trong `compose.yaml`, dữ liệu live và route MVC/API.
- Chọn hướng cloud tương lai là **AWS ECS trên EC2**, không dùng Docker Swarm.
- Theo quyết định của người dùng: **giữ SQL Express local; làm SQL HA bằng RDS SQL
  Server khi triển khai AWS**.

Các phần dưới đây là thay đổi của đợt hoàn thiện local và tài liệu chuẩn bị AWS.
Không đồng nghĩa toàn bộ các tính năng nghiệp vụ có sẵn của project được viết mới.

## 2. Scale ngang tầng ứng dụng và load balancing

### Đã làm gì

- Tái sử dụng cấu hình hai replica trong `compose.ha.yaml` cho Identity, Catalog,
  Booking, Customer, Notification, Gateway và Kong.
- Thêm overlay Nginx để có thể chọn Nginx làm entry point của stack hybrid.
- Giữ Kong/YARP để bảo toàn routing và hành vi MVC/API hiện có.
- Cấu hình Nginx phân giải lại Docker DNS khi container đổi IP.
- Giữ các listener nội bộ 8101–8105 để client HTTP giữa các service tiếp tục hoạt động.
- Thêm readiness subrequest cho listener nội bộ để trả lỗi hữu hạn khi dependency mất.
- Tách truy vấn readiness khỏi lượt chọn upstream của request nghiệp vụ.
- Tắt retry tại Nginx để tránh tự gửi lại booking/payment khi kết quả chưa xác định.
- Bổ sung tùy chọn `-Nginx`, `-Monitoring` vào script khởi động local; giữ chế độ
  single-instance/HA đang có.

### File chính

- [compose.nginx.yaml](../compose.nginx.yaml).
- [infrastructure/nginx/nginx.conf](../infrastructure/nginx/nginx.conf).
- [scripts/Start-Hybrid.ps1](../scripts/Start-Hybrid.ps1).
- [scripts/Test-Proxy.ps1](../scripts/Test-Proxy.ps1).

### Đã kiểm chứng

- Request thực sự đi qua hai replica của các backend/Gateway và Kong trong ma trận test.
- MVC Tours thực nhận request qua hai Catalog replica.
- Ma trận **18 tình huống** đạt: mất replica, mất toàn bộ service, SQL/Mongo mất,
  RabbitMQ mất và phục hồi; các chức năng không phụ thuộc tiếp tục phục vụ.

### Đây đã là scale chưa

**Đã có scale ngang ứng dụng bằng nhiều container replica trên một Docker host.**
Chưa tự tăng/giảm replica theo tải; chưa tự thêm/bớt server; chưa phân bố workload
trên nhiều server/AZ. Mongo/Rabbit replication là dự phòng dữ liệu/broker, không tự
chứng minh tăng thông lượng xử lý.

## 3. MongoDB replica set

### Đã làm gì

- Thêm lab MongoDB ba member chứa dữ liệu, mỗi member có volume riêng.
- Bật authentication và key file cho replica set.
- Thêm bootstrap replica set và provisioning user ứng dụng theo database sở hữu.
- Thêm profile kết nối Customer/Notification test vào replica set bằng nhiều seed,
  `replicaSet` và majority write concern.
- Sửa healthcheck để chờ listener mạng thật, tránh server localhost tạm trong lúc init.

### File chính

- [compose.replication-test.yaml](../compose.replication-test.yaml).
- [compose.cluster-apps-test.yaml](../compose.cluster-apps-test.yaml).
- [infrastructure/mongo/replica-init.js](../infrastructure/mongo/replica-init.js).
- [infrastructure/mongo/app-users.js](../infrastructure/mongo/app-users.js).
- [scripts/Test-Replication.ps1](../scripts/Test-Replication.ps1).

### Đã kiểm chứng

- Bầu primary mới sau khi dừng primary cũ.
- Bản ghi được xác nhận bằng majority vẫn còn sau election.
- Ứng dụng test đọc chat/notification trở lại sau primary loss.
- Suite tích hợp nghiệp vụ chạy trên cấu hình Mongo replica set.

Replica set này dùng dữ liệu test; chưa chuyển database Mongo live sang topology mới.

## 4. RabbitMQ cluster và quorum queues

### Đã làm gì

- Thêm cluster test ba broker, mỗi broker có hostname và volume riêng.
- Cấu hình default quorum queue và exporter Prometheus.
- Bổ sung kết nối nhiều broker endpoint cho dispatcher/consumer; giữ fallback một
  host để tương thích cấu hình cũ.
- Thêm job đợi cluster đủ ba node và bổ sung member cho quorum queue test đã tồn tại.
- Kiểm tra riêng cả ba queue nghiệp vụ đều có ba member; không chỉ đếm số broker.
- Sửa quy trình dừng A/B/C và khởi động C/B/A có chờ readiness để phục hồi metadata.
- Giữ publisher confirms, ACK, outbox và cơ chế chống xử lý trùng hiện có.

### File chính

- [Shared/Messaging/RabbitTopology.cs](../Shared/Messaging/RabbitTopology.cs).
- [Shared/Persistence/OutboxDispatcher.cs](../Shared/Persistence/OutboxDispatcher.cs).
- [NotificationConsumer.cs](../services/Notification/Modules/Notifications/Infrastructure/Messaging/NotificationConsumer.cs).
- [ChatOutboxDispatcher.cs](../services/Customer/Modules/Support/Infrastructure/Chat/ChatOutboxDispatcher.cs).
- [infrastructure/rabbitmq/cluster.conf](../infrastructure/rabbitmq/cluster.conf).
- [infrastructure/rabbitmq/cluster-ready.sh](../infrastructure/rabbitmq/cluster-ready.sh).
- [tests/cluster-resilience.mjs](../tests/cluster-resilience.mjs).

### Đã kiểm chứng và lỗi đã sửa

- Lab giữ được durable message sau mất broker A và đọc đúng payload qua broker B.
- Bài nghiệp vụ **support reply → notification** đạt khi broker A đang dừng.
- Phát hiện queue có thể chỉ có hai member dù cluster đã có ba broker; đã thêm
  readiness/membership check và grow member cho queue test.
- Có một lần dùng `force_boot` trên volume test để chẩn đoán sự cố shutdown đồng
  thời. Script chuẩn không tự force_boot; quy trình cuối đã chạy lại thành công
  không cần thao tác đó. Không áp dụng khôi phục này lên broker live.

## 5. Monitoring, metrics và cảnh báo

### Đã làm gì

- Thêm overlay Prometheus, blackbox exporter và Grafana.
- Provision datasource/dashboard từ source để có thể tái tạo.
- Thêm HTTP metrics dùng chung, bật theo cấu hình cho app/Gateway/Notification.
- Thu readiness/liveness, số replica khám phá, request rate, HTTP status rate,
  latency p50/p95/p99 và process CPU/working set.
- Thêm worker chỉ đọc SQL outbox thuộc service để đo backlog.
- Dùng `max` giữa replica khi xem cùng một backlog, tránh cộng trùng.
- Khi đọc SQL lỗi, báo observation=0 và backlog `NaN`, không giả thành backlog bằng 0.
- Thu RabbitMQ exporter/queue metrics trong profile cluster.
- Thêm **12 alert rules**, gồm lỗi health/discovery/scrape, latency/error cao,
  outbox backlog/observation lỗi và RabbitMQ metrics/backlog.
- Chặn metrics ở public edge, gồm đường dẫn chữ hoa và trailing slash.

### File chính

- [compose.monitoring.yaml](../compose.monitoring.yaml).
- [Shared/Routing/ServiceMetrics.cs](../Shared/Routing/ServiceMetrics.cs).
- [Shared/Hosting/OutboxMetrics.cs](../Shared/Hosting/OutboxMetrics.cs).
- [infrastructure/monitoring](../infrastructure/monitoring).
- [scripts/Test-Monitoring.ps1](../scripts/Test-Monitoring.ps1).
- [scripts/Test-MonitoringAlerts.ps1](../scripts/Test-MonitoringAlerts.ps1).
- [scripts/Test-OutboxMetrics.ps1](../scripts/Test-OutboxMetrics.ps1).

### Đã kiểm chứng

- **12 app metrics targets**, **8 SQL outbox observers**, **3 RabbitMQ exporters**.
- Hai dashboard được provision: replica health và HTTP/process/outbox/queue.
- Cảnh báo exporter-down thực sự firing rồi resolved sau phục hồi.
- Rule tests đạt, gồm thời gian chờ và chống cộng trùng backlog giữa replica.
- Dừng SQL test: tám observers báo lỗi/backlog NaN, sau đó phục hồi đúng.

Chưa có đầy đủ host disk/restart, database connection/failover metrics hoặc kênh
gửi alert ra ngoài. Cảnh báo hiện xem tại Prometheus/Grafana.

## 6. HTTPS localhost và correlation ID

### Đã làm gì

- Thêm profile HTTPS localhost cho Nginx.
- Tạo certificate tự ký cho test, private key nằm trong artifacts đã gitignore.
- Không thay trust store hệ điều hành, không dùng certificate test cho production.
- Nginx tạo correlation ID khi thiếu/sai, truyền qua proxy và ghi structured log.
- Giữ correlation ID trong response header và JSON metadata; tránh header lặp.

### File chính và kết quả

- [compose.nginx-tls.yaml](../compose.nginx-tls.yaml).
- [infrastructure/nginx/tls.conf](../infrastructure/nginx/tls.conf).
- [scripts/Initialize-LocalTls.ps1](../scripts/Initialize-LocalTls.ps1).
- `Test-Proxy.ps1` đạt: HTTPS trả 200 với certificate validation và correlation ID khớp.

Đây là TLS tại entry point local. Chưa chứng minh TLS giữa database/broker node,
chưa có certificate public/renewal hoặc cấu hình HTTPS-only production.

## 7. Backup và restore thật

### SQL Server test

- Backup CatalogDB bằng COPY_ONLY/CHECKSUM.
- Chạy RESTORE VERIFYONLY.
- Restore thật sang database có tên UUID mới, không ghi đè source.
- Chạy CHECKDB và đối chiếu số dòng bảng với source khi dữ liệu test ổn định.
- File: [Test-BackupRestore.ps1](../scripts/Test-BackupRestore.ps1),
  [restore-verification.sql](../tests/restore-verification.sql).

### MongoDB lab

- Mongodump dữ liệu kiểm chứng, tạo archive và checksum.
- Mongorestore vào namespace UUID riêng và kiểm tra marker sau restore.
- File: [verify-restore.sh](../infrastructure/mongo/verify-restore.sh), `Test-Replication.ps1`.

Các kiểm chứng này dùng database test/lab; chưa phải diễn tập khôi phục toàn bộ
hệ thống live sang máy độc lập, chưa chứng minh backup off-site theo lịch.

## 8. Kiểm thử và kết quả đo

| Kiểm tra | Kết quả đã ghi nhận |
| --- | --- |
| Build image app/test | Thành công |
| Suite tích hợp trên cấu hình cluster | **114 pass, 0 fail, 0 skip** |
| Support notification khi broker A dừng | **1/1 pass** |
| Ma trận lỗi app/dependency qua Nginx | **18 tình huống đạt** |
| MVC round-robin và HTTPS/correlation | Đạt |
| Prometheus config/rules và rule tests | Đạt |
| Exporter-down alert firing/recovery | Đạt |
| SQL-outage metrics/recovery | Đạt |
| SQL restore/CHECKDB/row counts | Đạt |
| Mongo election/majority marker/restore | Đạt |
| Rabbit quorum message sau mất broker | Đạt |

Load test catalog chỉ đọc, chạy riêng sau build/test:

| Chỉ số | Giá trị |
| --- | --- |
| Client đồng thời | 4 |
| Thời gian | Khoảng 20 giây |
| Tổng request | 6.698 |
| Throughput | 334,76 request/giây |
| p50 | 10,07 ms |
| p95 | 22,80 ms |
| p99 | 35,87 ms |
| Lỗi | 0 |
| Catalog replica thực nhận request | 2 |

Số liệu này không đại diện throughput ghi booking/payment, production hoặc AWS
autoscaling. Lần đo bị chồng build/test đã được giữ riêng và không dùng làm baseline.

Đã sửa test admin payment để chờ Saga giữ chỗ hoàn tất rồi lấy form/version mới,
gửi lệnh xác nhận một lần; không bỏ assertion hoặc retry mù lệnh payment.

## 9. Script vận hành và tài liệu đã bổ sung

- Mở rộng `Test-HybridHA.ps1` với `-Nginx`, `-Monitoring`, `-Cluster`, `-Tls`.
- Sửa dependency job tạo database test và chờ discovery trước lệnh ghi đầu tiên.
- Bổ sung `.gitattributes` để shell helpers giữ LF khi checkout trên Windows.
- Bổ sung `AI-rules/aws-guidelines.md` và liên kết từ `AGENTS.md` cho công việc AWS.
- Tạo [hướng dẫn chạy local](local-distributed.md), [monitoring runbook](monitoring.md),
  [kế hoạch AWS](aws-autoscaling-nginx-replication-plan.md) và
  [hướng dẫn triển khai AWS](aws-deployment-guide.md).
- Cập nhật `docs/operations.md` và `docs/verification.md`.

Các cơ chế nghiệp vụ đã có như auth, CSRF, idempotency, optimistic concurrency,
SQL locking, Saga và outbox được giữ và kiểm tra hồi quy; không viết lại toàn bộ.

## 10. Môi trường và bằng chứng

Địa chỉ của môi trường test đã được giữ chạy sau lần nghiệm thu:

| Thành phần | Địa chỉ |
| --- | --- |
| App test HTTP | http://localhost:18083 |
| App test HTTPS | https://localhost:18443 |
| Grafana test | http://localhost:13000 |
| Prometheus test | http://localhost:19090 |
| Stack live được giữ nguyên | http://localhost:8080 |

Đây là địa chỉ cấu hình, không bảo đảm container vẫn đang chạy khi đọc tài liệu
vào thời điểm khác. Credential test nằm trong file env đã gitignore; không ghi
mật khẩu vào tài liệu này.

| Thư mục bằng chứng | Nội dung |
| --- | --- |
| `artifacts/hybrid-verification/` | TRX, ma trận lỗi, cluster app, quorum members, image IDs |
| `artifacts/monitoring-verification/` | Scrape/dashboard, alert recovery, outbox failure, broker metrics |
| `artifacts/proxy-verification/` | MVC replicas và HTTPS |
| `artifacts/load-verification/` | Load test và lần đo bị nhiễu giữ riêng |
| `artifacts/restore-verification/` | SQL backup/restore/CHECKDB |
| `artifacts/replication-verification/` | Mongo election/restore, Rabbit cluster/message |

Artifacts có thể chứa dữ liệu test/credential, nên không commit hoặc chia sẻ cả
thư mục khi chưa rà soát. Các file source mới/thay đổi cần được review trước commit.

## 11. Những phần chưa làm

| Hạng mục | Trạng thái |
| --- | --- |
| Đưa hệ thống lên AWS | Chưa deploy, chưa tạo tài nguyên AWS |
| ECS Service Auto Scaling | Chưa cấu hình/chạy thực tế |
| EC2 ASG + Capacity Provider | Chưa cấu hình/chạy thực tế |
| Chạy ứng dụng trên nhiều server/AZ | Chưa kiểm chứng; hiện cùng một Docker host |
| SQL Server HA/replication | Chưa làm local; giữ Express, dự kiến RDS SQL Server |
| Chuyển Mongo/Rabbit live sang cluster | Chưa thực hiện; cluster hiện thuộc test/lab |
| Chịu mất cả Docker host hoặc AZ | Chưa chứng minh |
| Production TLS và TLS giữa DB/broker | Chưa hoàn thiện |
| Production monitoring/log tập trung/kênh alert | Chưa hoàn thiện toàn bộ |
| Backup off-site tự động và full disaster recovery | Chưa chứng minh |
| Rollout các thay đổi mới lên stack live 8080 | Chưa thực hiện trong đợt này |

## 12. Cách chạy lại bản tích hợp

```powershell
# Lần đầu hoặc khi source thay đổi: build và chạy kiểm chứng.
.\scripts\Test-HybridHA.ps1 -Nginx -Monitoring -Cluster -Tls -KeepRunning

# Sau khi suite hoàn tất, chạy các kiểm tra sau theo thứ tự, không chạy chồng load test.
.\scripts\Test-Proxy.ps1
.\scripts\Test-Monitoring.ps1
.\scripts\Test-MonitoringAlerts.ps1
.\scripts\Test-OutboxMetrics.ps1
node tests/load-readonly.mjs
.\scripts\Test-BackupRestore.ps1
```

Chỉ thêm `-SkipBuild` khi image đang có đúng source cần kiểm chứng. Xem runbook local
để dừng/rollback đúng project. Khi chuẩn bị AWS, làm theo hướng dẫn AWS và chốt
account, region, ngân sách, storage, database và kế hoạch chuyển dữ liệu trước deploy.

## Đổi tên proxy ngày 12/11/2026

Stack hybrid dùng service `gateway` với image `kong:3.9.1`; YARP chuyển thành `web-router` với image `tourbooking-hybrid-web-router`. Đã cập nhật Compose, upstream edge/Kong, monitoring và script liên quan. Stack SQLite giữ tên cũ. Xem [gateway-and-edge.md](gateway-and-edge.md) để biết vai trò và cổng truy cập.
