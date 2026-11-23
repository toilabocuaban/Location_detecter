using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PalletDetection.Rcs.Client
{
    /// <summary>
    /// Kết quả kiểm tra khả năng kết nối TCP tới máy chủ RCS-2000.
    /// </summary>
    public sealed class RcsConnectionCheckResult
    {
        public bool IsReachable { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public long DurationMs { get; set; }
        public string ErrorMessage { get; set; }

        public string EndpointDisplay => string.IsNullOrWhiteSpace(Host) ? string.Empty : $"{Host}:{Port}";
    }

    /// <summary>
    /// Tiện ích kiểm tra kết nối TCP không gửi dữ liệu nghiệp vụ để tránh tạo task giả trên RCS.
    /// </summary>
    public static class RcsConnectionChecker
    {
        public static async Task<RcsConnectionCheckResult> CheckTcpReachableAsync(
            string endpointUrl,
            int timeoutMs = 2000,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                return new RcsConnectionCheckResult
                {
                    IsReachable = false,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = "URL RCS chưa được cấu hình."
                };
            }

            Uri uri;
            if (!Uri.TryCreate(endpointUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                uri.Port <= 0)
            {
                return new RcsConnectionCheckResult
                {
                    IsReachable = false,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = "URL RCS không hợp lệ (cần http:// hoặc https://)."
                };
            }

            int effectiveTimeoutMs = timeoutMs > 0 ? timeoutMs : 2000;
            string host = uri.Host;
            int port = uri.Port;

            try
            {
                using (var client = new TcpClient())
                {
                    Task connectTask = client.ConnectAsync(host, port);
                    Task delayTask = Task.Delay(effectiveTimeoutMs, cancellationToken);
                    Task completed = await Task.WhenAny(connectTask, delayTask).ConfigureAwait(false);

                    if (completed != connectTask)
                    {
                        ObserveFault(connectTask);
                        stopwatch.Stop();

                        string timeoutOrCancelMsg = cancellationToken.IsCancellationRequested
                            ? "Đã hủy kiểm tra kết nối TCP."
                            : $"Quá thời gian chờ TCP ({effectiveTimeoutMs} ms).";

                        return new RcsConnectionCheckResult
                        {
                            IsReachable = false,
                            Host = host,
                            Port = port,
                            DurationMs = stopwatch.ElapsedMilliseconds,
                            ErrorMessage = timeoutOrCancelMsg
                        };
                    }

                    await connectTask.ConfigureAwait(false);
                    stopwatch.Stop();

                    if (!client.Connected)
                    {
                        return new RcsConnectionCheckResult
                        {
                            IsReachable = false,
                            Host = host,
                            Port = port,
                            DurationMs = stopwatch.ElapsedMilliseconds,
                            ErrorMessage = "Không thiết lập được phiên kết nối TCP."
                        };
                    }

                    return new RcsConnectionCheckResult
                    {
                        IsReachable = true,
                        Host = host,
                        Port = port,
                        DurationMs = stopwatch.ElapsedMilliseconds
                    };
                }
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return new RcsConnectionCheckResult
                {
                    IsReachable = false,
                    Host = host,
                    Port = port,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = "Đã hủy kiểm tra kết nối TCP."
                };
            }
            catch (SocketException sex)
            {
                stopwatch.Stop();
                return new RcsConnectionCheckResult
                {
                    IsReachable = false,
                    Host = host,
                    Port = port,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = $"Lỗi socket ({sex.SocketErrorCode}): {sex.Message}"
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new RcsConnectionCheckResult
                {
                    IsReachable = false,
                    Host = host,
                    Port = port,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static void ObserveFault(Task task)
        {
            if (task == null)
                return;

            task.ContinueWith(
                t => { Exception ignored = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
