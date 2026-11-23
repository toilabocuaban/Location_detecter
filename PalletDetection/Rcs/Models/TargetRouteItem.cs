using Newtonsoft.Json;
using System.Collections.Generic;

namespace PalletDetection.Rcs.Models
{
    /// <summary>
    /// Node trong mảng targetRoute xác định lộ trình di chuyển của AMR theo tài liệu RCS-2000 V4.2.0 (Mục 2.1.2 Apply Task).
    /// </summary>
    public class TargetRouteItem
    {
        public const string DefaultType = "SITE";

        /// <summary>
        /// Thứ tự chặng lộ trình, bắt đầu từ 0 (bắt buộc).
        /// </summary>
        [JsonProperty("seq")]
        public int Seq { get; set; }

        /// <summary>
        /// Loại đích đến (bắt buộc, tối đa 16 ký tự). Mặc định "SITE" (điểm trên bản đồ).
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; } = DefaultType;

        /// <summary>
        /// Mã đích đến tương ứng với type (bắt buộc, tối đa 256 ký tự).
        /// </summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>
        /// Thao tác tại điểm đến: COLLECT, DELIVERY, ROTATE (tùy chọn, không gửi nếu null).
        /// </summary>
        [JsonProperty("operation", NullValueHandling = NullValueHandling.Ignore)]
        public string Operation { get; set; }

        /// <summary>
        /// Phạm vi chọn AMR cho riêng chặng này: GROUPS hoặc ROBOTS (tùy chọn).
        /// </summary>
        [JsonProperty("robotType", NullValueHandling = NullValueHandling.Ignore)]
        public string RobotType { get; set; }

        /// <summary>
        /// Danh sách mã AMR hoặc nhóm AMR khớp với robotType (tùy chọn).
        /// </summary>
        [JsonProperty("robotCode", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> RobotCode { get; set; }

        public TargetRouteItem()
        {
        }

        public TargetRouteItem(int seq, string code, string type = DefaultType)
        {
            Seq = seq;
            Type = string.IsNullOrWhiteSpace(type) ? DefaultType : type;
            Code = code;
        }

        public override string ToString()
        {
            return $"[{Seq}] {Type}:{Code}";
        }
    }
}
