using System;

namespace PalletDetection.Rcs.Models
{
    /// <summary>
    /// Phân loại trạng thái kết quả của một lần gọi API tới RCS-2000.
    /// </summary>
    public enum RcsCallStatus
    {
        /// <summary>
        /// Thành công toàn diện: Kết nối được, HTTP 200, và RCS trả code == "SUCCESS".
        /// </summary>
        Success,

        /// <summary>
        /// Lỗi nghiệp vụ từ RCS-2000: Kết nối và HTTP 200 thành công, nhưng RCS trả về code != "SUCCESS".
        /// </summary>
        BusinessError,

        /// <summary>
        /// Lỗi máy chủ HTTP: Server trả mã khác 200 (ví dụ: 404, 500, 502...).
        /// </summary>
        HttpError,

        /// <summary>
        /// Lỗi mạng: Không kết nối được đến máy chủ (sai IP/port, đứt mạng, từ chối kết nối).
        /// </summary>
        NetworkError,

        /// <summary>
        /// Lỗi quá thời gian chờ (Timeout): Server không kịp phản hồi trong thời gian cấu hình.
        /// Lưu ý: KHÔNG tự động retry để tránh tạo trùng task.
        /// </summary>
        Timeout,

        /// <summary>
        /// Lỗi phân tích cú pháp JSON: Phản hồi không đúng định dạng JSON hoặc sai schema.
        /// </summary>
        JsonParseError
    }

    /// <summary>
    /// Đối tượng đóng gói kết quả gọi API RCS-2000 giúp caller phân biệt chính xác bản chất kết quả và lỗi.
    /// </summary>
    public class RcsCallResult
    {
        /// <summary>
        /// True nếu thành công trọn vẹn cả tầng hạ tầng lẫn nghiệp vụ (Status == Success).
        /// </summary>
        public bool IsSuccess => Status == RcsCallStatus.Success;

        /// <summary>
        /// Trạng thái phân loại chi tiết của lần gọi API.
        /// </summary>
        public RcsCallStatus Status { get; set; }

        /// <summary>
        /// Mã trạng thái HTTP trả về từ máy chủ (nếu có).
        /// </summary>
        public int? HttpStatusCode { get; set; }

        /// <summary>
        /// Thông báo lỗi mô tả chi tiết cho người vận hành/hệ thống.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Đối tượng response chuẩn được phân tích từ JSON của RCS-2000 (nếu parse thành công).
        /// </summary>
        public RcsApiResponse Response { get; set; }

        /// <summary>
        /// Chuỗi response thô nhận được từ server (phục vụ đối soát và gỡ lỗi).
        /// </summary>
        public string RawResponse { get; set; }

        /// <summary>
        /// Thời gian phản hồi tính bằng mili-giây (ms).
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// Mã reqCode gửi đi trong request.
        /// </summary>
        public string SentReqCode { get; set; }

        /// <summary>
        /// Kiểm tra xem reqCode trong response có khớp với reqCode đã gửi đi hay không.
        /// </summary>
        public bool IsReqCodeMatched
        {
            get
            {
                if (string.IsNullOrEmpty(SentReqCode) || Response == null || string.IsNullOrEmpty(Response.ReqCode))
                    return false;
                return string.Equals(SentReqCode, Response.ReqCode, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Mã task được tạo ra nếu gọi thành công.
        /// </summary>
        public string CreatedTaskCode => IsSuccess ? Response?.TaskCode : null;

        public static RcsCallResult CreateSuccess(RcsApiResponse response, string rawResponse, string reqCode, long durationMs)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.Success,
                HttpStatusCode = 200,
                Response = response,
                RawResponse = rawResponse,
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public static RcsCallResult CreateBusinessError(RcsApiResponse response, string rawResponse, string reqCode, long durationMs)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.BusinessError,
                HttpStatusCode = 200,
                ErrorMessage = $"RCS Business Error [Code: {response?.Code}]: {response?.Message}",
                Response = response,
                RawResponse = rawResponse,
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public static RcsCallResult CreateHttpError(int httpStatusCode, string rawResponse, string reqCode, long durationMs, string message = null)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.HttpError,
                HttpStatusCode = httpStatusCode,
                ErrorMessage = message ?? $"HTTP Error {httpStatusCode}",
                RawResponse = rawResponse,
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public static RcsCallResult CreateNetworkError(string errorMessage, string reqCode, long durationMs)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.NetworkError,
                ErrorMessage = $"Network Error: {errorMessage}",
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public static RcsCallResult CreateTimeout(string reqCode, long durationMs, string message = null)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.Timeout,
                ErrorMessage = message ?? $"Request timed out after {durationMs} ms. No retry performed to prevent duplicate task generation.",
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public static RcsCallResult CreateParseError(string errorMessage, string rawResponse, int httpStatusCode, string reqCode, long durationMs)
        {
            return new RcsCallResult
            {
                Status = RcsCallStatus.JsonParseError,
                HttpStatusCode = httpStatusCode,
                ErrorMessage = $"JSON Parse Error: {errorMessage}",
                RawResponse = rawResponse,
                SentReqCode = reqCode,
                DurationMs = durationMs
            };
        }

        public override string ToString()
        {
            return $"[RcsCallResult: Status={Status}, Http={HttpStatusCode}, Duration={DurationMs}ms, Success={IsSuccess}, Err={ErrorMessage}]";
        }
    }
}
