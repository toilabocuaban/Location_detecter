using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PalletDetection.Rcs.Config
{
    /// <summary>
    /// Quản lý cấu hình kết nối tới Hikrobot RCS-2000 V4.2.0 hoặc Mockoon server.
    /// Cho phép chuyển đổi môi trường qua file rcs_config.json hoặc giao diện Admin mà không cần build lại.
    /// </summary>
    public class RcsApiConfig
    {
        public const string DefaultConfigFileName = "rcs_config.json";
        public const string DefaultBaseUrl = "https://192.168.65.193";
        public const string DefaultEndpointPath = "/rcs/rtas/api/robot/controller/task/submit";
        public const string LegacyV3EndpointPath = "/rcms/services/rest/hikRpcService/genAgvSchedulingTask";
        public const string DefaultTaskType = "QF2";
        public const string DefaultRobotCode = "19781";

        /// <summary>
        /// Địa chỉ gốc của server (mặc định "https://192.168.65.193", hoặc "http://127.0.0.1:8182" cho Mockoon).
        /// </summary>
        [JsonProperty("baseUrl")]
        public string BaseUrl { get; set; } = DefaultBaseUrl;

        /// <summary>
        /// Đường dẫn endpoint Apply Task V4.2.0. Mặc định là /rcs/rtas/api/robot/controller/task/submit.
        /// </summary>
        [JsonProperty("endpointPath")]
        public string EndpointPath { get; set; } = DefaultEndpointPath;

        /// <summary>
        /// Loại task cấu hình trên RCS-2000 V4.2.0 (mặc định "QF2").
        /// </summary>
        [JsonProperty("taskType")]
        public string TaskType { get; set; } = DefaultTaskType;

        /// <summary>
        /// Mã xe AMR thực hiện (mặc định "19781"; có thể nhập nhiều mã cách nhau dấu phẩy ',', để trống để RCS tự chọn xe rảnh).
        /// </summary>
        [JsonProperty("robotCode")]
        public string RobotCode { get; set; } = DefaultRobotCode;

        /// <summary>
        /// Token xác thực (tùy chọn, không dùng khi tắt chữ ký số).
        /// </summary>
        [JsonProperty("tokenCode")]
        public string TokenCode { get; set; } = "";

        /// <summary>
        /// Mã nguồn hệ thống gửi qua header X-lr-source (mặc định "VISION_PALLET_APP").
        /// </summary>
        [JsonProperty("clientCode")]
        public string ClientCode { get; set; } = "VISION_PALLET_APP";

        /// <summary>
        /// Mã điểm Pick-up (seq 0, type SITE).
        /// </summary>
        [JsonProperty("pickupPosition")]
        public string PickupPosition { get; set; } = "ETEK42";

        /// <summary>
        /// Mã điểm Drop-off (seq 1, type SITE).
        /// </summary>
        [JsonProperty("dropoffPosition")]
        public string DropoffPosition { get; set; } = "ETEK44";

        /// <summary>
        /// Thời gian timeout khi gửi HTTP request (tính bằng giây). Mặc định là 5 giây.
        /// </summary>
        [JsonProperty("timeoutSeconds")]
        public int TimeoutSeconds { get; set; } = 5;

        /// <summary>
        /// Lấy URL hoàn chỉnh để gửi request (hỗ trợ trường hợp BaseUrl đã chứa sẵn đường dẫn path).
        /// </summary>
        public string GetFullEndpointUrl()
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new InvalidOperationException("BaseUrl chưa được cấu hình.");

            string baseClean = BaseUrl.Trim().TrimEnd('/');
            string pathClean = string.IsNullOrWhiteSpace(EndpointPath)
                ? DefaultEndpointPath
                : (EndpointPath.Trim().StartsWith("/") ? EndpointPath.Trim() : "/" + EndpointPath.Trim());

            Uri parsedUri;
            if (Uri.TryCreate(baseClean, UriKind.Absolute, out parsedUri) &&
                !string.IsNullOrEmpty(parsedUri.AbsolutePath) &&
                parsedUri.AbsolutePath != "/")
            {
                return baseClean;
            }

            return baseClean + pathClean;
        }

        /// <summary>
        /// Cập nhật BaseUrl và EndpointPath từ URL người dùng nhập (nhận cả base URL lẫn full URL).
        /// </summary>
        public void SetFullOrBaseUrl(string inputUrl)
        {
            if (string.IsNullOrWhiteSpace(inputUrl))
                throw new ArgumentException("URL RCS không được để trống.", nameof(inputUrl));

            string trimmed = inputUrl.Trim();
            Uri parsedUri;
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("URL RCS phải là địa chỉ http:// hoặc https:// hợp lệ.", nameof(inputUrl));
            }

            BaseUrl = parsedUri.GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrEmpty(parsedUri.AbsolutePath) && parsedUri.AbsolutePath != "/")
            {
                EndpointPath = parsedUri.AbsolutePath;
            }
            else if (string.IsNullOrWhiteSpace(EndpointPath) ||
                     string.Equals(EndpointPath.Trim(), LegacyV3EndpointPath, StringComparison.OrdinalIgnoreCase))
            {
                EndpointPath = DefaultEndpointPath;
            }
        }

        /// <summary>
        /// Tách danh sách mã RobotCode phân cách bằng dấu phẩy. Trả về danh sách rỗng nếu để trống.
        /// </summary>
        public List<string> ParseRobotCodes()
        {
            if (string.IsNullOrWhiteSpace(RobotCode))
                return new List<string>();

            return RobotCode.Split(',')
                .Select(code => code.Trim())
                .Where(code => code.Length > 0)
                .ToList();
        }

        public static string GetDefaultConfigPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                string asmLoc = Path.GetDirectoryName(typeof(RcsApiConfig).Assembly.Location);
                if (!string.IsNullOrEmpty(asmLoc))
                {
                    baseDir = asmLoc;
                }
            }
            catch { }

            return Path.Combine(baseDir, DefaultConfigFileName);
        }

        /// <summary>
        /// Đọc cấu hình từ file JSON. Nếu file chưa tồn tại, tự động tạo file mẫu với giá trị mặc định V4.2.0.
        /// </summary>
        public static RcsApiConfig LoadOrCreateDefault(string filePath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = GetDefaultConfigPath();
            }

            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var config = JsonConvert.DeserializeObject<RcsApiConfig>(json);
                    if (config != null)
                    {
                        if (string.IsNullOrWhiteSpace(config.BaseUrl))
                            config.BaseUrl = DefaultBaseUrl;
                        if (string.IsNullOrWhiteSpace(config.EndpointPath) ||
                            string.Equals(config.EndpointPath.Trim(), LegacyV3EndpointPath, StringComparison.OrdinalIgnoreCase))
                            config.EndpointPath = DefaultEndpointPath;
                        if (string.IsNullOrWhiteSpace(config.TaskType))
                            config.TaskType = DefaultTaskType;
                        if (config.RobotCode == null)
                            config.RobotCode = string.Empty;
                        return config;
                    }
                }

                var defaultConfig = new RcsApiConfig();
                defaultConfig.SaveToFile(filePath);
                return defaultConfig;
            }
            catch (Exception)
            {
                return new RcsApiConfig();
            }
        }

        /// <summary>
        /// Lưu cấu hình ra file JSON.
        /// </summary>
        public void SaveToFile(string filePath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                filePath = GetDefaultConfigPath();
            }

            string json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(filePath, json);
        }
    }
}
