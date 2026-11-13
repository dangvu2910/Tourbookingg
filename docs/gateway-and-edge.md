# Gateway trong stack hybrid

- `gateway`: API Gateway Kong (`kong:3.9.1`), đọc `infrastructure/kong/kong.hybrid.yml`. Là **điểm vào public duy nhất** local; chặn `/internal`, `/service-info`, `/metrics`.
- `web-router`: ứng dụng YARP (source `services/Gateway`), giữ các route MVC hiện có và phục vụ trang chủ tĩnh.
- Không còn service `edge` (HAProxy/Nginx). Local chỉ chạy một container mỗi service nên không cần load balancer.

Luồng truy cập local: trình duyệt → Kong → service API, hoặc Kong → web-router → service MVC.

| Thành phần | Cổng trong container | Cổng truy cập từ máy |
| --- | --- | --- |
| gateway/Kong proxy | 8000 | `127.0.0.1:${HTTP_PORT:-8080}`; stack test dùng 18083 |
| gateway/Kong status | 8100 | Chỉ nội bộ (health check) |
| web-router/YARP | 8080 | Chỉ nội bộ Docker |
| identity, catalog, booking, customer, notification | 8080 | Chỉ nội bộ Docker, gọi nhau bằng `http://<service>:8080` |

Tên container do Compose tạo, ví dụ `tourbooking-live-gateway-1`. Không dùng container_name cố định.

Chỉ có một cấu hình Kong là `kong.hybrid.yml` (stack SQLite cũ không dùng Kong và đã bỏ).
