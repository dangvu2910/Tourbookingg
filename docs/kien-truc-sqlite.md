# Tách dữ liệu service sang SQLite

## Mục tiêu và phạm vi

Bản này phục vụ chạy local, đồ án và thử nghiệm tải nhỏ. Mỗi backend có file SQLite và thư mục bind mount riêng. Không có service nào mount database của service khác. Không cần container SQL Server để chạy ứng dụng sau chuyển đổi; RabbitMQ vẫn được giữ.

Giữ 9 thành phần ứng dụng hiện có (Gateway + 8 backend), không tự gom Review hoặc Reporting. Gateway không sở hữu dữ liệu nghiệp vụ nên không tạo database rỗng chỉ để đủ số lượng.

## Quyền sở hữu và bản sao đọc

`ServiceData.Owner` định nghĩa chủ sở hữu từng bảng. `SaveChangesAsync` từ chối ghi bảng nghiệp vụ thuộc service khác. Booking chỉ ghi Bookings; Payment chỉ ghi Payments; Support chỉ ghi ContactInquiries và ContactInquiryReplies. Identity giữ toàn bộ dữ liệu xác thực.

Để bảo toàn Razor và truy vấn tổng hợp hiện có, vẫn dùng chung model EF. Trong mỗi database, ngoài bảng do service sở hữu có thể có bảng bản sao chỉ đọc cần thiết:

| Service đọc | Nguồn bản sao |
| --- | --- |
| Catalog | Identity, Booking, Review |
| Booking | Identity, Catalog, Payment |
| Payment | Identity, Catalog, Booking |
| Review | Identity, Catalog, Booking |
| Support | Identity |
| Reporting | Identity, Catalog, Booking, Payment, Review |

Các bản sao được thay thế trong transaction qua API `/internal/data` trước khi action chạy. API yêu cầu `X-Service-Key` từ `.env`; Gateway chặn `/internal` và bỏ header nội bộ do trình duyệt gửi. PasswordHash chỉ tồn tại trong Identity; token và đăng nhập ngoài không được sao chép sang backend khác.

Nếu nguồn HTTP tạm ngắt, action dùng bản sao gần nhất và ghi warning. Lỗi dữ liệu/schema không bị che giấu thành dữ liệu rỗng. Đây là cơ chế đơn giản cho dữ liệu demo, không phải giải pháp đồng bộ toàn bộ bảng phù hợp tải lớn. Chưa tách hoàn toàn model theo bounded context; các bảng không dùng có thể vẫn hiện diện nhưng rỗng vì dùng schema chung.

SQLite connection tắt foreign-key enforcement trong các database có bản sao: khóa ngoại EF phục vụ join không thể đảm bảo tham chiếu giữa các miền được đồng bộ độc lập. Vì vậy, ràng buộc liên service được kiểm tra tại ứng dụng; không tuyên bố có transaction hoặc foreign key xuyên database. Notification không cần cơ chế bản sao này. Hướng tiếp theo là DTO/read model riêng, bỏ navigation xuyên miền và bật lại khóa ngoại cho các quan hệ cục bộ.

## Booking và Payment

1. Booking lưu đơn và `booking.created` vào Outbox cùng transaction.
2. Dispatcher publish qua RabbitMQ bằng persistent message và publisher confirmation.
3. Payment nhận sự kiện, tự tạo giao dịch trong database Payment. Inbox ghi EventId trong cùng transaction để chống xử lý trùng.
4. Trong lúc chờ, trang thanh toán hiển thị trạng thái đang chuẩn bị và nút kiểm tra lại.
5. Khách báo đã chuyển khoản: Payment lưu `Processing`, phát `payment.updated`; Booking nhận sự kiện và cập nhật đơn.
6. Admin vẫn xác nhận từ màn quản lý Booking theo nghiệp vụ chuyển khoản thủ công cũ. Sự kiện Confirmed cập nhật Payment thành Paid. Notification nhận các sự kiện để tạo thông báo.
7. Hủy booking chưa trả tiền dẫn đến Payment Cancelled; nếu đã Paid thì chuyển RefundPending, không giả định tiền đã tự hoàn.

Consumer bỏ qua sự kiện Processing nếu Booking đã Confirmed/Cancelled, và không hạ Payment Paid về Pending khi nhận lại sự kiện cũ. Mỗi consumer có durable queue, retry trễ và dead-letter queue sau 5 lần thử lại. Inbox chống trùng EventId; queue được khai báo cả từ producer để giữ message khi consumer khởi động muộn.

Không có transaction SQL chung giữa Booking và Payment. Giao diện có thể thấy trạng thái cũ trong khoảng ngắn. Bản này **chưa bổ sung kho chỗ theo lịch khởi hành, thời hạn giữ chỗ hoặc cổng thu tiền tự động**; đây là phạm vi nghiệp vụ khác với chuyển database.

## Support và Notification

Gửi yêu cầu, tải lịch sử và gửi phản hồi vẫn qua HTTP. Support lưu bản ghi cùng Outbox. Sự kiện `contact.created`, `contact.updated`, `contact.reply.created` được RabbitMQ chuyển tới Notification. Sự kiện phản hồi mang UserId của người mở yêu cầu, kể cả phản hồi do admin gửi.

