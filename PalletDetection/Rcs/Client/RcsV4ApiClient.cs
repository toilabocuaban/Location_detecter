using Newtonsoft.Json;
using PalletDetection.Rcs.Config;
using PalletDetection.Rcs.Logging;
using PalletDetection.Rcs.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PalletDetection.Rcs.Client
{
    /// <summary>
    /// REST API Client kết nối đến Hikrobot RCS-2000 V4.2.0 (hoặc Mockoon).
    /// Thực hiện gửi yêu cầu Apply Task (/rcs/rtas/api/robot/controller/task/submit) qua HTTP POST (không bật chữ ký số).
    /// Đảm bảo nguyên tắc: Gửi header X-lr-request-id (16 hex), X-lr-version: v1.0, phân loại rõ lỗi và KHÔNG tự retry khi timeout/lỗi.
    /// </summary>
    public class RcsV4ApiClient : IDisposable
    {
        public const string DefaultApiVersion = "v1.0";

        private readonly RcsApiConfig _config;
        private readonly IRcsLogger _logger;
        private readonly HttpClient _httpClient;
        private bool _disposed = false;

        static RcsV4ApiClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public RcsApiConfig Config => _config;
        public IRcsLogger Logger => _logger;

        public RcsV4ApiClient() : this(null, null, null)
        {
        }

        public RcsV4ApiClient(RcsApiConfig config = null, IRcsLogger logger = null, HttpMessageHandler handler = null)
        {
            _config = config ?? RcsApiConfig.LoadOrCreateDefault();
            _logger = logger ?? new RcsLogger(configuredToken: _config.TokenCode);

            _httpClient = handler == null ? new HttpClient(CreateDefaultHandler()) : new HttpClient(handler);
            int timeoutSec = _config.TimeoutSeconds > 0 ? _config.TimeoutSeconds : 5;
            _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        }

        private static HttpMessageHandler CreateDefaultHandler()
        {
            var handler = new WebRequestHandler();
            handler.ServerCertificateValidationCallback = ValidateServerCertificate;
            return handler;
        }

        /// <summary>
        /// Chỉ cho phép chứng chỉ SSL tự ký đối với các host thuộc mạng nội bộ/LAN khi gọi RCS.
        /// </summary>
        public static bool ValidateServerCertificate(
            object sender,
            X509Certificate certificate,
            X509Chain chain,
            SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (certificate == null) return false;

            string host = (sender as HttpWebRequest)?.RequestUri?.Host
                ?? (sender as HttpRequestMessage)?.RequestUri?.Host
                ?? (sender as Uri)?.Host;

            if (string.IsNullOrWhiteSpace(host))
                return false;

            return IsLocalOrPrivateHost(host);
        }

        /// <summary>
        /// Kiểm tra host đích có phải địa chỉ loopback, LAN riêng (RFC1918), link-local hoặc tên máy nội bộ hay không.
        /// </summary>
        public static bool IsLocalOrPrivateHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            string trimmed = host.Trim().Trim('[', ']');
            if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                return true;

            // Parse IP trước khi kiểm tra tên miền không chứa dấu '.' để không nhận nhầm IPv6 công cộng.
            IPAddress ip;
            if (IPAddress.TryParse(trimmed, out ip))
            {
                if (IPAddress.IsLoopback(ip)) return true;
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    byte[] b = ip.GetAddressBytes();
                    if (b[0] == 10) return true;
                    if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                    if (b[0] == 192 && b[1] == 168) return true;
                    if (b[0] == 169 && b[1] == 254) return true;
                    return false;
                }

                if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
                    byte[] b = ip.GetAddressBytes();
                    return (b[0] & 0xFE) == 0xFC;
                }
                return false;
            }

            return !trimmed.Contains(".") && !trimmed.Contains(":");
        }

        /// <summary>
        /// Gửi yêu cầu Apply Task (Mục 2.1.2) tới RCS-2000 V4.2.0 qua HTTP POST.
        /// </summary>
        public async Task<RcsCallResult> ApplyTaskAsync(ApplyTaskRequest request, CancellationToken cancellationToken = default, string requestId = null)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            request.Validate();

            string reqId = NormalizeRequestId(requestId);
            string traceId = Guid.NewGuid().ToString("N");
            string url = _config.GetFullEndpointUrl();
            string jsonBody = JsonConvert.SerializeObject(request, Formatting.None);

            _logger.LogInfo($"[REQ] POST {url} | X-lr-request-id: {reqId} | X-lr-version: {DefaultApiVersion} | Body: {jsonBody}");

            var stopwatch = Stopwatch.StartNew();

            try
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Post, url))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    httpRequest.Headers.TryAddWithoutValidation("X-lr-request-id", reqId);
                    httpRequest.Headers.TryAddWithoutValidation("X-lr-version", DefaultApiVersion);
                    httpRequest.Headers.TryAddWithoutValidation("X-lr-trace-id", traceId);

                    if (!string.IsNullOrWhiteSpace(_config.ClientCode))
                    {
                        string source = _config.ClientCode.Trim();
                        if (source.Length > 32)
                            source = source.Substring(0, 32);
                        httpRequest.Headers.TryAddWithoutValidation("X-lr-source", source);
                    }

                    httpRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    HttpResponseMessage responseMessage;
                    try
                    {
                        responseMessage = await _httpClient.SendAsync(httpRequest, linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException tex)
                    {
                        stopwatch.Stop();
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger.LogWarning($"[CANCEL] Yêu cầu Apply Task (X-lr-request-id: {reqId}) đã bị hủy bởi caller.");
                            return RcsCallResult.CreateTimeout(reqId, stopwatch.ElapsedMilliseconds, "Tác vụ bị hủy bởi caller.");
                        }

                        _logger.LogError($"[TIMEOUT] Yêu cầu Apply Task (X-lr-request-id: {reqId}) bị Timeout sau {stopwatch.ElapsedMilliseconds} ms. CẢNH BÁO: Không tự động gửi lại để tránh tạo trùng task!", tex);
                        return RcsCallResult.CreateTimeout(reqId, stopwatch.ElapsedMilliseconds);
                    }

                    stopwatch.Stop();
                    long durationMs = stopwatch.ElapsedMilliseconds;

                    string rawResponse = await responseMessage.Content.ReadAsStringAsync().ConfigureAwait(false);
                    int statusCode = (int)responseMessage.StatusCode;
                    string responseReqId = ReadHeader(responseMessage, "X-lr-request-id");

                    _logger.LogInfo($"[RESP] HTTP {statusCode} ({durationMs} ms) | X-lr-request-id: {responseReqId ?? reqId} | Raw: {rawResponse}");

                    if (!responseMessage.IsSuccessStatusCode)
                    {
                        _logger.LogError($"[HTTP_ERROR] Máy chủ trả về mã HTTP {statusCode} cho X-lr-request-id {reqId}. Raw: {rawResponse}");
                        return RcsCallResult.CreateHttpError(statusCode, rawResponse, reqId, durationMs);
                    }

                    RcsApiResponse rcsResponse;
                    try
                    {
                        rcsResponse = JsonConvert.DeserializeObject<RcsApiResponse>(rawResponse);
                        if (rcsResponse == null)
                        {
                            throw new JsonException("Kết quả JSON deserialize trả về null.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"[PARSE_ERROR] Không thể phân tích JSON phản hồi từ RCS (X-lr-request-id: {reqId}). Raw: {rawResponse}", ex);
                        return RcsCallResult.CreateParseError(ex.Message, rawResponse, statusCode, reqId, durationMs);
                    }

                    if (!string.IsNullOrWhiteSpace(responseReqId))
                    {
                        rcsResponse.ReqCode = responseReqId;
                        if (!string.Equals(reqId, responseReqId, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogWarning($"[REQID_MISMATCH] Cảnh báo: header X-lr-request-id phản hồi ({responseReqId}) không khớp với giá trị đã gửi ({reqId}).");
                        }
                    }

                    if (rcsResponse.IsSuccess)
                    {
                        _logger.LogInfo($"[BUSINESS_SUCCESS] Apply Task thành công! robotTaskCode: {rcsResponse.TaskCode} (X-lr-request-id: {reqId}, code: {rcsResponse.Code})");
                        return RcsCallResult.CreateSuccess(rcsResponse, rawResponse, reqId, durationMs);
                    }
                    else
                    {
                        _logger.LogWarning($"[BUSINESS_ERROR] RCS từ chối Apply Task! Code: {rcsResponse.Code}, Message: {rcsResponse.Message} (X-lr-request-id: {reqId})");
                        return RcsCallResult.CreateBusinessError(rcsResponse, rawResponse, reqId, durationMs);
                    }
                }
            }
            catch (HttpRequestException hrex)
            {
                stopwatch.Stop();
                string rootMessage = hrex.GetBaseException()?.Message;
                string detailMessage = string.IsNullOrWhiteSpace(rootMessage) ||
                                       string.Equals(rootMessage, hrex.Message, StringComparison.Ordinal)
                    ? hrex.Message
                    : $"{hrex.Message} ({rootMessage})";
                _logger.LogError($"[NETWORK_ERROR] Không thể kết nối tới server RCS ({url}) cho X-lr-request-id: {reqId}. Chi tiết: {detailMessage}", hrex);
                return RcsCallResult.CreateNetworkError(detailMessage, reqId, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError($"[UNEXPECTED_ERROR] Lỗi không xác định khi gọi API RCS ({url}) cho X-lr-request-id: {reqId}. Chi tiết: {ex.Message}", ex);
                return RcsCallResult.CreateNetworkError(ex.Message, reqId, stopwatch.ElapsedMilliseconds);
            }
        }

        private static string NormalizeRequestId(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                return Guid.NewGuid().ToString("N").Substring(0, 16);

            string trimmed = requestId.Trim();
            return trimmed.Length <= 16 ? trimmed : trimmed.Substring(0, 16);
        }

        private static string ReadHeader(HttpResponseMessage response, string headerName)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues(headerName, out values))
                return values.FirstOrDefault();
            return null;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
