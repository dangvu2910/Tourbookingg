# Kiến trúc

Luồng public local: **Kong → API backends**, hoặc **Kong → web-router (YARP) → MVC backends**. Một container mỗi service, một Docker network; HTTP nội bộ gọi thẳng `http://<service>:8080`. Không có HAProxy/Nginx/edge local.


| Backend | Module | Sở hữu | Schema / collection |
| --- | --- | --- | --- |
| Identity | Accounts, Review | Người dùng, vai trò, cookie/JWT; đánh giá tour | `identity` |
| Catalog | Tours, Reservations, Reporting | Tour, giá, giữ chỗ; báo cáo chỉ đọc bản sao | `catalog` |
| Booking | Booking, Payment | Đơn, thanh toán | `booking` |
| Customer | Support (chat trong Mongo) | Liên hệ/hỗ trợ | `customer`, `ChatDB.conversations` |
| Notification | Notifications | Thông báo theo EventId | `NotificationDB.notifications` |

## Cấu trúc thư mục service

Mỗi backend .NET theo cùng một layout. Host chỉ là composition root; mã nghiệp vụ nằm trong module.

```text
services/<backend>/
  <Host>.csproj, Program.cs, appsettings.json, Dockerfile, wwwroot/
  Modules/<Module>/
    Domain/          entity (<Module>.Models.csproj hoặc *.Domain.csproj)
    Application/     use case, interface client/repository (khi có)
    Infrastructure/  EF/Mongo/HTTP/RabbitMQ (khi có)
    Web/             <Module>.csproj: Controllers/, ViewModels/, Views/, Program.cs chạy riêng
```

- Host nạp module bằng `ProjectReference` có `IsConsolidatedHost=true` và tên assembly trong `TourbookingHost.RunAsync(args, "<backend>", "<Module>", ...)`.
- Module mới phải được thêm vào `ServiceBoundary.Owners` của backend sở hữu, biến `Services__<module>` trong compose và quyền schema trong `infrastructure/sql/provision.sql`.
- Notification là minimal API một project; các thư mục `Domain/Application/Infrastructure/Web` nằm trong cùng `Notification.csproj`.
- `ApplicationDbContext` và migration lịch sử vẫn dùng chung ở `Shared/Persistence`; ranh giới ghi/đọc do `ServiceData` và quyền schema bảo vệ. Endpoint giữ chỗ nội bộ vẫn đăng ký ở `Shared/Hosting`.
- Namespace C# (`Tourbooking.Controllers`, `Tourbooking.Models`, `Tourbooking.ViewModels`) và route MVC/API giữ nguyên.

### Chuyển dữ liệu khi đổi chủ sở hữu Review/Reporting

Reporting không sở hữu bảng nên chỉ đổi code/config. Review sở hữu `TourReviews`, `TourReviewVotes`: database mới được provision vào schema `identity`, nhưng **database đang có dữ liệu phải chuyển bảng trước khi chạy image mới**, nếu không trang đánh giá sẽ thấy bảng rỗng. Chưa có script tự động; chạy thủ công trong cửa sổ bảo trì, sau backup đã kiểm chứng, và đối chiếu số dòng:

- SQL Server container (`CustomerDB` → `IdentityDB`): để Identity khởi tạo bảng trống, chép dữ liệu `TourReviews` rồi `TourReviewVotes` với `IDENTITY_INSERT ON`, đối chiếu số dòng, giữ bảng cũ đến khi xác nhận xong.

SQL Server (container) dùng một instance với database riêng `IdentityDB`, `CatalogDB`, `BookingDB`, `CustomerDB`; mỗi database dùng schema sở hữu (`identity`, `catalog`, `booking`, `customer`) và schema đọc tương ứng `<service>_read`. Backend không mở database/schema của backend khác. Schema `_read` chỉ là bản sao tối thiểu qua HTTP nội bộ có khóa service; user summary không có mật khẩu, security stamp hoặc vai trò để cấp quyền. JWT được kiểm tra tại Identity; cookie dùng chung key ring và định kỳ xác minh lại. Điểm vào public chặn API nội bộ.

## Đặt tour và thanh toán

1. Catalog giữ chỗ theo tour/ngày, tối đa 20 khách; RequestId chống yêu cầu lặp. Transaction và SQL application lock bảo vệ giữa nhiều replica.
2. Booking lưu đơn/payment cùng transaction, dùng giá và snapshot do Catalog chốt. Tác vụ giữ chỗ xử lý xác nhận, hủy và khôi phục lịch cũ khi đổi lịch thất bại; không giữ transaction SQL trong lúc gọi HTTP.
3. Khách báo đã chuyển khoản dùng trạng thái mới nhất. Gửi lại không tạo payment hoặc ghi đè mã giao dịch đã nhận. Trang chờ đối soát ẩn QR và nhắc không chuyển tiền lần nữa. Nút này không tự thu tiền.
4. Admin đối soát rồi đánh dấu Paid/cấp vé. Hủy lặp giữ RefundPending/Refunded. Hủy/đổi lịch/duyệt vẫn bảo vệ version và CSRF.

## Sự kiện và chịu lỗi

