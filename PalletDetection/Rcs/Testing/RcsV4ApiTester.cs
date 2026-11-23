using PalletDetection.Rcs.Client;
using PalletDetection.Rcs.Config;
using PalletDetection.Rcs.Logging;
using PalletDetection.Rcs.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PalletDetection.Rcs.Testing
{
    /// <summary>
    /// Bộ tiện ích kiểm thử độc lập module REST API RCS-2000 V4.2.0 (Apply Task) với Mockoon trước khi kết nối RCS thật.
    /// </summary>
    public class RcsV4ApiTester
    {
        private readonly RcsApiConfig _config;
        private readonly IRcsLogger _logger;

        public RcsV4ApiTester() : this(null, null)
        {
        }

        public RcsV4ApiTester(RcsApiConfig config = null, IRcsLogger logger = null)
        {
            _config = config ?? RcsApiConfig.LoadOrCreateDefault();
            _logger = logger ?? new RcsLogger(configuredToken: _config.TokenCode);
        }

        public async Task<RcsCallResult> RunTestSuccessAsync(string taskType = "QF2", string pickupPosition = "ETEK42", string dropoffPosition = "ETEK44", string robotCode = "19781")
        {
            using (var client = new RcsV4ApiClient(_config, _logger))
            {
                var request = new ApplyTaskRequest
                {
                    TaskType = taskType,
                    TargetRoute = new List<TargetRouteItem>
                    {
                        new TargetRouteItem(0, pickupPosition, TargetRouteItem.DefaultType),
                        new TargetRouteItem(1, dropoffPosition, TargetRouteItem.DefaultType)
                    }
                };

                if (!string.IsNullOrWhiteSpace(robotCode))
                {
                    request.RobotType = "ROBOTS";
                    request.RobotCode = new List<string> { robotCode.Trim() };
                }

                return await client.ApplyTaskAsync(request).ConfigureAwait(false);
            }
        }

        public async Task<RcsCallResult> RunTestBusinessErrorAsync()
        {
            using (var client = new RcsV4ApiClient(_config, _logger))
            {
                var request = new ApplyTaskRequest
                {
                    TaskType = "INVALID_TASK_TYPE",
                    TargetRoute = new List<TargetRouteItem>
                    {
                        new TargetRouteItem(0, "ETEK42", TargetRouteItem.DefaultType)
                    }
                };

                return await client.ApplyTaskAsync(request).ConfigureAwait(false);
            }
        }

        public async Task<RcsCallResult> RunTestNetworkErrorAsync()
        {
            var invalidConfig = new RcsApiConfig
            {
                BaseUrl = "http://127.0.0.1:59999",
                EndpointPath = RcsApiConfig.DefaultEndpointPath,
                TimeoutSeconds = 2,
                ClientCode = _config.ClientCode
            };

            using (var client = new RcsV4ApiClient(invalidConfig, _logger))
            {
                var request = new ApplyTaskRequest
                {
                    TaskType = "QF2",
                    TargetRoute = new List<TargetRouteItem>
                    {
                        new TargetRouteItem(0, "ETEK42", TargetRouteItem.DefaultType)
                    }
                };

                return await client.ApplyTaskAsync(request).ConfigureAwait(false);
            }
        }

        public async Task<RcsCallResult> RunTestTimeoutAsync()
        {
            var shortTimeoutConfig = new RcsApiConfig
            {
                BaseUrl = _config.BaseUrl,
                EndpointPath = _config.EndpointPath,
                TimeoutSeconds = 1,
                ClientCode = _config.ClientCode
            };

            using (var client = new RcsV4ApiClient(shortTimeoutConfig, _logger))
            {
                var request = new ApplyTaskRequest
                {
                    TaskType = "TIMEOUT_TEST",
                    TargetRoute = new List<TargetRouteItem>
                    {
                        new TargetRouteItem(0, "ETEK42", TargetRouteItem.DefaultType)
                    }
                };

                return await client.ApplyTaskAsync(request).ConfigureAwait(false);
            }
        }
    }
}
