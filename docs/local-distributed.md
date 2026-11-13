# Chạy và kiểm chứng local

Các cấu hình ở đây dùng project test riêng. Không thay stack live,
volume, ảnh tour hoặc khóa đăng nhập của stack live. Đọc `AI-rules/AGENTS.md`,
`AI-rules/api-guidelines.md` trước khi thay đổi.

Local chạy một container mỗi service trên một Docker network, Kong là điểm vào duy nhất.
Không có load balancer/edge, replica hay TLS; dự án chỉ chạy local.

## Kiểm thử tích hợp và monitoring

```powershell
.\scripts\Test-Hybrid.ps1 -Monitoring -KeepRunning
.\scripts\Test-Monitoring.ps1
.\scripts\Test-MonitoringAlerts.ps1
.\scripts\Test-OutboxMetrics.ps1
node tests/load-readonly.mjs
.\scripts\Test-BackupRestore.ps1
```

Lần đầu build image. Lần sau có thể thêm `-SkipBuild` khi code/image không đổi.
Test ghi dữ liệu chỉ trong `tourbooking-hybrid-verification`; Kong test publish ở port `18083`
(`HTTP_PORT` trong `artifacts/hybrid-verification/test.env`).
Prometheus: `http://localhost:19090`; Grafana: `http://localhost:13000`.
User Grafana `admin`; password ngẫu nhiên nằm trong
`artifacts/hybrid-verification/test.env` (gitignore), không dùng password live.

`Test-Hybrid` kiểm tra mọi service trả lời qua Kong, route MVC/API, booking/payment
gửi lặp và ma trận mất toàn bộ từng service/SQL/Mongo/broker (route phụ thuộc trả lỗi
hữu hạn, route khác vẫn chạy; outbox gửi lại sau khi broker phục hồi), sau đó chạy suite
tích hợp. Kong/proxy không tự replay booking/payment khi timeout.
Load test chỉ đọc catalog với mặc định 4 client, 20 giây, p95 <= 2 giây và error rate
<= 1%. Đây là baseline, không phải ngưỡng production hoặc bài đo throughput booking.
Các biến `LOAD_CONCURRENCY`, `LOAD_SECONDS`, `LOAD_MAX_P95_MS`, `LOAD_MAX_ERROR_RATE`
cho phép đổi ngưỡng trước khi chạy. URL cố định vào test để tránh bắn tải vào live.
Chạy tuần tự khi build, integration suite và các fault test đã xong; không đo baseline
trong lúc có thao tác dừng service/database hoặc compile image khác.

SQL restore test chỉ tìm container có label project test, backup CatalogDB rồi restore
sang DB tên UUID mới, CHECKDB và đối chiếu số dòng. Chạy sau các test ghi dữ liệu,
khi không có tải thay đổi catalog. Không restore đè source; không dùng `WITH REPLACE`.

## Lab database/broker replication

```powershell
.\scripts\Test-Replication.ps1
```

Script tạo project `tourbooking-replication-test`: MongoDB 3 member chứa dữ liệu,
keyfile/authentication, RabbitMQ 3 node với default quorum queues và metrics plugin.
Mật khẩu/key ngẫu nhiên trong `artifacts/replication-verification/test.env`.
Chỉ management test bind localhost 15681/15682; cổng DB và AMQP không publish.

Kiểm tra gồm Mongo majority write, mất primary/election, marker sau election,
mongodump/mongorestore vào database UUID mới và checksum archive; Rabbit cluster đủ
3 node, publish durable marker, dừng broker A rồi lấy lại đúng payload qua broker B.
Script dừng/xóa container test khi xong, giữ named volume. `-KeepRunning` để chẩn đoán.
Không xóa file credential khi giữ volume vì user/key trong DB sẽ không tự đổi theo env.

Đây là **lab riêng**, chưa tự thay connection string của stack live hoặc chứng minh
app failover end-to-end trên cluster. Để chạy app test với replica set/quorum cluster,
dùng `Test-Hybrid.ps1 -Monitoring -Cluster -KeepRunning`: overlay
`compose.cluster-apps-test.yaml` tạo user Mongo ít quyền, đổi seed list và nhiều
broker endpoint, kiểm tra đọc sau election rồi chạy suite nghiệp vụ. Sau suite,
broker A bị dừng để kiểm tra luồng support reply -> notification qua các node còn lại.
Job `rabbit-cluster-ready` đợi ba broker chạy đầy đủ, bổ sung member cho quorum queue
test đã tồn tại; runner kiểm tra riêng cả ba queue nghiệp vụ đều có ba member.
Ba broker trong cluster không tự bảo đảm mỗi queue có ba replica, nhất là khi queue
được khai báo trước lúc node cuối join. Không áp dụng job test này lên queue live.
Script tắt A/B/C rồi đợi C/B/A khởi động lần lượt để phục hồi metadata. Nếu toàn cụm
bị tắt đột ngột, cần chẩn đoán node cuối và backup trước khi chọn recovery; không tự
`reset`, `force_boot` hoặc xóa volume để làm test chuyển xanh.
Profile này cần nhiều RAM hơn; chạy tuần tự sau lab độc lập. TLS giữa DB/broker node và host-level HA chưa
được chứng minh. Không deploy file có hậu tố `replication-test` vào production.
Mongo primary/secondary và Rabbit node chạy cùng Docker host; mất host vẫn mất cụm.
SQL Server chạy một container (không có SQL HA); SQL Express đã bỏ ngày 13/11/2026.

## Dừng và rollback

Dùng đúng env/project/file của lần khởi động, chạy `stop` để giữ dữ liệu. Với lab
replication, script mặc định `down` không xóa volume. Không dùng `prune` hoặc xóa
volume live.

Artifacts kết quả: `hybrid-verification`, `monitoring-verification`,
`load-verification`, `restore-verification`, `replication-verification` dưới `artifacts/`.
Chỉ chia sẻ kết quả đã bỏ secret; các file env không được commit.
