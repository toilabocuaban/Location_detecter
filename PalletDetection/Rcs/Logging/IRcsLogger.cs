using System;

namespace PalletDetection.Rcs.Logging
{
    /// <summary>
    /// Giao diện Logger ghi nhận hoạt động và lỗi của module RCS REST API.
    /// Đảm bảo nguyên tắc bảo mật: không làm lộ token hoặc thông tin xác thực.
    /// </summary>
    public interface IRcsLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message, Exception ex = null);

        /// <summary>
        /// Ẩn chuỗi token và dữ liệu nhạy cảm trong văn bản / JSON trước khi ghi log hoặc hiển thị.
        /// </summary>
        string MaskSensitiveData(string text);
    }
}