Notification chỉ ACK sau khi lưu vào SQLite. EventId là khóa duy nhất chống thông báo trùng. Khi RabbitMQ tạm ngắt, Support vẫn lưu yêu cầu và Outbox; dispatcher gửi lại sau khi broker hồi phục. Đây là hội thoại hỗ trợ và thông báo bất đồng bộ; chưa thêm SignalR hay chat realtime.

## Chuyển dữ liệu SQL Server

```powershell
./scripts/Migrate-ToSqlite.ps1
```

Script build công cụ trước, dừng các writer/consumer, backup hai database nguồn vào volume backup SQL Server rồi chạy công cụ trong cùng mạng Docker. Thông tin kết nối lấy từ môi trường, không in mật khẩu.

Công cụ đọc dữ liệu bằng provider SQL Server, tạo schema SQLite mới, giữ nguyên ID, password hash, ngày giờ, nội dung hội thoại, trạng thái giao dịch và thông báo. Tiền được lưu bằng số nguyên có hệ số 100 để giữ chính xác hai chữ số thập phân. Rowversion cũ được giữ lúc nhập; những lần sửa tiếp theo dùng token byte do ứng dụng sinh và EF kiểm tra chống ghi đè.

Mỗi bảng được đối chiếu số bản ghi và SHA-256 của giá trị đã chuẩn hóa. Giá trị số `1.00` và `1` được coi là tương đương; chuỗi, ID và byte array vẫn so sánh chính xác. Chạy `PRAGMA integrity_check` cho từng database nghiệp vụ. Bản ghi Outbox cũ được chia theo Source, giữ nguyên trạng thái đã publish và lưu thêm archive JSON. Metadata lịch sử EF migration SQL Server không phải schema migration SQLite.

Chỉ sau khi kiểm tra thành công, thư mục staging mới được đổi thành `data/sqlite`. Nếu lỗi, giữ staging để kiểm tra, không ghi đè bản đang dùng. Không chạy ứng dụng SQLite vào bản chuyển dở. Có thể dùng `-Destination data/sqlite-trial-2` để tạo bản thử khác; khi chạy Compose cần đổi bind mount sang thư mục đó nếu muốn sử dụng.

Nếu nguồn khác mặc định, truyền `MIGRATION_SQL_CONNECTION` và `MIGRATION_NOTIFICATION_CONNECTION` qua môi trường cho container công cụ. Thư mục đích phải trống. Công cụ không tự bỏ qua lỗi kết nối hoặc tự tạo bộ dữ liệu mẫu thay dữ liệu thật.

## Vận hành và giới hạn

- Chạy một instance cho mỗi service SQLite. Không mount cùng file cho nhiều replica hoặc đặt database trên network filesystem.
- SQLite chỉ có một writer cho mỗi file; dùng transaction ngắn và timeout khóa 30 giây. WAL có thể tạo thêm file `-wal`, `-shm`.
- Outbox, Inbox, thông báo và hội thoại tăng dần; chưa có chính sách tự xóa dữ liệu lịch sử. Cần chọn thời gian lưu trước khi dùng lâu dài.
- Bản sao HTTP toàn bảng phù hợp demo nhỏ. Với dữ liệu lớn, chuyển sang API truy vấn theo ID hoặc projection theo sự kiện và watermark.
- Schema SQLite ban đầu dùng `EnsureCreated`; không chạy chuỗi migration SQL Server trên SQLite. Thay đổi schema sau này cần migration SQLite riêng và backup trước.
- Provider SQL Server và migration cũ được giữ để công cụ đọc nguồn/đối chiếu. Runtime host dùng SQLite.

## Backup và khôi phục

Dừng toàn bộ backend trước khi sao chép các thư mục `services/<Service>/data` để không bỏ sót WAL hoặc lệch trạng thái giữa service. Giữ cả auth keys, ảnh tour và cấu hình RabbitMQ nếu muốn khôi phục hệ thống đầy đủ. Không copy riêng file `.db` của một tiến trình đang ghi.

Database SQL Server gốc và image phiên bản cũ là đường quay lại dữ liệu trước chuyển đổi. Dữ liệu phát sinh sau khi chạy SQLite không tự đồng bộ ngược sang SQL Server. `compose.sqlserver.yaml` chỉ lưu cấu hình nguồn; không build code SQLite hiện tại rồi coi đó là bản ứng dụng SQL Server.

Database hiện nằm tại `services/<Service>/data/<service>.db`, ví dụ `services/Booking/data/booking.db`. Các thư mục này bị loại khỏi Git và Docker build context. Khi chạy Docker, mỗi thư mục vẫn mount vào `/data` của đúng service. Báo cáo migration cũ vẫn giữ tại `data/sqlite/migration-report.json`.

Migration mặc định chuyển dữ liệu đã kiểm chứng vào từng service. Dùng `-ExportOnly -Destination data/sqlite-trial-2` nếu chỉ muốn xuất một bản thử riêng, không cài vào service. Backup gom tám thư mục database vào cấu trúc `sqlite/<service>` trong bản backup; Restore phân phối lại vào từng service.