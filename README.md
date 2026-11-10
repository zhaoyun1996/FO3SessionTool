# FO3 Session Tool

Công cụ WinForms (.NET 10, chỉ chạy trên Windows) cho server game riêng **3Sea** (FIFA Online 3):

- Đăng nhập giống launcher (hàng chờ → token → phiên game), lưu phiên mã hóa bằng DPAPI.
- **Ép thẻ tự động**: mua thẻ +1 hoặc dùng thẻ trong kho, ép theo rule đến +10.
- **Xoay vòng (mua + ép + giao dịch chạy song song):** ở ô "Cách chạy" chọn *Xoay vòng*. Ba việc chạy cùng lúc: *Mua* thẻ +1 liên tục (chỉ khi dây chuyền còn chỗ, để không chôn EP vào thẻ chưa bán được), *Ép* ngay khi có đủ cặp thẻ theo rule, *Giao dịch* khi tổng giá trị thẻ đích (+5…+10, chưa đăng ký) của cầu thủ đạt 900 triệu đến 1 tỷ EP: đổi hết, chọn phương án có tổng lớn nhất, không giữ lại thẻ nào. Trong lúc một lô đang giao dịch, tool đã mua và ép tiếp cho lô sau. Dừng khi: hết EP, lỗ 2 lần giao dịch liên tiếp (lãi/lỗ của một vòng = EP thay đổi từ lần giao dịch trước đến lần này, cộng giá cầu thủ nhận về), phiên hết hạn, có lỗi, hoặc bấm Dừng. Chỉ có một hộp xác nhận ở đầu; mỗi giao dịch sau đó không hỏi lại và không hoàn tác. Giao dịch đang chốt thì không bị ngắt giữa chừng khi bấm Dừng. Chống vòng lặp vô tận: nếu mua thất bại liên tiếp (kho đầy chỗ) mà không còn gì để ép/đổi thì dừng; nếu 5 phút không mua, ép hay đổi được gì thì dừng và báo thẻ còn kẹt. Mã nguồn: `CycleRunner.cs`.

**Kho cầu thủ**: danh sách cầu thủ chưa đăng ký, tên đầy đủ, chọn thẻ để giao dịch.
- **Giao dịch**: đổi thẻ lấy cầu thủ + EP tiền tươi (tự chọn hoặc tự động chọn phương án lớn nhất).
- **Tìm kiếm cầu thủ** (dữ liệu từ fifaaddict).
- Lấy phiên từ game đang chạy và giữ phiên bằng ping định kỳ.

> Tool gọi API riêng của một server game không chính thức. Mọi API ở đây được suy ra từ việc bắt gói của chính game/launcher trên máy bạn. Dùng cho tài khoản của bạn, tự chịu rủi ro; các thao tác mua, ép, giao dịch tiêu EP thật và không hoàn tác.

---

## 1. Yêu cầu

