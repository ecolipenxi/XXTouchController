# XXTouch Controller 3.8.26

## Bố cục màn hình nhỏ

- Cửa sổ hỗ trợ kích thước từ 900 × 560 và không còn bị ép vượt khỏi vùng làm việc khi Windows dùng Scale 125–150%.
- Các nhóm nút Kết nối, thao tác hàng loạt, TikTok username và TikTok Point tự xuống dòng khi thiếu chiều rộng.
- Vùng xem màn hình iPhone có thanh chia để kéo thu hẹp hoặc mở rộng.
- Danh sách thiết bị và Log vẫn giữ vùng hiển thị tối thiểu khi giảm chiều cao cửa sổ.

Ứng dụng WPF cho Windows 10/11 x64, dùng để tìm và điều khiển việc chạy một file
Lua trên các iPhone cài XXTouch Elite/Elite TS trong cùng mạng LAN.

## Khởi động

1. Bật **Remote Access** trong XXTouch Elite TS trên iPhone.
2. Đảm bảo máy tính và iPhone cùng mạng LAN.
3. Giải nén toàn bộ gói, sau đó chạy `XXTouchController.exe`.
4. Nếu Windows Firewall hỏi quyền mạng, cho phép **Private networks** để UDP discovery hoạt động.

Ứng dụng là bản self-contained, máy đích không cần cài .NET.

Khi nâng cấp, có thể chép file `XXTouchController.exe` mới đè lên file cũ. Gói cập
nhật không chứa `config\devices.json`, vì vậy danh sách thiết bị hiện có không bị ghi đè.

## Chọn và xóa thiết bị

- Ô **Tìm ID / STT / IP / tên / username** lọc ngay danh sách theo toàn bộ các trường
  này. Khi đang lọc, nút **Chọn tất cả** chỉ đánh dấu các dòng đang hiển thị.
- Agent 3.0 cung cấp ID thiết bị ổn định trong `/deviceinfo`; ID được hiển thị thành một
  cột riêng để có thể tìm và đối chiếu các iPhone trùng tên. Trong bảng, ID dài được
  rút gọn theo dạng `B35A34A5…CAD6`; rê chuột lên ID để xem đầy đủ. Ô tìm kiếm vẫn
  đối chiếu với toàn bộ ID gốc.
- Tick cột **Chọn** rồi dùng Start/Stop/Kiểm tra/Cập nhật Agent để thao tác đúng toàn bộ
  thiết bị đã đánh dấu.
- Nút **Xóa thiết bị đã chọn** xóa tất cả dòng đã tick trong một lần. Nếu chưa tick dòng
  nào, nút sẽ xóa riêng dòng đang được bôi.
- Danh sách được lưu tuần tự và thay file an toàn, tránh mất thao tác xóa khi quét hoặc
  kiểm tra thiết bị đang chạy song song.

## Cập nhật Agent qua LAN

- Agent 0.8 trở lên nhận trực tiếp URL TIPA từ nút **Cập nhật Agent...**.
- Agent 0.6/0.7 bị từ chối và được ghi rõ trong Log. Controller không tự mở Safari
  hoặc Magnifier, tránh điều khiển nhầm ứng dụng khi URL Scheme chưa được bật.
- TrollStore phải bật **URL Scheme** và TrollVNC phải bật VNC ở cổng `5901`.
- Controller mở trang cập nhật rồi tự bấm **Install** trực tiếp qua VNC. Nếu VNC không
  bấm được, Controller mới dùng Lua/OCR làm phương án dự phòng.
- Agent 1.6 không tự khởi động lại sau khi cài. Nếu thiết bị chuyển Offline, hãy mở
  TrollVNC hoặc bấm **Apply** trên iPhone để Agent hoạt động lại.
- Hãy thử một vài máy trước rồi mới chạy theo lô. Thông báo thành công của Controller
  xác nhận đã gửi cập nhật và bấm Install, không xác nhận Agent đã tự khởi động lại.