SQL ghi nghiệp vụ/outbox cùng transaction. Dispatcher claim lease dưới khóa SQL, publish có xác nhận broker rồi đánh dấu đã gửi. Delivery vẫn là at-least-once: Notification dùng EventId làm `_id`; chat cập nhật reply và pending event trong cùng Mongo document. Khóa SQL bảo vệ việc thay bản sao đọc giữa các replica.

- Một container mất: mỗi service chỉ có một container nên mất container là mất service đó đến khi Docker khởi động lại (`restart: unless-stopped`).
- Cả service mất: chức năng phụ thuộc trả 502/503/504 hữu hạn; phần không phụ thuộc tiếp tục chạy. Projection có thể dùng cache cũ, không dùng cache để bán vượt chỗ.
- RabbitMQ mất: SQL/outbox và đọc thông báo vẫn hoạt động; sự kiện chờ gửi lại khi broker phục hồi.
- Identity mất: cookie còn hiệu lực có thể tiếp tục theo chính sách hiện có; login mới và xác thực Bearer cần Identity. Không bỏ qua kiểm tra Bearer.
- Database mất: phần cần database đó ngừng nhận traffic; không giả báo ghi thành công.

Kong dùng upstream pools, health check và timeout hữu hạn; Kong chặn `/internal`, `/service-info`, `/metrics` ở cổng public. Kong không tự retry payment command. Local không có HA: mọi container, SQL Server, MongoDB, RabbitMQ, Docker host và ổ đĩa là điểm lỗi đơn.

Tham khảo: [Kong health checks](https://developer.konghq.com/gateway/traffic-control/health-checks-circuit-breakers/).

## Chuông thông báo cho admin

Luồng bất đồng bộ, không gọi chéo service khi khách đặt tour:

1. Booking lưu đơn và ghi sự kiện `booking.created` vào outbox trong cùng transaction.
2. `OutboxDispatcher` đẩy sự kiện lên RabbitMQ; `NotificationConsumer` (Notification service) lưu vào MongoDB `NotificationDB.notifications` (khử trùng lặp theo `EventId`).
3. Header của mọi trang admin (`Shared/Presentation/Views/Shared/_AdminNotificationBell.cshtml`) hỏi `GET /api/notifications/admin/summary` mỗi 10 giây qua Kong → web-router → notification. Khi số chưa xem tăng: chuông rung, hiện toast, tiêu đề tab có `(n)`.
4. Mở chuông gọi `POST /api/notifications/admin/seen` (cần header `X-Requested-With`), lưu thời điểm đã xem theo từng admin ở `NotificationDB.admin_seen`.

Chỉ báo các việc admin cần xử lý: đơn mới (`booking.created`), khách đã gửi thanh toán (`booking.updated` trạng thái `Processing`) và yêu cầu hỗ trợ mới (`contact.created`); logic ở `AdminFeed`. Notification service tắt thì chuông mờ đi, trang admin và việc đặt tour vẫn chạy; sự kiện chờ trong RabbitMQ và hiện ra khi service lên lại. Độ trễ tối đa khoảng 15 giây (outbox ~1–5 giây + chu kỳ hỏi 10 giây).

## Circuit breaker giữa các service (chương 8)

Mọi lời gọi HTTP giữa các service đi qua `IHttpClientFactory` đều có pipeline resilience. Pipeline được đăng ký trong `Shared/Routing/ServiceResilience.cs`, dùng `Microsoft.Extensions.Http.Resilience`/Polly, và mỗi service đích (`identity:8080`, `catalog:8080`, ...) có pipeline riêng. Các lớp, từ ngoài vào trong:

1. **Retry**: tối đa 2 lần, backoff có jitter, **chỉ cho GET/HEAD**. Lệnh giữ chỗ và thanh toán là POST nên không bao giờ bị client gửi lại; chúng dựa vào `RequestId`.
2. **Circuit breaker**: mạch mở khi ≥50% của tối thiểu 4 lời gọi trong 30 giây bị lỗi. Lỗi được tính là 5xx, 408, 429, lỗi mạng hoặc timeout. Mạch mở thì trong 15 giây mọi lời gọi tới service đó lỗi ngay, không chờ timeout. Sau đó một lời gọi thử được cho đi (half-open); thành công thì mạch đóng.
3. **Timeout**: 3 giây cho mỗi lần thử.

Khi mạch mở, lỗi được đổi thành `HttpRequestException` để mọi chỗ gọi giữ nguyên cách dự phòng như trước (dùng projection đã cache, trả 503/504, giữ phiên đăng nhập). Trạng thái mạch có trong metric `tourbooking_http_circuit_state{target}` và `tourbooking_http_circuit_opened_total`, và trong log `Circuit OPEN/HALF-OPEN/CLOSED`.

Đã kiểm chứng trên `tourbooking-live` ngày 13/11/2026: dừng identity thì `/Tours` chậm 3,2 giây ở request đầu, sau đó mạch mở và các request tiếp theo chỉ còn 16–90 ms (vẫn 200, dùng cache). Bật lại identity thì mạch chuyển HALF-OPEN rồi CLOSED.