| Thứ | Dùng để |
|---|---|
| Windows 10/11 | Tool và chế độ bắt gói theo tiến trình |
| [.NET 10 SDK](https://dotnet.microsoft.com/download) | Build tool |
| Python 3 + [mitmproxy](https://mitmproxy.org/) (`pip install mitmproxy`) | Bắt gói launcher/game |
| Launcher 3Sea (`3seaLauncher.exe`) và game (`fifazf.exe`) đã cài | Nguồn để bắt API |
| Quyền Administrator | Chế độ `local:<tiến trình>` của mitmproxy cần quyền này |

---

## 2. Bước đầu tiên: bắt API

Tool không dùng tài liệu API công khai nào. Bước đầu là bắt gói của launcher (để biết cách đăng nhập) và của game (để biết các API ép thẻ, mua, kho, giao dịch).

Thư mục `capture\` có sẵn:

| File | Việc |
|---|---|
| `run_capture.bat` | Bắt gói **launcher** (`3seaLauncher.exe`), ghi ra `capture_redacted.txt` |
| `redact_capture.py` | Addon mitmproxy cho launcher: che mật khẩu, token, cookie |
| `run_game_capture.bat` | Bắt gói **game** (`fifazf.exe`), ghi ra `game_capture_redacted.txt` |
| `game_capture.py` | Addon mitmproxy cho game: ghi mọi HTTP và TCP thô, che bí mật, đồng thời chia sẻ phiên cho tool (mục 6) |

Các file `.bat` giả định `mitmdump.exe` nằm ở `%APPDATA%\Python\Python315\Scripts\`. Nếu bạn cài Python ở chỗ khác, sửa đường dẫn đó trong hai file `.bat`.

### 2.1. Bắt launcher (lấy cách đăng nhập và hwid)

1. Đóng launcher nếu đang mở.
2. Mở `capture\run_capture.bat` bằng **Run as administrator** (lần đầu mitmproxy sẽ hỏi cài chứng chỉ, làm theo hướng dẫn của mitmproxy).
3. Mở launcher và **đăng nhập** (có thể phải xếp hàng).
4. Đóng cửa sổ mitmdump (Ctrl+C). Mở `capture\capture_redacted.txt`.

Trong file sẽ thấy:

- `POST https://3sea.online/web/api/launcher/login` với body `{username, password, hwid:{board,cpu,disk,id,machine}}`. Mật khẩu bị che. **`hwid` được giữ nguyên**, bạn lấy 5 giá trị này cho bước 3.
- Phản hồi `{"result":false,"code":"QUEUE","position":…,"ticket":…}` khi đang xếp hàng. Các lần hỏi lại chỉ gửi `hwid` và `ticket`. Khi tới lượt: `{"result":true,"token":…}`.
- User-Agent của launcher (ví dụ `3seaLauncher/1.0.5`).

### 2.2. Bắt game (lấy các API trong game)

1. Mở `capture\run_game_capture.bat` bằng **Run as administrator** **trước khi** mở launcher.
2. Mở launcher, vào game.
3. Làm đúng thao tác bạn muốn tool bắt chước (mua thẻ, ép thẻ, xem kho, giao dịch…).
4. Đóng mitmdump, đọc `capture\game_capture_redacted.txt`. Mỗi khối là một yêu cầu HTTP (`HTTP hh:mm:ss  GET|POST url`) kèm phản hồi; dòng `TCP …` là gói thô tới cổng 28003 (không dùng).

Lưu ý khi đọc capture:
- Thân bản ghi bị cắt khoảng 6000 ký tự, các giá trị dài hoặc nhạy cảm bị che thành `<REDACTED len=…>`.
- Nên lặp lại thao tác ở nhiều tình huống (thành công, thất bại) để thấy hết dạng phản hồi.

---

## 3. Cấu hình

Sao chép `config.example.json` thành `config.json` (đặt cạnh file `.exe`, `config.json` không được commit):

```json
{
  "launcherBase": "https://3sea.online",
  "gameBase": "http://216.236.2.193:18090/lang-vi",
  "launcherUserAgent": "3seaLauncher/1.0.5",
  "queuePollSeconds": 5,
  "hwid": {
    "board": "...", "cpu": "...", "disk": "...", "id": "...", "machine": "..."
  }
}
```

- `hwid`: 5 giá trị lấy ở bước 2.1.
- `gameBase`: địa chỉ API game, lấy từ các dòng `HTTP … http://<ip>:18090/lang-vi/...` trong `game_capture_redacted.txt`. **Địa chỉ này từng đổi** (`72.62.247.145` → `216.236.2.193`), nên khi tool báo hết thời gian chờ (timeout) khi gọi game, hãy bắt lại game và sửa `gameBase`.
- `launcherUserAgent`: lấy theo launcher bạn đang dùng.

---

## 4. Build và chạy

```powershell
dotnet build FO3SessionTool.csproj -c Debug

# bản chạy độc lập 1 file
dotnet publish FO3SessionTool.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

Chép `config.json` vào `dist\` cạnh `FO3SessionTool.exe`. Lệnh publish sẽ lỗi nếu `dist\FO3SessionTool.exe` đang chạy; hãy đóng tool trước.

Lần đầu mở: bấm **Đăng nhập**, nhập tài khoản và mật khẩu. Tool xếp hàng giống launcher, đổi token lấy phiên game, rồi lưu phiên (mã hóa DPAPI, chỉ tài khoản Windows hiện tại giải mã được) tại `%APPDATA%\FO3SessionTool\session.dat`. Lần sau tool tự khôi phục phiên nếu server còn chấp nhận. Nhật ký lưu ở `%APPDATA%\FO3SessionTool\logs\`.

---

## 5. Các API game mà tool dùng

> Định dạng yêu cầu/phản hồi chi tiết, luật ép thẻ, thuật toán và các lỗi đã gặp nằm trong [`docs/API.md`](docs/API.md).

Gốc: `{gameBase}`, cookie phiên `connect.sid` và `fo3sid`.

| API | Việc |
|---|---|
| `POST /publisher/garena/sign` `{token}` | Đổi token launcher lấy phiên game |
| `GET /account` | Kiểm tra phiên (`NOT_SIGNED` nếu hết hạn) |
| `GET /sign/ping` | Giữ phiên (game gửi mỗi khoảng 10 phút, tool làm tương tự) |
| `GET /coach/me` | Đội hình, kho cầu thủ, số EP/ES |
| `GET /transfer/search/{spid}` | Danh sách thẻ đang bán trên chợ |
| `POST /transfer/purchase/{spid}/{grade}/{price}/purchase` | Mua thẻ (giá phải khớp giá niêm yết) |
| `enhance2.5` (xem `GameApi.cs`) | Xem trước và thực hiện ép thẻ |
| `POST /players/trade2/proposal` `{players:[id…]}` | Xin phương án giao dịch (chưa đổi thẻ) |
| `POST /trade3/contract` `{selectnum, slotnum}` | Chốt phương án giao dịch, **không hoàn tác** |
| `GET /trade3/staticvalues` | Giới hạn giao dịch (mỗi lần tối đa 1 tỷ EP, ví tối đa 5 tỷ) |

Nguồn dữ liệu ngoài game:
- Ảnh cầu thủ: `http://cdn.3sea.online/cdn/externalAssets/players/p{spid}.png`.
- Tên đầy đủ, chỉ số, tìm kiếm: trang fifaaddict (dữ liệu game dạng `.big` đã mã hóa nên không đọc được).
- Cờ quốc gia: flagcdn.com.

---

## 6. Lấy phiên từ game đang chạy

Nút **Lấy phiên từ game** (thanh trên cùng) dùng lại phiên của game thật để bạn không phải xếp hàng lại:

1. Chạy `capture\run_game_capture.bat` (Administrator) rồi mở launcher và vào game.
2. Addon `game_capture.py` chỉ ghi hai cookie phiên vào `%APPDATA%\FO3SessionTool\game_cookie.dat`, mã hóa DPAPI, không ghi vào log.
3. Trong tool bấm **Lấy phiên từ game**: tool kiểm tra phiên với server, lấy tên nhân vật rồi dùng luôn.

Nếu báo chưa có phiên hoặc phiên hết hạn: chạy lại bước 1 và vào lại game.

---

## 7. Cách dùng các tab

**Ép thẻ tự động**
1. Chọn cầu thủ (gõ tên và tìm, hoặc áp dụng từ tab Tìm kiếm).
2. Chọn mức đích (+5 đến +10) và rule nguyên liệu cho từng mức.
3. Nguồn thẻ: *Mua mới* (theo số thẻ +1 hoặc theo số cầu thủ mong muốn) hoặc *Thẻ trong kho*. Đặt giá tối đa mỗi thẻ và tốc độ.
4. Bấm **Thực hiện**. Với *Mua mới*, tool mua thẻ +1, rồi quét các thẻ hợp lệ trong kho, rồi ép theo rule.

**Kho cầu thủ**: chỉ liệt kê thẻ chưa đăng ký. Tick thẻ để giao dịch; tổng giá trị đã chọn không được vượt 1 tỷ EP.

**Giao dịch**: chọn *Tự chọn kết quả* hoặc *Tự động* rồi bấm **Giao dịch**. Tool xin 3 phương án; chế độ tự chọn mở popup để chọn một phương án, chế độ tự động lấy phương án có tổng (EP + giá cầu thủ) lớn nhất. Sau cùng luôn có hộp xác nhận trước khi chốt.

---

## 8. Xử lý sự cố

| Triệu chứng | Cách xử lý |
|---|---|
| Hết thời gian chờ khi gọi game (`HttpClient.Timeout`) | Server đổi địa chỉ: bắt lại game, sửa `gameBase` |
| `NOT_SIGNED` / phiên hết hạn | Đăng nhập lại, hoặc dùng *Lấy phiên từ game* |
| Đăng nhập báo `QUEUE` rất lâu | Bình thường khi đông người; tool tự hỏi lại mỗi `queuePollSeconds` giây |
| mitmdump không bắt được gì | Chưa chạy bằng Administrator, hoặc bật mitmdump sau khi đã mở game |
| `publish` lỗi `MSB4018` | `dist\FO3SessionTool.exe` đang chạy, đóng tool |

---

## 9. Bảo mật

- `config.json` (có hwid máy), thư mục `dist/`, `bin/`, `obj/` và log bắt gói nằm trong `.gitignore`; đừng commit chúng.
- File phiên và cookie đều mã hóa DPAPI theo tài khoản Windows, không dùng được ở máy khác.
- Capture đã che mật khẩu, token, cookie; nhưng vẫn chứa tên nhân vật, ID thẻ và cấu trúc API, nên đừng đăng công khai.

---

## 10. Cấu trúc mã nguồn

| File | Nội dung |
|---|---|
| `Program.cs`, `Config.cs` | Khởi động, đọc `config.json` |
| `LauncherClient.cs`, `LoginDialog.cs` | Đăng nhập launcher (hàng chờ, token) |
| `GameApi.cs` | Toàn bộ API game, ví EP/ES, kho, mua, ép, giao dịch |
| `SessionStore.cs` | Lưu và đọc phiên (DPAPI), đọc cookie do addon chia sẻ |
| `UpgradeRunner.cs`, `UpgradeTab.cs` | Thuật toán và giao diện ép thẻ |
| `KhoTab.cs`, `TradeDialog.cs` | Kho cầu thủ và giao dịch |
| `PlayerSearch.cs`, `PlayerCard.cs` | Dữ liệu fifaaddict và thẻ thông tin cầu thủ |
| `MainForm.cs`, `Theme.cs`, `Controls.cs` | Cửa sổ chính, giao diện tối, các control tự vẽ |
| `capture/` | Script bắt gói bằng mitmproxy |