## Phân tích và khắc phục lỗi bằng AI

Tab **Phân tích lỗi AI** chỉ dùng OpenAI API để đọc log, Lua đang chọn và ảnh màn hình
khi người dùng chủ động bật tùy chọn gửi ảnh. AI không có công cụ Start/Stop, không được
điều khiển thiết bị và không thể chạy PowerShell hay lệnh hệ thống.

1. Tạo OpenAI API key trên tài khoản OpenAI API của bạn.
2. Nhập key rồi bấm **Lưu API key**. Key được lưu trong Windows Credential Manager,
   không ghi vào `settings.json`, EXE hoặc gói ZIP.
3. Nên giữ bật **Che IP và địa chỉ mạng trước khi gửi**.
4. Chọn file Lua, để ứng dụng thu thập log lỗi rồi bấm **Phân tích lỗi**.
5. Đọc báo cáo. Nếu AI tạo Lua sửa, dùng **Lưu bản sửa thành file mới...**.

Ứng dụng không ghi đè Lua gốc và không tự chạy Lua đã sửa. OpenAI API cần Internet,
có hạn mức và thanh toán tách biệt với gói ChatGPT. Chỉ tối đa 150 log gần nhất,
100.000 ký tự Lua và một snapshot JPEG tối đa 5 MB được đưa vào một lần phân tích.

## Sử dụng

- **Tự quét thiết bị**: trước tiên gửi UDP broadcast chính thức tới cổng `46953`.
  Nếu LAN proxy chặn broadcast, Controller tự lấy subnet của card mạng có gateway
  và dò cổng `46952` song song. Mỗi subnet được giới hạn tối đa 4.094 host (`/20`).
- Cổng TCP mở chỉ là ứng viên. Controller chỉ thêm dòng khi `/deviceinfo` trả về
  ID hoặc phiên bản Agent hợp lệ; phản hồi từ LAN proxy, máy ảo và dịch vụ khác
  bị loại bỏ và được đếm riêng trong trạng thái quét.
- Sau khi xác minh HTTP, Controller tự dọn các dòng ảo do bản 3.8.22 đã lưu: chỉ
  xóa dòng đang Offline, mang tên mặc định `iPhone` và hoàn toàn không có ID,
  thông tin thiết bị, phiên bản Agent hay dữ liệu người dùng. Việc dọn chỉ chạy
  khi lần quét hiện tại đã tìm thấy ít nhất một Agent thật.
- Nếu quét bị firewall chặn, nhập IP và cổng `46952`, rồi bấm **Thêm thiết bị**.
- Bấm **Chọn file** để chọn đúng một file `.lua` UTF-8.
- Đánh dấu thiết bị, sau đó dùng Start/Stop/Kiểm tra hàng loạt; hoặc dùng nút trên từng dòng.
- Nút **Stop** luôn bấm được và vẫn gửi `/recycle` khi trạng thái Online trên Windows bị chậm. Controller xác nhận dừng qua cả kênh nền lẫn `/health`; nếu chưa xác nhận được thì nút Stop không bị khóa và có thể bấm lại.
- Stop hủy luôn hàng đợi **Lặp Lua** của đúng thiết bị. Lua đã dừng sẽ không bị Controller tự Start lại ở lượt kế tiếp.
- Cột trạng thái hiển thị tiến độ lặp riêng của từng máy, ví dụ
  `Đang chạy 00:02:15 · Vòng 2/5`; khi dừng hoặc lỗi vẫn giữ lại vòng cuối.
- **Đồng thời** đặt số thiết bị được Start trong cùng một lượt.
- **Delay mỗi lượt (giây)** đặt thời gian mỗi vị trí chạy chờ trước khi nhận thiết bị kế tiếp.
- **Lọc máy sẵn sàng** đọc trạng thái mới nhất của toàn bộ danh sách và chỉ đánh dấu
  máy đồng thời sáng màn hình, đã mở khóa và đang ở Home. Controller xác nhận hai mẫu
  cách nhau 450 ms để tránh nhận nhầm lúc ứng dụng đang chuyển cảnh.
