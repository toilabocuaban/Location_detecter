using PalletDetection.Models;
using PalletDetection.Rcs.Client;
using PalletDetection.Rcs.Config;
using PalletDetection.Rcs.Logging;
using PalletDetection.Rcs.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace PalletDetection.Rcs.Automation
{
    /// <summary>
    /// Các trạng thái trong một chu kỳ tự động phát hiện thùng hàng, người và gửi task đến RCS-2000.
    /// </summary>
    public enum RcsCycleState
    {
        /// <summary>
        /// Chưa phát hiện thùng hàng trong khung hình.
        /// </summary>
        WaitingForBox,

        /// <summary>
        /// Đã phát hiện thùng hàng, đang đếm thời gian liên tục 2 giây để xác nhận ổn định.
        /// </summary>
        BoxStabilizing,

        /// <summary>
        /// Thùng hàng đã ổn định, đang theo dõi người rời đi liên tục 1 giây.
        /// </summary>
        WaitingForHumanDeparture,

        /// <summary>
        /// Đã đủ điều kiện và đã phát lệnh gửi task RCS cho chu kỳ này (khóa gửi lặp lại).
        /// </summary>
        TaskTriggered,

        /// <summary>
        /// Task đã được gửi, đang chờ thùng hàng hiện tại được dọn đi để bắt đầu chu kỳ mới.
        /// </summary>
        WaitingForBoxClear
    }

    /// <summary>
    /// Quản lý máy trạng thái (State Machine) tự động gửi task đến RCS-2000 dựa trên kết quả YOLO.
    /// Đảm bảo nghiêm ngặt:
    /// 1. Thùng hàng xuất hiện liên tục 2.0s mới tính là ổn định.
    /// 2. Người phải vắng mặt liên tục 1.0s sau khi thùng ổn định.
    /// 3. Mỗi chu kỳ chỉ gửi duy nhất 1 task, không gửi lặp lại.
    /// 4. Không tự động retry khi lỗi/timeout để tránh tạo trùng task.
    /// 5. Chỉ reset chu kỳ khi thùng hàng hiện tại biến mất và sau đó xuất hiện thùng mới.
    /// </summary>
    public class RcsTaskCycleManager : IDisposable
    {
        private readonly object _stateLock = new object();
        private readonly RcsApiConfig _config;
        private readonly IRcsLogger _logger;
        private readonly RcsV4ApiClient _apiClient;

        public IRcsLogger Logger => _logger;
        public RcsApiConfig Config => _config;

        /// <summary>
        /// Cap nhat nhanh 2 vi tri Pick-up va Drop-off tu giao dien.
        /// </summary>
        public void UpdatePositions(string pickupPosition, string dropoffPosition)
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrWhiteSpace(pickupPosition))
                    _config.PickupPosition = pickupPosition.Trim();
                if (!string.IsNullOrWhiteSpace(dropoffPosition))
                    _config.DropoffPosition = dropoffPosition.Trim();
            }
        }

        /// <summary>
        /// Cập nhật đầy đủ cấu hình Apply Task RCS-2000 V4.2.0 từ giao diện Admin.
        /// </summary>
        public void UpdateTaskConfig(string rcsUrl, string taskType, string robotCode, string pickupPosition, string dropoffPosition)
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrWhiteSpace(rcsUrl))
                    _config.SetFullOrBaseUrl(rcsUrl);
                if (!string.IsNullOrWhiteSpace(taskType))
                    _config.TaskType = taskType.Trim();
                _config.RobotCode = robotCode == null ? string.Empty : robotCode.Trim();
                if (!string.IsNullOrWhiteSpace(pickupPosition))
                    _config.PickupPosition = pickupPosition.Trim();
                if (!string.IsNullOrWhiteSpace(dropoffPosition))
                    _config.DropoffPosition = dropoffPosition.Trim();
            }
        }

        // Các ngưỡng thời gian cấu hình
        public double BoxStableDurationSeconds { get; set; } = 2.0;
        public double HumanAbsentDurationSeconds { get; set; } = 1.0;
        public double BoxClearToleranceSeconds { get; set; } = 0.5;

        // Trạng thái chu kỳ
        public RcsCycleState State { get; private set; } = RcsCycleState.WaitingForBox;
        public bool HasSentTaskInCycle { get; private set; } = false;
        public bool IsSendingApi { get; private set; } = false;
        public string LastTaskCode { get; private set; }
        public string LastResultSummary { get; private set; }

        // Các mốc thời gian theo dõi
        private DateTime? _boxFirstDetectedTime;
        private DateTime? _humanAbsentStartTime;
        private DateTime? _boxAbsentStartTime;

        // Event thông báo khi trạng thái thay đổi
        public event Action<RcsCycleState, string> OnStateChanged;

        /// <summary>
        /// Phát ra sau khi hoàn tất một lần gọi API Apply Task tới RCS-2000 (kèm vị trí Pick-up và Drop-off).
        /// </summary>
        public event Action<RcsCallResult, string, string> OnTaskCompleted;

        public RcsTaskCycleManager() : this(null, null)
        {
        }

        public RcsTaskCycleManager(RcsApiConfig config = null, IRcsLogger logger = null)
            : this(config, logger, null)
        {
        }

        public RcsTaskCycleManager(RcsApiConfig config, IRcsLogger logger, HttpMessageHandler handler)
        {
            _config = config ?? RcsApiConfig.LoadOrCreateDefault();
            _logger = logger ?? new RcsLogger(configuredToken: _config.TokenCode);
            _apiClient = new RcsV4ApiClient(_config, _logger, handler);
        }

        /// <summary>
        /// Xử lý danh sách detection của frame hiện tại trong vòng lặp Real Time.
        /// </summary>
        public void ProcessFrame(List<DetectionResult> detections, DateTime? frameTime = null)
        {
            DateTime now = frameTime ?? DateTime.UtcNow;

            bool hasBox = detections != null && detections.Any(d => IsBox(d));
            bool hasHuman = detections != null && detections.Any(d => IsHuman(d));

            string statusMsg = null;
            bool stateChanged = false;
            RcsCycleState prevState;

            lock (_stateLock)
            {
                prevState = State;

                switch (State)
                {
                    case RcsCycleState.WaitingForBox:
                        if (hasBox)
                        {
                            _boxFirstDetectedTime = now;
                            _humanAbsentStartTime = null;
                            State = RcsCycleState.BoxStabilizing;
                            stateChanged = true;
                            _logger.LogInfo("[CYCLE] Phát hiện thùng hàng. Bắt đầu đếm thời gian ổn định (2.0s)...");
                        }
                        break;

                    case RcsCycleState.BoxStabilizing:
                        if (!hasBox)
                        {
                            // Mất thùng trước khi đủ 2 giây -> Reset
                            _boxFirstDetectedTime = null;
                            State = RcsCycleState.WaitingForBox;
                            stateChanged = true;
                            _logger.LogInfo("[CYCLE] Thùng hàng biến mất trước khi đủ 2.0s. Hủy đếm ổn định, quay lại chờ thùng.");
                        }
                        else
                        {
                            double boxDuration = (now - _boxFirstDetectedTime.Value).TotalSeconds;
                            if (boxDuration >= BoxStableDurationSeconds)
                            {
                                // Thùng đã ổn định đủ 2.0s!
                                State = RcsCycleState.WaitingForHumanDeparture;
                                _humanAbsentStartTime = hasHuman ? (DateTime?)null : now;
                                stateChanged = true;
                                _logger.LogInfo($"[CYCLE] Thùng hàng đã ổn định liên tục {boxDuration:F1}s! Bắt đầu theo dõi người rời đi (1.0s)...");
                            }
                        }
                        break;

                    case RcsCycleState.WaitingForHumanDeparture:
                        if (!hasBox)
                        {
                            // Thùng bị lấy đi trước khi gửi task -> Quay lại từ đầu
                            _boxFirstDetectedTime = null;
                            _humanAbsentStartTime = null;
                            State = RcsCycleState.WaitingForBox;
                            stateChanged = true;
                            _logger.LogInfo("[CYCLE] Thùng hàng biến mất trước khi kịp gửi task. Quay lại chờ thùng.");
                        }
                        else if (hasHuman)
                        {
                            // Có người -> Reset bộ đếm vắng người về 0
                            if (_humanAbsentStartTime != null)
                            {
                                _logger.LogInfo("[CYCLE] Người xuất hiện trở lại trước khi đủ 1.0s. Tính lại thời gian chờ từ đầu.");
                            }
                            _humanAbsentStartTime = null;
                        }
                        else
                        {
                            // Không có người -> Tích lũy thời gian vắng người
                            if (_humanAbsentStartTime == null)
                            {
                                _humanAbsentStartTime = now;
                            }

                            double absentDuration = (now - _humanAbsentStartTime.Value).TotalSeconds;
                            if (absentDuration >= HumanAbsentDurationSeconds)
                            {
                                // ĐỦ ĐIỀU KIỆN GỬI TASK!
                                State = RcsCycleState.TaskTriggered;
                                HasSentTaskInCycle = true;
                                stateChanged = true;

                                _logger.LogInfo($"[CYCLE] Thùng đã ổn định VÀ Người đã rời đi liên tục {absentDuration:F1}s! Kích hoạt gửi task RCS...");

                                // Kích hoạt gửi task bất đồng bộ ngoài lock
                                Task.Run(() => SendTaskInternalAsync());
                            }
                        }
                        break;

                    case RcsCycleState.TaskTriggered:
                        // Đã phát lệnh gửi task cho chu kỳ này, chuyển sang chờ thùng được lấy đi
                        State = RcsCycleState.WaitingForBoxClear;
                        _boxAbsentStartTime = null;
                        stateChanged = true;
                        break;

                    case RcsCycleState.WaitingForBoxClear:
                        // Chờ đến khi thùng hàng biến mất hoàn toàn
                        if (!hasBox)
                        {
                            if (_boxAbsentStartTime == null)
                            {
                                _boxAbsentStartTime = now;
                            }

                            double absentDuration = (now - _boxAbsentStartTime.Value).TotalSeconds;
                            if (absentDuration >= BoxClearToleranceSeconds)
                            {
                                // Thùng đã thực sự biến mất -> Reset chu kỳ mới
                                _logger.LogInfo($"[CYCLE] Thùng hàng hiện tại đã biến mất liên tục {absentDuration:F1}s. RESET CHU KỲ, sẵn sàng nhận thùng mới!");
                                ResetCycleInternal();
                                stateChanged = true;
                            }
                        }
                        else
                        {
                            // Thùng vẫn còn ở đó -> Giữ nguyên trạng thái chờ dọn thùng
                            _boxAbsentStartTime = null;
                        }
                        break;
                }

                statusMsg = GetStatusDescriptionInternal(now, hasBox, hasHuman);
            }

            if (stateChanged || prevState != State)
            {
                OnStateChanged?.Invoke(State, statusMsg);
            }
        }

        private async Task SendTaskInternalAsync()
        {
            lock (_stateLock)
            {
                if (IsSendingApi)
                    return;
                IsSendingApi = true;
            }

            string pickPos = "";
            string dropPos = "";
            RcsCallResult completedResult = null;

            try
            {
                string taskType;
                List<string> robotCodes;

                lock (_stateLock)
                {
                    taskType = !string.IsNullOrWhiteSpace(_config?.TaskType) ? _config.TaskType.Trim() : RcsApiConfig.DefaultTaskType;
                    pickPos = !string.IsNullOrWhiteSpace(_config?.PickupPosition) ? _config.PickupPosition.Trim() : "ETEK42";
                    dropPos = !string.IsNullOrWhiteSpace(_config?.DropoffPosition) ? _config.DropoffPosition.Trim() : "ETEK44";
                    robotCodes = _config?.ParseRobotCodes() ?? new List<string>();
                }

                var request = new ApplyTaskRequest
                {
                    TaskType = taskType,
                    TargetRoute = new List<TargetRouteItem>
                    {
                        new TargetRouteItem(0, pickPos, TargetRouteItem.DefaultType),
                        new TargetRouteItem(1, dropPos, TargetRouteItem.DefaultType)
                    }
                };

                if (robotCodes.Count > 0)
                {
                    request.RobotType = "ROBOTS";
                    request.RobotCode = robotCodes;
                }

                string robotInfo = robotCodes.Count > 0 ? string.Join(",", robotCodes) : "AUTO (RCS tự chọn)";
                _logger.LogInfo($"[CYCLE] Dang gui yeu cau Apply Task V4.2 (TaskType: {request.TaskType}, Pick-up: {pickPos}, Drop-off: {dropPos}, Robot: {robotInfo})...");

                completedResult = await _apiClient.ApplyTaskAsync(request).ConfigureAwait(false);

                lock (_stateLock)
                {
                    IsSendingApi = false;

                    if (completedResult.IsSuccess)
                    {
                        LastTaskCode = completedResult.CreatedTaskCode;
                        LastResultSummary = $"Thành công! TaskCode: {LastTaskCode}";
                        _logger.LogInfo($"[CYCLE] Task đã được RCS-2000 tạo thành công. Mã task: {LastTaskCode}");
                    }
                    else if (completedResult.Status == RcsCallStatus.BusinessError)
                    {
                        LastResultSummary = $"RCS báo lỗi: {completedResult.Response?.Message ?? completedResult.ErrorMessage}";
                        _logger.LogWarning($"[CYCLE] RCS từ chối task: {LastResultSummary}. KHÔNG gửi lại chu kỳ này.");
                    }
                    else if (completedResult.Status == RcsCallStatus.Timeout)
                    {
                        LastResultSummary = "API Timeout (Không gửi lại để tránh trùng task)";
                        _logger.LogWarning($"[CYCLE] {LastResultSummary}");
                    }
                    else
                    {
                        LastResultSummary = $"Lỗi kết nối ({completedResult.Status})";
                        _logger.LogError($"[CYCLE] Gửi task gặp lỗi: {completedResult.ErrorMessage}. KHÔNG gửi lại chu kỳ này.");
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    IsSendingApi = false;
                    LastResultSummary = $"Ngoại lệ: {ex.Message}";
                }
                _logger.LogError("[CYCLE] Lỗi ngoài dự kiến khi gửi task RCS", ex);
                completedResult = RcsCallResult.CreateNetworkError(ex.Message, string.Empty, 0);
            }

            if (completedResult != null)
            {
                OnTaskCompleted?.Invoke(completedResult, pickPos, dropPos);
            }
        }

        /// <summary>
        /// Kiểm tra đối tượng có phải là Thùng hàng / Sản phẩm không.
        /// Hỗ trợ Product (best1.onnx), Box, Carton, Pallet.
        /// </summary>
        public static bool IsBox(DetectionResult d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.ClassName))
                return false;

            string name = d.ClassName.Trim().ToLowerInvariant();
            return name.Contains("product") || name.Contains("box") || name.Contains("carton") || name.Contains("pallet");
        }

        /// <summary>
        /// Kiểm tra đối tượng có phải là Người không.
        /// Hỗ trợ Human (best1.onnx), Person.
        /// </summary>
        public static bool IsHuman(DetectionResult d)
        {
            if (d == null || string.IsNullOrWhiteSpace(d.ClassName))
                return false;

            string name = d.ClassName.Trim().ToLowerInvariant();
            return name.Contains("human") || name.Contains("person");
        }

        /// <summary>
        /// Lấy chuỗi mô tả trạng thái hiện tại kèm tiến trình thời gian phục vụ hiển thị lên giao diện.
        /// </summary>
        public string GetStatusDescription(DateTime? frameTime = null)
        {
            lock (_stateLock)
            {
                return GetStatusDescriptionInternal(frameTime ?? DateTime.UtcNow, false, false);
            }
        }

        private string GetStatusDescriptionInternal(DateTime now, bool hasBox, bool hasHuman)
        {
            switch (State)
            {
                case RcsCycleState.WaitingForBox:
                    return "RCS: Chờ thùng hàng";

                case RcsCycleState.BoxStabilizing:
                    if (_boxFirstDetectedTime.HasValue)
                    {
                        double sec = Math.Min(BoxStableDurationSeconds, (now - _boxFirstDetectedTime.Value).TotalSeconds);
                        return $"RCS: Thùng đang ổn định ({sec:F1}s / {BoxStableDurationSeconds:F1}s)";
                    }
                    return "RCS: Thùng đang ổn định...";

                case RcsCycleState.WaitingForHumanDeparture:
                    if (hasHuman || _humanAbsentStartTime == null)
                    {
                        return "RCS: Thùng ổn định (Có người trong khu vực)";
                    }
                    else
                    {
                        double sec = Math.Min(HumanAbsentDurationSeconds, (now - _humanAbsentStartTime.Value).TotalSeconds);
                        return $"RCS: Chờ người rời đi ({sec:F1}s / {HumanAbsentDurationSeconds:F1}s)";
                    }

                case RcsCycleState.TaskTriggered:
                case RcsCycleState.WaitingForBoxClear:
                    string info = !string.IsNullOrEmpty(LastTaskCode)
                        ? $"Task: {LastTaskCode}"
                        : (!string.IsNullOrEmpty(LastResultSummary) ? LastResultSummary : "Đã phát lệnh");
                    return $"RCS: Đã gửi task ({info}) - Chờ lấy thùng";

                default:
                    return "RCS: Sẵn sàng";
            }
        }

        /// <summary>
        /// Reset thủ công chu kỳ (ví dụ khi dừng Real Time).
        /// </summary>
        public void ResetCycle()
        {
            lock (_stateLock)
            {
                ResetCycleInternal();
            }
        }

        private void ResetCycleInternal()
        {
            State = RcsCycleState.WaitingForBox;
            HasSentTaskInCycle = false;
            _boxFirstDetectedTime = null;
            _humanAbsentStartTime = null;
            _boxAbsentStartTime = null;
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
        }
    }
}

