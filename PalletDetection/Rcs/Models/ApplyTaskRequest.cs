using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PalletDetection.Rcs.Models
{
    /// <summary>
    /// Mô hình dữ liệu Request tạo task cho AMR theo RCS-2000 V4.2.0 (Mục 2.1.2 Apply Task: /rcs/rtas/api/robot/controller/task/submit).
    /// </summary>
    public class ApplyTaskRequest
    {
        /// <summary>
        /// Loại task cấu hình trên RCS-2000 (bắt buộc, VD: QF2, TRANSPORT).
        /// </summary>
        [JsonProperty("taskType")]
        public string TaskType { get; set; }

        /// <summary>
        /// Danh sách các chặng lộ trình của task (bắt buộc, tối thiểu 1 chặng).
        /// </summary>
        [JsonProperty("targetRoute")]
        public List<TargetRouteItem> TargetRoute { get; set; } = new List<TargetRouteItem>();

        /// <summary>
        /// Độ ưu tiên ban đầu của task từ 1 đến 120 (tùy chọn).
        /// </summary>
        [JsonProperty("initPriority", NullValueHandling = NullValueHandling.Ignore)]
        public int? InitPriority { get; set; }

        /// <summary>
        /// Hạn hoàn thành task (tùy chọn).
        /// </summary>
        [JsonProperty("deadline", NullValueHandling = NullValueHandling.Ignore)]
        public string Deadline { get; set; }

        /// <summary>
        /// Kiểu chọn AMR: GROUPS hoặc ROBOTS (tùy chọn; để null nếu để RCS tự chọn xe).
        /// </summary>
        [JsonProperty("robotType", NullValueHandling = NullValueHandling.Ignore)]
        public string RobotType { get; set; }

        /// <summary>
        /// Danh sách mã AMR hoặc nhóm AMR khớp với robotType (tùy chọn).
        /// </summary>
        [JsonProperty("robotCode", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> RobotCode { get; set; }

        /// <summary>
        /// Cho phép ngắt task hay không: 0 hoặc 1 (tùy chọn).
        /// </summary>
        [JsonProperty("interrupt", NullValueHandling = NullValueHandling.Ignore)]
        public int? Interrupt { get; set; }

        /// <summary>
        /// Mã task bên ngoài tự chỉ định (tùy chọn, tối đa 64 ký tự; rỗng thì RCS tự sinh).
        /// </summary>
        [JsonProperty("robotTaskCode", NullValueHandling = NullValueHandling.Ignore)]
        public string RobotTaskCode { get; set; }

        /// <summary>
        /// Mã nhóm task (tùy chọn, tối đa 32 ký tự).
        /// </summary>
        [JsonProperty("groupCode", NullValueHandling = NullValueHandling.Ignore)]
        public string GroupCode { get; set; }

        /// <summary>
        /// Dữ liệu mở rộng tùy biến (tùy chọn).
        /// </summary>
        [JsonProperty("extra", NullValueHandling = NullValueHandling.Ignore)]
        public object Extra { get; set; }

        /// <summary>
        /// Kiểm tra tính hợp lệ các trường theo đặc tả RCS-2000 V4.2.0 Mục 2.1.2.
        /// </summary>
        public void Validate()
        {
            string taskType = (TaskType ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(taskType) || taskType.Length > 32)
                throw new ArgumentException("TaskType bắt buộc nhập và tối đa 32 ký tự.", nameof(TaskType));

            if (TargetRoute == null || TargetRoute.Count == 0)
                throw new ArgumentException("TargetRoute phải có ít nhất 1 chặng lộ trình.", nameof(TargetRoute));

            for (int i = 0; i < TargetRoute.Count; i++)
            {
                TargetRouteItem item = TargetRoute[i];
                if (item == null)
                    throw new ArgumentException($"Phần tử TargetRoute[{i}] không được null.");
                if (item.Seq < 0)
                    throw new ArgumentException($"TargetRoute[{i}].Seq phải >= 0.");

                string routeType = (item.Type ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(routeType) || routeType.Length > 16)
                    throw new ArgumentException($"TargetRoute[{i}].Type bắt buộc nhập và tối đa 16 ký tự.");

                string routeCode = (item.Code ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(routeCode) || routeCode.Length > 256)
                    throw new ArgumentException($"TargetRoute[{i}].Code bắt buộc nhập và tối đa 256 ký tự.");
            }

            if (InitPriority.HasValue && (InitPriority.Value < 1 || InitPriority.Value > 120))
                throw new ArgumentException("InitPriority nếu có phải nằm trong khoảng 1 đến 120.", nameof(InitPriority));

            if (Interrupt.HasValue && Interrupt.Value != 0 && Interrupt.Value != 1)
                throw new ArgumentException("Interrupt nếu có chỉ nhận 0 hoặc 1.", nameof(Interrupt));

            string robotType = (RobotType ?? string.Empty).Trim();
            bool hasRobotCodes = RobotCode != null && RobotCode.Any(c => !string.IsNullOrWhiteSpace(c));

            if (!string.IsNullOrEmpty(robotType) &&
                !string.Equals(robotType, "ROBOTS", StringComparison.Ordinal) &&
                !string.Equals(robotType, "GROUPS", StringComparison.Ordinal))
            {
                throw new ArgumentException("RobotType chỉ nhận 'ROBOTS' hoặc 'GROUPS'.", nameof(RobotType));
            }

            if (!string.IsNullOrEmpty(robotType) && !hasRobotCodes)
                throw new ArgumentException("Khi chỉ định RobotType, RobotCode phải có ít nhất 1 phần tử.", nameof(RobotCode));

            if (string.IsNullOrEmpty(robotType) && hasRobotCodes)
                throw new ArgumentException("Khi có RobotCode, phải chỉ định RobotType ('ROBOTS' hoặc 'GROUPS').", nameof(RobotType));
        }
    }
}
