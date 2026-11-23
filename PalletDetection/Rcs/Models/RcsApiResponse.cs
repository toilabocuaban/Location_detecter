using Newtonsoft.Json;
using System;

namespace PalletDetection.Rcs.Models
{
    /// <summary>
    /// Phần data trả về trong response của API Apply Task RCS-2000 V4.2.0 (Mục 2.1.2).
    /// </summary>
    public class RcsApiResponseData
    {
        [JsonProperty("robotTaskCode")]
        public string RobotTaskCode { get; set; }

        [JsonProperty("extra")]
        public object Extra { get; set; }
    }

    /// <summary>
    /// Mô hình dữ liệu Response trả về từ Hikrobot RCS-2000 V4.2.0 (Chapter 1 & Section 2.1.2).
    /// </summary>
    public class RcsApiResponse
    {
        /// <summary>
        /// Mã kết quả: "SUCCESS" là thành công; các giá trị khác (Err_*) là lỗi nghiệp vụ.
        /// </summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>
        /// Thông báo kết quả ("succeeded" hoặc mô tả lỗi nghiệp vụ).
        /// </summary>
        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>
        /// Mã định danh request tương ứng từ response header X-lr-request-id của RCS-2000 V4.2.0.
        /// </summary>
        [JsonIgnore]
        public string ReqCode { get; set; }

        /// <summary>
        /// Dữ liệu nghiệp vụ trả về từ RCS-2000 V4.2.0, chứa robotTaskCode khi thành công.
        /// </summary>
        [JsonProperty("data")]
        public RcsApiResponseData Data { get; set; }

        /// <summary>
        /// Kiểm tra kết quả nghiệp vụ thành công (code == "SUCCESS").
        /// </summary>
        [JsonIgnore]
        public bool IsSuccess => string.Equals(Code, "SUCCESS", StringComparison.Ordinal);

        /// <summary>
        /// Mã Task ID (robotTaskCode) được tạo bởi RCS-2000 V4.2.0 nếu thành công.
        /// </summary>
        [JsonIgnore]
        public string TaskCode => IsSuccess ? Data?.RobotTaskCode : null;

        public override string ToString()
        {
            return $"[RcsApiResponse: Code={Code}, Message={Message}, RequestId={ReqCode}, RobotTaskCode={Data?.RobotTaskCode}]";
        }
    }
}