- Bật **Chỉ chạy máy sáng ở Home** để lọc lại ngay trước mỗi lệnh Start. Máy không đạt
  `home_ready=true`, máy mất liên lạc và máy dùng Agent cũ không vào hàng đợi, không
  được tính vào tổng tiến độ và không được tính số vòng Lua.
- Tính năng lọc Home tin cậy cần **LuaAgent 3.0**. Agent 3.0 đối chiếu trạng thái màn hình
  qua FrontBoard, trạng thái Darwin của SpringBoard và SpringBoardServices rồi trả `screen_on`, `locked`,
  `frontmost_app` và `home_ready` qua
  `/deviceinfo`. Controller vẫn dùng được với Agent cũ khi tắt checkbox; cột
  **Sẵn sàng Home** sẽ báo rõ `Cần Agent 3.0` thay vì báo nhầm màn hình tắt.
- **Xem màn hình** tải JPEG bằng endpoint `/snapshot`; ảnh không nhận thao tác chuột.
- Khi Lua đang chạy, cột trạng thái hiển thị `Đang chạy HH:MM:SS` và tăng mỗi giây.
- Controller chỉ hỏi trạng thái các thiết bị đang chạy theo chu kỳ 2 giây. Khi Lua kết thúc,
  trạng thái tự chuyển thành `Đã dừng HH:MM:SS` và giữ lại tổng thời gian chạy.
- Khi Start từ Windows, đồng hồ được gắn với `run_id` vừa nhận và tính theo đồng hồ của
  Windows. Thời điểm chạy cũ hoặc giờ hệ thống trên iPhone không được ghi đè bộ đếm này.
- Cấu hình và danh sách thiết bị nằm trong thư mục `config`.

## Lấy TikTok username

1. Chỉ bật màn hình những iPhone cần kiểm tra, mở khóa và đăng nhập TikTok sẵn.
2. Tick cột **Chọn**, rồi bấm **Lấy username thiết bị đã chọn**.
3. Controller tự bỏ qua máy Offline, màn hình tắt và Agent đang chạy Lua khác; không
   đánh thức máy và không dừng tác vụ đang có.
4. Mọi nút TikTok/iOS đều chỉ được bấm sau khi OCR xác nhận trạng thái và
   `screen.find_image()` tìm thấy đúng ảnh. Popup lạ không bị bấm đoán; Controller lưu
   snapshot vào `Ket-qua-username\snapshots` để kiểm tra.
5. Kết quả được ghi ngay vào danh sách thiết bị và
   `Ket-qua-username\TikTok-usernames-latest.csv`. Sau mỗi lượt có thêm một CSV mang
   thời gian; nút **Xuất username CSV...** cho phép xuất lại thiết bị đã chọn, hoặc toàn
   bộ danh sách khi chưa chọn máy nào.

Lua tích hợp xác minh cùng một chuỗi `@username` nhiều lần và đọc mới thêm hai lần
trước khi gửi `USERNAME_FOUND` về Windows. Vì vậy tên hiển thị, mô tả và username của
video khác không được chấp nhận làm kết quả.

Controller chỉ tải snapshot của thiết bị đang xem. Danh sách được ảo hóa và việc
xác minh thiết bị dùng tối đa 32 kết nối song song để phục vụ danh sách 100–150 máy.
Với số lượng lớn, nên tắt diagnostics, đặt chu kỳ quét từ 10 giây trở lên và bắt
đầu với mức đồng thời 5–10.

### Kết quả kiểm thử thực tế

- Đã thử thành công trên 9 thiết bị chỉ định và thêm 12 thiết bị chọn ngẫu nhiên: tổng cộng 21/21
  thiết bị lấy được username, không có runtime error trong hai lượt đầy đủ.
