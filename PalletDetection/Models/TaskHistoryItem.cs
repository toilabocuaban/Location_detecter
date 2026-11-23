using System;

namespace PalletDetection.Models
{
    /// <summary>
    /// Lưu thông tin hiển thị cho một lần gửi Apply Task RCS-2000 trong lịch sử vận hành.
    /// </summary>
    public sealed class TaskHistoryItem
    {
        public DateTime Time { get; set; }
        public string TimeText => Time.ToString("HH:mm:ss");
        public string TaskCode { get; set; }
        public string RouteText { get; set; }
        public string TaskAndRouteDisplay =>
            (string.IsNullOrWhiteSpace(TaskCode) ? "-" : TaskCode) +
            (string.IsNullOrWhiteSpace(RouteText) ? "" : " · " + RouteText);
        public bool IsSuccess { get; set; }
        public long DurationMs { get; set; }
        public string DurationText => DurationMs > 0 ? DurationMs + " ms" : "-";
        public string Message { get; set; }
        public string ResultKind => IsSuccess ? "Ok" : "Ng";
        public string ResultBadgeText =>
            IsSuccess
                ? $"OK ({DurationMs} ms)"
                : (string.IsNullOrWhiteSpace(Message) ? "NG" : "NG — " + Message);
    }
}
