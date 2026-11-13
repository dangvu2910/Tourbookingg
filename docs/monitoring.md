# Monitoring sức khỏe service và HTTP

Overlay `compose.monitoring.yaml` dành cho stack hybrid (một container mỗi service).
Không chạy migration, không gắn volume nghiệp vụ.
Prometheus khám phá IP từng container qua Docker DNS mỗi 15 giây, blackbox exporter
gọi `/health/ready` và `/health/live`. Không cấp credential database cho monitoring.

## Khởi động

Để kiểm chứng bằng image mới và dữ liệu riêng, ưu tiên
`scripts/Test-Hybrid.ps1 -Monitoring -KeepRunning` rồi
`scripts/Test-Monitoring.ps1`; xem [hướng dẫn local](local-distributed.md).
Overlay bật `Metrics:Enabled` cho app. Cần build/recreate app để HTTP metrics có
hiệu lực; chỉ khởi động ba container monitoring sẽ chỉ có health probes nếu app
hiện tại chưa bật metrics. Không tự áp dụng thay đổi này lên live.

Thêm `GRAFANA_ADMIN_PASSWORD` riêng, đủ mạnh vào `.env` local (đã gitignore).
Dùng đúng project/env và các file override của stack hiện tại. Ví dụ với
SQL Server container:

```powershell
$files = @('-f','compose.hybrid.yaml','-f','compose.monitoring.yaml')
.\scripts\Compose.ps1 @files config --quiet
.\scripts\Compose.ps1 @files up -d --no-deps prometheus blackbox grafana
```

Lệnh chỉ khởi động ba service monitoring; không chạy lại init/provision nghiệp vụ.
Grafana tại `http://localhost:3000`, user `admin`, password từ `.env`.
Hai dashboard **Tourbooking - replica health** và **Tourbooking - HTTP and process**
được provision từ source.
Prometheus tại `http://localhost:9090`: kiểm tra trang Targets, Rules và Alerts.
Không mở các cổng này ra Internet. Không mount Docker socket vào monitoring.

## Xử lý cảnh báo

- `ServiceDiscoveryMissing`: kiểm tra service/container còn chạy và DNS trên đúng
  Compose network. Không coi mất target DNS là hệ thống khỏe.
- `AppReadinessFailed`: so sánh liveness, kiểm tra dependency SQL/Mongo và log của
  replica; dùng correlation ID để truy vết. Không restart database live để thử lỗi.
- `AppProcessUnavailable`: kiểm tra process, container exit/restart và tài nguyên host.
- `ProbeScrapeFailed`: kiểm tra blackbox exporter/network; lỗi exporter khác lỗi ứng dụng.
- `HealthProbeSlow`: endpoint readiness vượt hai giây trong năm phút; kiểm tra tải
  và dependency trước khi chỉnh ngưỡng.
- `ApplicationMetricsUnavailable`: kiểm tra app có image mới và bật metrics hay chưa.
- `ApplicationErrorRateHigh`: HTTP 5xx > 5% trong năm phút; đối chiếu request rate và log.
- `ApplicationLatencyHigh`: HTTP p95 > 2 giây trong năm phút; kiểm tra dependency/tải.

Cảnh báo hiện hiển thị trong Prometheus/Grafana; chưa có Alertmanager hoặc kênh gửi
ra ngoài. Ngưỡng là baseline cần hiệu chỉnh theo số liệu của môi trường.

## Kiểm chứng và rollback

```powershell
$monitoring = (Resolve-Path infrastructure/monitoring).Path
docker run --rm --mount "type=bind,source=$monitoring,target=/etc/prometheus,readonly" --entrypoint /bin/promtool prom/prometheus:v3.2.1 check config /etc/prometheus/prometheus.yml
docker run --rm --mount "type=bind,source=$monitoring,target=/etc/prometheus,readonly" -w /etc/prometheus --entrypoint /bin/promtool prom/prometheus:v3.2.1 test rules alerts.test.yml
```

Chỉ trên stack test riêng: dừng một container, quan sát probe bằng 0 hoặc target DNS
biến mất; dừng cả service để kiểm tra `ServiceDiscoveryMissing`. Chờ thời gian DNS,
scrape và `for: 1m`; khởi động lại và xác nhận alert hết. Lưu timestamp UTC, topology,
query/API response vào `artifacts/monitoring-verification/`, không lưu password.
Local chạy một container mỗi service (6 app target, 4 SQL outbox observer). Số IP khám phá chưa phải số task mong muốn của
orchestrator; chưa có alert
về số task mong muốn. DNS discovery này dành cho Docker Compose, chưa chứng minh
multi-host placement.

Rollback: dùng cùng `$files` chạy `stop prometheus blackbox grafana`. Giữ volume
monitoring để bảo toàn lịch sử; không dùng `down --volumes`.

## Phạm vi hiện có

Dashboard đo readiness/liveness, số replica khám phá, probe duration, request rate,
HTTP status rate, HTTP p50/p95/p99 và process CPU/working set. Histogram gộp các
request được middleware đo, không chỉ một API; static files có thể phục vụ trước
middleware. Metric labels không chứa raw URL/query string, token hoặc nội dung chat.
Endpoint metrics opt-in và bị chặn ở Kong public; backend vẫn phải private.
SQL outbox được đọc theo service ownership mỗi 15 giây, timeout ba giây; dùng max
giữa replica để không cộng trùng cùng backlog. Observation lỗi trả gauge success=0
và backlog NaN, không giả thành không có sự kiện chờ. Profile cluster có RabbitMQ
exporter và dashboard queue messages theo node. Chưa có host disk/restart hoặc
database connections/failover metrics; chưa hoàn thành monitoring production và
chưa thay đổi plugin/database live.

## Log tập trung (Loki)

`compose.monitoring.yaml` có thêm `loki` (Grafana Loki 3.6) và `alloy` (Grafana Alloy). Alloy đọc stdout/stderr của mọi container trong project qua Docker socket (read-only) rồi đẩy sang Loki. Log được giữ 7 ngày trong volume `monitoring-loki`.

- Các service .NET ghi log JSON, mỗi bản ghi một dòng, kèm scope `CorrelationId`. Alloy gắn nhãn `service`, `container`, `project` và `level`.
- Xem log trong Grafana: **Explore → Loki**, hoặc bảng "Logs: warnings and errors" trên dashboard *Tourbooking - HTTP and process*.
- Ví dụ truy vấn:
  - `{service="booking"} | json | LogLevel="Error"`: lỗi của booking.
  - `{project="tourbooking-live"} |= "<X-Correlation-ID>"`: một request đi qua những service nào. Lấy mã từ header `X-Correlation-ID` của response.
  - `{service=~".+"} |= "Circuit OPEN"`: các lần circuit breaker mở.
- Prometheus job `logging` theo dõi `loki:3100` và `alloy:12345`. `up{job="logging"} == 0` nghĩa là log không còn được thu thập. Image Loki là distroless nên không có Docker healthcheck.