- Sau lượt chạy thực tế 116 máy, 43 máy lỗi ban đầu đã được thử lại bằng bản sửa: 42/43 máy
  thành công. Tổng kết hiện tại là 115/116 máy lấy được username. Máy còn lại có TikTok tự crash
  về Home mỗi lần mở Profile dù đã tự khởi động lại hai lần; đây không phải popup chưa hỗ trợ.
- Luồng đã được kiểm tra với Profile tiếng Anh/tiếng Nhật, TikTok đang mở sẵn, màn hình chọn ảnh,
  hộp thoại lưu đăng nhập, hai dạng Find contacts, Quick security checkup, yêu cầu danh bạ,
  Settings, iOS Home và popup TikTok.
- Bản 3.8.20 bổ sung hai mẫu Profile nền sáng (tiếng Anh và tiếng Nhật). Lua thử song song các
  mẫu nền tối/nền sáng trong vùng tab Profile, nên video sáng không còn làm mất nút Profile.
- Nút chỉ được bấm khi OCR xác nhận đúng ngữ cảnh và `screen.find_image()` khớp ảnh ở ngưỡng an
  toàn; không dùng tọa độ cố định để đoán vị trí popup.

## OpenAPI được sử dụng

- `POST /deviceinfo`
- `POST /spawn` với body `text/plain; charset=utf-8`
- `GET /is_running` (đối chiếu; trạng thái chính lấy từ `deviceinfo.data.is_running`)
- `POST /recycle`
- `GET /snapshot?ext=jpeg&compress=0.8&orient=0`

## TikTok Point (Controller 3.8.25 + LuaAgent 3.1)

- Agent 3.1 thêm `clipboard.get()`, `clipboard.set(text)` và `clipboard.clear()` cho Lua.
  Clipboard không có endpoint HTTP riêng; Controller không thể đọc tùy ý nội dung clipboard của iPhone.
- Lua gửi đúng dữ liệu cần thiết bằng `point.report(event, detail)`. Controller nhận các sự kiện
  `START`, `BALANCE`, `PLAN`, `STATUS`, `LINK`, `READY`, `SKIP` và `ERROR` qua `/logs`.
- Controller hiển thị số Point, mức dự kiến, trạng thái và link công khai; kết quả được lưu tại
  `Ket-qua-TikTok-Point\TikTok-Point-latest.csv` và có thể xuất lại bằng nút **Xuất Point CSV...**.
- Link chỉ được chấp nhận khi là URL tuyệt đối `http` hoặc `https`, không chứa user-info và có host hợp lệ.
- Luồng an toàn phải phát `READY` rồi dừng trước thao tác xác nhận rút cuối. Controller chỉ thu kết quả,
  không tự bấm nút xác nhận giao dịch.

Quy tắc mức dự kiến được đặt trong Lua: dưới 20.000 thì bỏ qua; 20.000/50.000/100.000/150.000
theo ngưỡng tương ứng; lớn hơn 150.000 dùng toàn bộ số Point còn lại.

Ứng dụng không tự retry `/spawn` và không dựng máy chủ giả. Dò subnet không chạy tuần tự:
tối đa 256 kết nối TCP được kiểm tra song song, sau đó chỉ IP mở cổng `46952` mới được
xác minh qua `/deviceinfo`.

## Build từ mã nguồn

Yêu cầu .NET 8 SDK:

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true
```

Hoặc chạy `publish-win-x64.ps1`.

## Xử lý sự cố

- Web Manager phải mở được tại `http://IP_IPHONE:46952/`.
- Nếu thiết bị Offline, kiểm tra cùng lớp mạng, Remote Access và Windows Firewall.
- IP DHCP có thể đổi; thử tên Bonjour như `iPhone.local` hoặc quét lại.
- Log diagnostics hiển thị URL, HTTP status, Content-Type, thời gian phản hồi và exception;
  không ghi mã bản quyền.
