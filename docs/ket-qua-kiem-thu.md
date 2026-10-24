# Kết quả kiểm chứng ngày 15 tháng 10 năm 2026

> Kết quả bên dưới là bản ghi lịch sử của kiến trúc 5 service, không phải xác nhận kiểm thử bản 9 service.

Các kiểm tra dưới đây chạy trên môi trường Docker mới của dự án. Cơ sở dữ liệu LocalDB gốc không tham gia các thử nghiệm này.

| Kiểm tra | Kết quả |
| --- | --- |
| Build toàn solution .NET 10 | Thành công, 0 lỗi và 0 cảnh báo |
| Gateway và 4 dịch vụ phía sau | Tất cả readiness healthy |
| RabbitMQ publish và receive | Vượt qua |
| Đăng ký, chia sẻ cookie qua Gateway, đặt tour, gửi lại RequestId, xác nhận thanh toán bởi Admin, nhận thông báo | Vượt qua |
| API riêng tư, quyền Admin, chủ sở hữu booking, CSRF | Vượt qua |
| Rollback dữ liệu booking cùng outbox | Vượt qua |
| Hai cập nhật dùng phiên bản cũ của booking | Bản cũ bị từ chối bằng optimistic concurrency |
| Hai thao tác lưu cùng EventId | Chỉ có một thông báo được lưu |
| Dừng RabbitMQ rồi tạo booking | Booking vẫn lưu thành công |
| Bật lại RabbitMQ | Sự kiện pending được giao đến Notification |
| Sao lưu hai SQL database | BACKUP CHECKSUM và RESTORE VERIFYONLY thành công |
| Khôi phục đầy đủ từ bản vừa sao lưu | Hai database, RabbitMQ definitions, auth keys và ảnh được phục hồi; các dịch vụ khởi động lại thành công |

Bộ integration test gồm 6 test, chạy bằng service `tests` trong Compose. Test workflow bao gồm nhiều bước nghiệp vụ và phân quyền trong một test. Các script backup, restore và resilience được chạy riêng để kiểm chứng hành vi vận hành thực tế.

Log chi tiết được lưu trong thư mục `artifacts/` trên máy chạy, không commit vào repository: `dotnet-build-final.log`, `integration-tests.log`, `integration-tests-final.log`, `resilience-test.log`, `backup-verification.log`, `restore-drill.log`.

Phạm vi chưa kiểm chứng: tải cao, cụm nhiều máy, replica SQL/RabbitMQ, failover node vật lý và thanh toán qua nhà cung cấp thực tế. Những nội dung này không được tính là tính năng đã triển khai.
