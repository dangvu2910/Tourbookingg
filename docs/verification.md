# Kiểm chứng

Kiểm thử có ghi dữ liệu chỉ chạy trong project `tourbooking-hybrid-verification`, cổng `18083`, SQL Server container và MongoDB/volume/network riêng. Không gắn database/volume live vào test. Credential ngẫu nhiên nằm trong `artifacts/hybrid-verification/test.env`, được gitignore.

## Chạy lại

```powershell
dotnet build Tourbooking.sln
dotnet test tests/RabbitMq.Tests/RabbitMq.Tests.csproj
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

- Đã cập nhật `tourbooking-live` tại `http://localhost:8080`. Có **17 container running/healthy**: hai replica cho 5 backend, Gateway và Kong; một HAProxy, MongoDB và RabbitMQ. SQL Express tiếp tục ở Windows, không chuyển dữ liệu.
- Kiểm tra GET trên cổng 8080 xác nhận cả hai instance của từng backend/Gateway và hai Kong nhận request. Các API riêng trả 401 đúng policy khi chưa đăng nhập. Image ID của mọi replica khớp image đã kiểm thử; bằng chứng trong `artifacts/hybrid-verification/live-check.json` và `tested-images.json`.
- Không chạy migration/prune/nhập lại dữ liệu. Số dòng nghiệp vụ trước/sau rollout được đối chiếu không giảm; bản ghi đối chiếu ở thư mục backup.
- Backup và rollback: `backups/ha-deploy-20261106-171650`. SQL COPY_ONLY/CHECKSUM đã RESTORE VERIFYONLY; đường dẫn file native ở `sql-backup-receipt.json`. ACL của thư mục SQL không cho sao chép file vào repository, nên giữ bản đã xác minh tại thư mục Backup của SQL, không đổi ACL. Mongo archive đã dry-run verify; có backup key ring/ảnh tour, manifest SHA256 và snapshot ứng dụng để rollback.
- Đã dọn tài nguyên stack test riêng sau kiểm chứng. Các database/volume live, ảnh và khóa đăng nhập được giữ nguyên.

## Giới hạn

Health check cần thời gian phát hiện lỗi; request đang xử lý có thể thất bại. Khi cả service/database mất, chức năng phụ thuộc phải báo lỗi thay vì giả thành công. Kết quả không chứng minh database cluster, multi-host HA, backup off-site hoặc chịu mất toàn bộ Docker host. Các database/volume live được giữ nguyên khi rollout ứng dụng.
