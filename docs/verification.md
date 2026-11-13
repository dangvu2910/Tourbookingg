# Kiểm chứng

> Ghi chú 13/11/2026: HAProxy/Nginx, overlay HA (`compose.ha.yaml`, `compose.single.yaml`, `compose.nginx*.yaml`), TLS local và `Deploy-HybridHA`/`Test-Proxy` đã được gỡ; local chạy một container mỗi service, Kong là điểm vào duy nhất. Kết quả bên dưới là lịch sử của cấu hình cũ; chạy lại bằng `scripts/Test-Hybrid.ps1`.

## Bổ sung local ngày 12/11/2026

- Lệnh tích hợp cuối `Test-HybridHA.ps1 -Nginx -Monitoring -Cluster -Tls -SkipBuild
  -KeepRunning` đã hoàn tất: **114/114 pass, 0 fail, 0 skip** trên SQL Server test,
  Mongo replica set và Rabbit quorum cluster. Sau đó dừng broker A, bài nghiệp vụ
  support reply -> notification vẫn **1/1 pass**. TRX và log ở
  `artifacts/hybrid-verification/test-results/`, `broker-failover.log`.
- Đã sửa thứ tự khởi động/dừng Rabbit cluster và kiểm tra membership từng queue:
  ba queue nghiệp vụ đều có ba member. Có một lần khôi phục metadata test bằng
  force_boot sau shutdown đồng thời trong quá trình chẩn đoán; script chuẩn không
  tự force_boot. Quy trình cuối đã chạy lại thành công mà không cần force_boot.
- `Test-Proxy.ps1`: MVC Tours thực nhận request qua hai Catalog replica; HTTPS
  localhost trả 200 khi client kiểm tra certificate bằng CA test; correlation ID
  trong header và JSON metadata khớp. Metrics public bị chặn cả `/metrics/` và chữ hoa.
- Load test độc lập sau build/test: **6.698 request catalog / 20 giây**, 4 client,
  **334,76 request/giây**, p50 **10,07 ms**, p95 **22,80 ms**, p99 **35,87 ms**,
  **0 lỗi**, thực nhận qua hai Catalog replica. Đây là phép đo chỉ đọc trên một host,
  không phải throughput booking hoặc bằng chứng autoscaling. Kết quả ở
  `artifacts/load-verification/result.json`. Lần đo bị chồng build/test được giữ
  riêng ở `overlapped-build-result.json`, không dùng làm baseline nghiệm thu.

- Nginx overlay và HTTP metrics đã được build trong image app/test. Ma trận Nginx
  trên SQL/Mongo test đạt **18 tình huống**, xác nhận round-robin thực qua hai replica,
  lỗi dependency hữu hạn và SQL outbox chuyển lại thành thông báo sau broker phục hồi.
  Bằng chứng: `artifacts/hybrid-verification/resilience.json`.
- Prometheus scrape **12 app metrics targets**; Grafana provision đủ hai dashboard;
  `/metrics` public trả 404. Đã thử mất exporter và quan sát `ProbeScrapeFailed`
  firing rồi resolved sau phục hồi. Kết quả ở `artifacts/monitoring-verification/`.
- Bản metrics cuối có **8 SQL outbox observers** khỏe, **3 RabbitMQ exporters** và
  queue metrics có dữ liệu. Promtool validate **12 alert rules**; rule tests gồm
  chống cộng trùng backlog giữa replica và thời gian chờ cảnh báo đều đạt.
- `Test-OutboxMetrics.ps1` đã dừng SQL test, xác nhận cả tám observers báo lỗi và
  backlog NaN, sau đó khởi động lại SQL và xác nhận cả tám observers phục hồi.
  Bằng chứng: `artifacts/monitoring-verification/outbox-failure.json`.
- SQL: đã backup CatalogDB test với COPY_ONLY/CHECKSUM, RESTORE VERIFYONLY,
  **restore thật sang DB UUID mới**, CHECKDB và đối chiếu số dòng. Không restore đè.
  Bằng chứng: `artifacts/restore-verification/sql-restore.log`.
- Lab MongoDB ba member: majority marker giữ được sau primary election;
  mongodump/mongorestore sang namespace UUID mới thành công, có checksum archive.
- Lab RabbitMQ ba node: quorum queue giữ message sau mất broker A, đọc đúng marker
  qua broker B. Kết quả cluster/restore ở `artifacts/replication-verification/`.
- SQL Server chạy một container, không có SQL HA. SQL Express và kế hoạch AWS đã bỏ ngày 13/11/2026.

Các kiểm chứng trên chạy cùng một Docker host, chưa chứng minh host/AZ failover,
TLS giữa database/broker, hoặc SQL HA. Dữ liệu live và SQLite legacy giữ nguyên.
Hướng dẫn tái chạy: [local-distributed.md](local-distributed.md).
Image IDs đang chạy và commit/worktree status được ghi tại
`artifacts/hybrid-verification/local-images.json`. Stack test được giữ chạy để xem
tại HTTP 18083, HTTPS 18443, Grafana 13000 và Prometheus 19090. Stack live 8080
không được rollout trong đợt này.

## Monitoring bổ sung ngày 10/11/2026

- Đã thêm overlay Prometheus/blackbox/Grafana, dashboard provision từ source và
  runbook tại [monitoring.md](monitoring.md).
- `promtool check config`: đạt; nhận diện đủ 5 alert rules.
- `promtool test rules`: đạt cho readiness lỗi, khoảng chờ trước khi cảnh báo,
  liveness còn khỏe và service biến mất khỏi discovery.
- Compose hybrid + HA + monitoring `config --quiet`: đạt; JSON dashboard parse được.
- Chưa bật monitoring trên live, chưa kiểm tra dashboard bằng trình duyệt hoặc
  failover thực với monitoring. Không thay đổi database/volume nghiệp vụ.
- Đây là baseline health monitoring; các metrics nghiệp vụ/tài nguyên và multi-host
  HA trong kế hoạch vẫn chưa hoàn thành.

Kiểm thử có ghi dữ liệu chỉ chạy trong project `tourbooking-hybrid-verification`, cổng `18083`, SQL Server container và MongoDB/volume/network riêng. Không gắn database/volume live vào test. Credential ngẫu nhiên nằm trong `artifacts/hybrid-verification/test.env`, được gitignore.

## Chạy lại

```powershell
dotnet build Tourbooking.sln
dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj
.\scripts\Test-HybridHA.ps1
```

Suite mặc định chỉ chạy các bài không cần dịch vụ thật. `Test-HybridHA` build image, tạo stack SQL/Mongo test, chạy ma trận lỗi và suite tích hợp, rồi dọn đúng tài nguyên test. `-SkipBuild` dùng image đã build; `-KeepRunning` giữ môi trường để chẩn đoán. Bài giữ chỗ dùng database riêng `TourbookingReservationTest_Suite`, được tạo bởi bước provision test.

Kết quả máy chạy kiểm thử nằm trong `artifacts/hybrid-verification`: `resilience.json`, `test-results/hybrid.trx`, `dotnet-tests.log` khi chạy bằng script. Không commit artifact chứa dữ liệu hoặc credential.

## Kết quả xác minh ngày 06/11/2026

- Build solution: thành công, không warning/error.
- Ma trận SQL/Mongo: 18 tình huống đạt, gồm baseline, dừng một replica của 7 tầng, dừng toàn bộ từng tầng và SQL/Mongo, dừng RabbitMQ. Cả 6 loại backend/Gateway và Kong thực nhận request qua hai instance khác nhau.
- Đã xác nhận booking/payment gửi lặp sau cập nhật giữ chỗ, không hiện lại QR; outbox SQL được chuyển thành thông báo Mongo sau khi RabbitMQ phục hồi.
- Kiểm thử hồi quy cache user rỗng: 13 bài đạt. Nhận diện người dùng lấy từ principal đã xác thực, không đợi profile cache ở replica đồng bộ.
- Suite tích hợp SQL Server/MongoDB: **114/114 đạt, 0 lỗi, 0 bỏ qua**, bao gồm phân quyền DB, cạnh tranh giữ chỗ, rowversion, chat, admin đối soát và RabbitMQ. File TRX lưu ở đường dẫn trên.
- Đã chạy lại đầy đủ ma trận chịu lỗi trên image cuối sau khi sửa cache-user: 18 tình huống đều đạt; outbox phục hồi thành công.

## Triển khai thực tế

- Đã cập nhật `tourbooking-live` tại `http://localhost:8080`. Có **17 container running/healthy**: hai replica cho 5 backend, Gateway và Kong; một HAProxy, MongoDB và RabbitMQ.
- Kiểm tra GET trên cổng 8080 xác nhận cả hai instance của từng backend/Gateway và hai Kong nhận request. Các API riêng trả 401 đúng policy khi chưa đăng nhập. Image ID của mọi replica khớp image đã kiểm thử; bằng chứng trong `artifacts/hybrid-verification/live-check.json` và `tested-images.json`.
- Không chạy migration/prune/nhập lại dữ liệu. Số dòng nghiệp vụ trước/sau rollout được đối chiếu không giảm; bản ghi đối chiếu ở thư mục backup.
- Backup và rollback (đã xoá 13/11/2026 khi bỏ HA): `backups/ha-deploy-20261106-171650`. SQL COPY_ONLY/CHECKSUM đã RESTORE VERIFYONLY; đường dẫn file native ở `sql-backup-receipt.json`. ACL của thư mục SQL không cho sao chép file vào repository, nên giữ bản đã xác minh tại thư mục Backup của SQL, không đổi ACL. Mongo archive đã dry-run verify; có backup key ring/ảnh tour, manifest SHA256 và snapshot ứng dụng để rollback.
- Đã dọn tài nguyên stack test riêng sau kiểm chứng. Các database/volume live, ảnh và khóa đăng nhập được giữ nguyên.

## Giới hạn

Health check cần thời gian phát hiện lỗi; request đang xử lý có thể thất bại. Khi cả service/database mất, chức năng phụ thuộc phải báo lỗi thay vì giả thành công. Kết quả không chứng minh database cluster, multi-host HA, backup off-site hoặc chịu mất toàn bộ Docker host. Các database/volume live được giữ nguyên khi rollout ứng dụng.
