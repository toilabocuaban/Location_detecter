using OpenCvSharp;
using PalletDetection.Camera;
using PalletDetection.Models;
using PalletDetection.Rcs.Automation;
using PalletDetection.Rcs.Client;
using PalletDetection.Rcs.Models;
using PalletDetection.Views;
using PalletDetection.Vision;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;

namespace PalletDetection
{
    public partial class MainWindow : System.Windows.Window
    {
        #region Fields and State

        private CameraManager _cameraManager;
        private DispatcherTimer _liveTimer;

        // AI Detection
        private YoloV8Detector _detector;
        private Rect? _roiImageRect;
        private bool _isRoiDrawingMode = false;
        private bool _isMouseDown = false;
        private Point _startMousePoint;
        private System.Windows.Shapes.Rectangle _drawingRoiShape;

        // Máy trạng thái RealTime & Quản lý bất đồng bộ chống Race Condition
        private enum RealTimeState
        {
            Stopped,
            Starting,
            Running,
            Stopping
        }

        private RealTimeState _realTimeState = RealTimeState.Stopped;
        private readonly SemaphoreSlim _operationLock = new SemaphoreSlim(1, 1);
        private volatile int _renderOwner = 0; // 0 = None, 1 = LiveTimer, 2 = RealTime
        // Trạng thái RealTime được quản lý tập trung bởi máy trạng thái _realTimeState
        private CancellationTokenSource _realTimeCts;
        private Task _realTimeTask;
        private readonly object _inferenceLock = new object();

        // Quản lý đóng ứng dụng an toàn (Chống đệ quy và giải phóng tài nguyên triệt để)
        private bool _isClosing = false;
        private bool _isCleaningUp = false;
        private bool _isCleanupDone = false;

        // Quản lý cấu hình Camera (DPAPI) và Admin Authentication
        private CameraConfig _cameraConfig;
        private AdminAuthConfig _adminAuthConfig;
        private bool _isAdminLoggedIn = false;

        // Tự động gửi Task RCS theo chu kỳ nhận diện thùng và người
        private RcsTaskCycleManager _rcsCycleManager;
        private volatile bool _isAutoSendRcsTaskEnabled = true;

        // Kiểm tra kết nối TCP tới RCS định kỳ
        private DispatcherTimer _rcsHealthTimer;
        private CancellationTokenSource _rcsHealthCts;
        private int _isCheckingRcsHealth = 0;
        private bool? _isRcsReachable = null;
        private string _rcsHealthTooltip = "RCS: Đang kiểm tra kết nối...";
        private string _rcsHealthStatusText = "RCS: Đang kiểm tra kết nối...";

        // Lịch sử, Nhật ký và Thống kê HMI
        private readonly ObservableCollection<TaskHistoryItem> _taskHistory = new ObservableCollection<TaskHistoryItem>();
        private readonly ObservableCollection<string> _operationLogs = new ObservableCollection<string>();
        private int _totalTaskCount = 0;
        private int _okTaskCount = 0;
        private int _ngTaskCount = 0;
        private bool _lastRcsCallFailed = false;

        #endregion

        #region Constructor

        public MainWindow()
        {
            InitializeComponent();
            dgTaskHistory.ItemsSource = _taskHistory;

            _cameraManager = new CameraManager();

            bool result = _cameraManager.Initialize();

            if (!result)
            {
                MessageBox.Show(
                    "Không thể khởi tạo Hikvision SDK.",
                    "SDK Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Close();
                return;
            }

            _liveTimer = new DispatcherTimer();
            _liveTimer.Interval = TimeSpan.FromMilliseconds(33); // khoảng 30 FPS
            _liveTimer.Tick += LiveTimer_Tick;

            // Khởi tạo AI Detector và tự động load model mặc định nếu có
            _detector = new YoloV8Detector();
            AutoLoadDefaultModel();

            // Tự động nạp vùng ROI đã lưu (nếu có)
            AutoLoadSavedRoi();

            // Khởi tạo bộ quản lý chu kỳ gửi Task RCS
            _rcsCycleManager = new RcsTaskCycleManager();
            _rcsCycleManager.OnTaskCompleted += OnRcsTaskCompleted;
            _rcsCycleManager.OnStateChanged += OnRcsCycleStateChanged;
            InitRcsTaskConfigUi();
            if (chkAutoSendRcsTask != null)
            {
                chkAutoSendRcsTask.Checked += (s, e) =>
                {
                    _isAutoSendRcsTaskEnabled = true;
                    AppendLog("RCS: Đã BẬT tính năng tự động gửi task.");
                    UpdateHeaderIndicators();
                };
                chkAutoSendRcsTask.Unchecked += (s, e) =>
                {
                    _isAutoSendRcsTaskEnabled = false;
                    AppendLog("RCS: Đã TẮT tính năng tự động gửi task.");
                    UpdateHeaderIndicators();
                };
            }

            // Kết nối Logger của chu kỳ RCS vào danh sách nhật ký hoạt động
            if (_rcsCycleManager.Logger is PalletDetection.Rcs.Logging.RcsLogger rcsLogger)
            {
                rcsLogger.OnLogEntry += (logLine) => AddLogEntry(logLine);
            }

            _rcsHealthCts = new CancellationTokenSource();
            _rcsHealthTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10)
            };
            _rcsHealthTimer.Tick += async (s, e) => await CheckRcsConnectivityAsync(logStateChange: true);

            AppendLog("Hệ thống khởi động thành công. Bắt đầu tự động kết nối...");
            UpdateHeaderIndicators();
            UpdateRcsIdleStatusText();

            // Bắt đầu tự động kết nối camera và nạp cấu hình hệ thống
            AutoConnectAndInitSystemAsync();
        }

        #endregion

        #region System Initialization & Readiness Assessment

        /// <summary>
        /// Tự động nạp cấu hình đã lưu, kết nối Camera bằng mật khẩu DPAPI và phát Live View khi khởi động ứng dụng.
        /// </summary>
        private async void AutoConnectAndInitSystemAsync()
        {
            try
            {
                _ = CheckRcsConnectivityAsync(logStateChange: true);
                _rcsHealthTimer?.Start();
                // 1. Nạp cấu hình Camera từ file camera_config.json
                _cameraConfig = CameraConfig.LoadOrCreateDefault();
                txtIP.Text = _cameraConfig.IP;
                txtPort.Text = _cameraConfig.Port.ToString();
                txtUsername.Text = _cameraConfig.Username;

                string decryptedPassword = _cameraConfig.GetDecryptedPassword();
                txtPassword.Password = decryptedPassword;

                // 2. Nạp cấu hình Admin
                _adminAuthConfig = AdminAuthConfig.LoadOrCreateDefault();

                // 3. Kết nối Camera tự động trong luồng nền để không chặn giao diện
                AppendLog($"Camera: Đang tự động kết nối {_cameraConfig.IP}:{_cameraConfig.Port}...");

                bool loginSuccess = false;
                bool rtspSuccess = false;

                await Task.Run(() =>
                {
                    try
                    {
                        loginSuccess = _cameraManager.Login(
                            _cameraConfig.IP,
                            _cameraConfig.Port,
                            _cameraConfig.Username,
                            decryptedPassword);

                        if (loginSuccess)
                        {
                            rtspSuccess = _cameraManager.ConnectRtsp(
                                _cameraConfig.IP,
                                _cameraConfig.Username,
                                decryptedPassword);

                            if (rtspSuccess)
                            {
                                _cameraManager.StartLive();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Camera tự động kết nối ngoại lệ: " + ex.Message);
                    }
                });

                if (loginSuccess && rtspSuccess)
                {
                    txtCameraStatus.Text = "Camera : ONLINE";
                    AppendLog($"Camera: Tự động kết nối và phát Live thành công ({_cameraConfig.IP}:{_cameraConfig.Port}).");
                    _liveTimer.Start();
                }
                else
                {
                    txtCameraStatus.Text = "Camera : OFFLINE";
                    AppendLog("Camera: Tự động kết nối thất bại! Quản trị viên vui lòng đăng nhập để kiểm tra hoặc kết nối thủ công.");
                }

                // Đánh giá trạng thái sẵn sàng của hệ thống
                EvaluateSystemReadiness();
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi tự động khởi tạo: " + ex.Message);
                EvaluateSystemReadiness();
            }
        }

        /// <summary>
        /// Thẩm định toàn diện trạng thái hệ thống: Camera, Model AI và ROI.
        /// Chỉ khi đủ điều kiện mới cho phép chuyển sang READY và bật nút Khởi Động.
        /// </summary>
        private void EvaluateSystemReadiness()
        {
            if (_isClosing)
                return;

            UpdateHeaderIndicators();

            // Nếu đang trong chu trình RealTime thì trạng thái được quản lý bởi máy trạng thái
            if (_realTimeState == RealTimeState.Running || _realTimeState == RealTimeState.Starting || _realTimeState == RealTimeState.Stopping)
            {
                return;
            }

            bool isCameraReady = _cameraManager != null && _cameraManager.IsConnected && _cameraManager.IsLiveRunning;
            bool isModelReady = _detector != null && _detector.IsLoaded;

            List<string> missing = new List<string>();
            if (!isCameraReady) missing.Add("Camera chưa phát Live");
            if (!isModelReady) missing.Add("Chưa nạp Model AI");

            if (missing.Count == 0)
            {
                SetStatusBanner("HỆ THỐNG: SẴN SÀNG (READY)", "Ready");

                if (btnStartOperation != null)
                    btnStartOperation.IsEnabled = true;

                if (btnStopOperation != null)
                    btnStopOperation.IsEnabled = false;
            }
            else
            {
                string reason = string.Join(", ", missing);
                SetStatusBanner($"HỆ THỐNG: CHƯA SẴN SÀNG ({reason})", "Error");

                if (btnStartOperation != null)
                    btnStartOperation.IsEnabled = false;

                if (btnStopOperation != null)
                    btnStopOperation.IsEnabled = false;
            }
        }

        #endregion

        #region Model AI Loading

        private void AutoLoadDefaultModel()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string[] possiblePaths = new[]
                {
                    System.IO.Path.Combine(baseDir, "best2.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\best2.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\PalletDetection\\best2.onnx"),
                    @"D:\work\vision\Location Detecter\Location Detecter\Program\Location Detecter 1\PalletDetection\PalletDetection\best2.onnx",
                    System.IO.Path.Combine(baseDir, "best1.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\best1.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\PalletDetection\\best1.onnx"),
                    @"D:\work\vision\Location Detecter\Location Detecter\Program\Location Detecter 1\PalletDetection\PalletDetection\best1.onnx",
                    System.IO.Path.Combine(baseDir, "best.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\best.onnx"),
                    System.IO.Path.Combine(baseDir, "..\\..\\PalletDetection\\best.onnx"),
                    @"D:\work\vision\Location Detecter\Location Detecter\Program\Location Detecter 1\PalletDetection\PalletDetection\best.onnx"
                };

                string foundModelPath = null;
                foreach (var path in possiblePaths)
                {
                    if (System.IO.File.Exists(path))
                    {
                        foundModelPath = System.IO.Path.GetFullPath(path);
                        break;
                    }
                }

                if (foundModelPath != null)
                {
                    bool loaded = _detector.LoadModel(foundModelPath);
                    if (loaded)
                    {
                        string fileName = System.IO.Path.GetFileName(foundModelPath);
                        txtModelStatus.Text = $"Model: {fileName}";
                        if (txtOpModelInfo != null)
                        {
                            txtOpModelInfo.Text = $"Model: {fileName}";
                        }
                        AppendLog($"Tự động nạp Model thành công: {foundModelPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi tự động load model mặc định: " + ex.Message);
            }
        }

        private void btnSelectModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "ONNX Model (*.onnx)|*.onnx|All Files (*.*)|*.*",
                Title = "Chọn file Model YOLOv8 ONNX"
            };

            if (dialog.ShowDialog() == true)
            {
                string modelPath = dialog.FileName;

                bool loaded = _detector.LoadModel(modelPath);

                if (loaded)
                {
                    string fileName = System.IO.Path.GetFileName(modelPath);
                    txtModelStatus.Text = $"Model: {fileName}";
                    if (txtOpModelInfo != null)
                    {
                        txtOpModelInfo.Text = $"Model: {fileName}";
                    }
                    AppendLog($"Đã nạp Model AI: {modelPath}");
                    MessageBox.Show("Nạp model ONNX thành công!", "Model AI", MessageBoxButton.OK, MessageBoxImage.Information);
                    EvaluateSystemReadiness();
                }
                else
                {
                    MessageBox.Show("Không thể nạp model ONNX.\nVui lòng kiểm tra file.", "Model AI Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    EvaluateSystemReadiness();
                }
            }
        }

        #endregion

        #region Camera Connection & Live Control

        private void btnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_realTimeState != RealTimeState.Stopped)
            {
                MessageBox.Show("Vui lòng dừng Real Time Detection trước khi thay đổi kết nối camera.", "Thông Báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = txtIP.Text.Trim();
            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;

            ushort port;
            if (!ushort.TryParse(txtPort.Text, out port))
            {
                MessageBox.Show("Port không hợp lệ.");
                return;
            }

            // Dừng live cũ trước khi kết nối mới để tránh xung đột luồng
            _liveTimer.Stop();
            _cameraManager.StopLive();

            bool login = _cameraManager.Login(ip, port, username, password);

            if (!login)
            {
                MessageBox.Show("Login Camera thất bại.\nError : " + _cameraManager.GetLastError());
                txtCameraStatus.Text = "Camera : OFFLINE";
                EvaluateSystemReadiness();
                return;
            }

            bool rtsp = _cameraManager.ConnectRtsp(ip, username, password);

            if (!rtsp)
            {
                MessageBox.Show("Không mở được RTSP.");
                txtCameraStatus.Text = "Camera : OFFLINE";
                EvaluateSystemReadiness();
                return;
            }

            txtCameraStatus.Text = "Camera : ONLINE";
            AppendLog($"Camera đã kết nối thành công: {ip}:{port}");

            // Tự động phát live sau khi kết nối thủ công thành công
            _cameraManager.StartLive();
            _liveTimer.Start();

            MessageBox.Show("Camera Connected & Live Started.", "Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            EvaluateSystemReadiness();
        }

        private void btnStartLive_Click(object sender, RoutedEventArgs e)
        {
            _cameraManager.StartLive();
            _liveTimer.Start();
            AppendLog("Camera: Bắt đầu phát Live stream.");
            EvaluateSystemReadiness();
        }

        private void btnStopLive_Click(object sender, RoutedEventArgs e)
        {
            if (_realTimeState != RealTimeState.Stopped)
            {
                StopRealTimeAsync().ConfigureAwait(false);
            }

            _liveTimer.Stop();
            _cameraManager.StopLive();
            AppendLog("Camera: Đã dừng phát Live stream.");
            EvaluateSystemReadiness();
        }

        private void btnSaveImage_Click(object sender, RoutedEventArgs e)
        {
            BitmapSource image = _cameraManager.GetCurrentImage();

            if (image == null)
            {
                MessageBox.Show("Không có hình ảnh để lưu.");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JPEG Image (*.jpg)|*.jpg|PNG Image (*.png)|*.png",
                DefaultExt = ".jpg",
                FileName = $"Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    BitmapEncoder encoder;
                    string ext = System.IO.Path.GetExtension(dialog.FileName).ToLower();

                    if (ext == ".png")
                        encoder = new PngBitmapEncoder();
                    else
                        encoder = new JpegBitmapEncoder();

                    encoder.Frames.Add(BitmapFrame.Create(image));

                    using (var fs = new FileStream(dialog.FileName, FileMode.Create))
                    {
                        encoder.Save(fs);
                    }

                    AppendLog($"Đã lưu ảnh snapshot: {dialog.FileName}");
                    MessageBox.Show("Lưu ảnh thành công!", "Lưu Ảnh", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi khi lưu ảnh: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LiveTimer_Tick(object sender, EventArgs e)
        {
            // Tránh xung đột Live View: Nếu Realtime đang vẽ (_renderOwner == 2), LiveTimer không được gán đè frame
            if (_renderOwner == 2)
            {
                return;
            }

            _renderOwner = 1;
            try
            {
                BitmapSource image = _cameraManager.GetCurrentImage();

                if (image == null)
                    return;

                imgLive.Source = image;

                txtResolution.Text = string.Format(
                    "Resolution : {0} x {1}",
                    image.PixelWidth,
                    image.PixelHeight);
            }
            finally
            {
                if (_renderOwner == 1)
                {
                    _renderOwner = 0;
                }
            }
        }

        #endregion

        #region Single Detection

        private void btnDetect_Click(object sender, RoutedEventArgs e)
        {
            if (!_detector.IsLoaded)
            {
                MessageBox.Show(
                    "Model AI chưa được load.\nVui lòng chuyển sang Tab Configuration để chọn file Model (.onnx) trước.",
                    "AI Detection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!_cameraManager.IsConnected)
            {
                MessageBox.Show(
                    "Camera chưa kết nối.\nVui lòng chuyển sang Tab Configuration và bấm 'CONNECT CAMERA' trước.",
                    "AI Detection",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            using (Mat frame = _cameraManager.GetCurrentMat())
            {
                if (frame == null || frame.Empty())
                {
                    MessageBox.Show(
                        "Không lấy được frame từ camera.\nVui lòng kiểm tra lại camera.",
                        "AI Detection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                Rect? currentRoi = _roiImageRect;

                List<DetectionResult> detections;
                lock (_inferenceLock)
                {
                    detections = _detector.Detect(frame, currentRoi);
                }

                using (Mat annotated = _detector.AnnotateImage(frame, detections, currentRoi))
                {
                    BitmapSource bmp = ImageConverter.ToBitmapSource(annotated);
                    imgLive.Source = bmp;

                    string classStats = string.Join(", ", detections
                        .GroupBy(d => d.ClassName)
                        .Select(g => $"{g.Key}: {g.Count()}"));

                    string summary = $"Phát hiện {detections.Count} đối tượng" +
                        (string.IsNullOrEmpty(classStats) ? "" : $" ({classStats})");

                    txtDetectSummary.Text = summary;
                    txtDetectionInfo.Text = $"AI: {detections.Count} đối tượng";

                    AppendLog($"Single Detection: {summary}");
                }
            }
        }

        #endregion

        #region Real Time Detection & Synchronization

        private async void btnStartOperation_Click(object sender, RoutedEventArgs e)
        {
            await StartRealTimeAsync();
        }

        private async void btnStopOperation_Click(object sender, RoutedEventArgs e)
        {
            await StopRealTimeAsync();
        }

        private async void btnRealTime_Click(object sender, RoutedEventArgs e)
        {
            if (_realTimeState == RealTimeState.Running || _realTimeState == RealTimeState.Starting)
            {
                await StopRealTimeAsync();
            }
            else if (_realTimeState == RealTimeState.Stopped)
            {
                await StartRealTimeAsync();
            }
        }

        /// <summary>
        /// Khởi động tác vụ RealTime với Semaphore bảo vệ chống Race condition và chống trạng thái ảo.
        /// </summary>
        private async Task StartRealTimeAsync()
        {
            await _operationLock.WaitAsync();
            try
            {
                if (_realTimeState != RealTimeState.Stopped)
                {
                    return;
                }

                if (!_detector.IsLoaded)
                {
                    MessageBox.Show(
                        "Model AI chưa được load.\nVui lòng chuyển sang Tab Configuration để chọn file Model (.onnx) trước.",
                        "Real Time Detection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    EvaluateSystemReadiness();
                    return;
                }

                if (!_cameraManager.IsConnected || !_cameraManager.IsLiveRunning)
                {
                    MessageBox.Show(
                        "Camera chưa kết nối hoặc chưa phát Live.\nVui lòng kiểm tra lại camera trước khi bắt đầu Real Time.",
                        "Real Time Detection",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    EvaluateSystemReadiness();
                    return;
                }

                // 1. Chuyển trạng thái sang Starting
                _realTimeState = RealTimeState.Starting;

                // Vô hiệu hóa cả Start và Stop trong lúc đang khởi tạo
                if (btnStartOperation != null) btnStartOperation.IsEnabled = false;
                if (btnStopOperation != null) btnStopOperation.IsEnabled = false;
                if (btnRealTime != null) btnRealTime.IsEnabled = false;
                if (btnDetect != null) btnDetect.IsEnabled = false;

                SetStatusBanner("HỆ THỐNG: ĐANG KHỞI ĐỘNG REALTIME...", "Warning");
                UpdateHeaderIndicators();

                AppendLog("Bắt đầu khởi động nhận diện Real Time...");

                // Dừng live timer để Realtime độc quyền render
                _liveTimer.Stop();
                _renderOwner = 2;

                _realTimeCts = new CancellationTokenSource();
                var token = _realTimeCts.Token;

                _realTimeTask = Task.Run(async () =>
                {
                    bool isFirstInferencePassed = false;

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            if (!_cameraManager.IsLiveRunning || _isClosing)
                            {
                                break;
                            }

                            using (Mat currentFrame = _cameraManager.GetCurrentMat())
                            {
                                if (currentFrame == null || currentFrame.Empty())
                                {
                                    await Task.Delay(5, token).ConfigureAwait(false);
                                    continue;
                                }

                                Rect? currentRoi = _roiImageRect;

                                List<DetectionResult> detections;
                                lock (_inferenceLock)
                                {
                                    detections = _detector.Detect(currentFrame, currentRoi);
                                }

                                using (Mat annotated = _detector.AnnotateImage(currentFrame, detections, currentRoi))
                                {
                                    BitmapSource bmp = ImageConverter.ToBitmapSource(annotated);
                                    if (bmp != null && bmp.CanFreeze)
                                    {
                                        bmp.Freeze();
                                    }

                                    string classStats = string.Join(", ", detections
                                        .GroupBy(d => d.ClassName)
                                        .Select(g => $"{g.Key}: {g.Count()}"));

                                    string summary = $"Realtime: {detections.Count} đối tượng" +
                                        (string.IsNullOrEmpty(classStats) ? "" : $" ({classStats})");

                                    string rcsCycleText = null;
                                    if (_isAutoSendRcsTaskEnabled && _rcsCycleManager != null)
                                    {
                                        _rcsCycleManager.ProcessFrame(detections);
                                        rcsCycleText = _rcsCycleManager.GetStatusDescription();
                                    }
                                    else
                                    {
                                        rcsCycleText = "RCS: Tự động gửi đã tắt";
                                    }

                                    // Nếu frame đầu tiên inference thành công -> Chuyển sang RUNNING thật sự!
                                    if (!isFirstInferencePassed)
                                    {
                                        isFirstInferencePassed = true;
                                        _ = Dispatcher.BeginInvoke(new Action(() =>
                                        {
                                            _realTimeState = RealTimeState.Running;
                                            UpdateHeaderIndicators();
                                            SetStatusBanner("ĐANG VẬN HÀNH — CHỜ THÙNG HÀNG", "Ready");

                                            if (btnStartOperation != null) btnStartOperation.IsEnabled = false;
                                            if (btnStopOperation != null) btnStopOperation.IsEnabled = true;

                                            if (btnRealTime != null)
                                            {
                                                btnRealTime.IsEnabled = true;
                                                btnRealTime.Content = "STOP REAL TIME";
                                                btnRealTime.Background = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                                            }

                                            AppendLog("Real Time đang chạy ổn định.");
                                        }));
                                    }

                                    // Cập nhật UI render
                                    _ = Dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        if (_realTimeState != RealTimeState.Running || _isClosing)
                                            return;

                                        imgLive.Source = bmp;
                                        txtDetectSummary.Text = summary;
                                        txtDetectionInfo.Text = $"AI Realtime: {detections.Count} đối tượng";
                                        txtResolution.Text = string.Format(
                                            "Resolution : {0} x {1}",
                                            bmp.PixelWidth,
                                            bmp.PixelHeight);

                                        if (rcsCycleText != null && txtRcsCycleStatus != null)
                                        {
                                            txtRcsCycleStatus.Text = rcsCycleText;
                                        }
                                    }), DispatcherPriority.Render);
                                }
                            }

                            await Task.Delay(5, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                        catch (Exception)
                        {
                            await Task.Delay(30, token).ConfigureAwait(false);
                        }
                    }

                    // Tự động khôi phục nếu vòng lặp kết thúc do lỗi hoặc mất tín hiệu
                    _ = Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_realTimeState != RealTimeState.Stopped && !_isClosing)
                        {
                            StopRealTimeAsync().ConfigureAwait(false);
                        }
                    }));
                }, token);
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi khi bắt đầu Real Time: " + ex.Message);
                _realTimeState = RealTimeState.Stopped;
                
                _renderOwner = 0;
                EvaluateSystemReadiness();
            }
            finally
            {
                _operationLock.Release();
            }
        }

        /// <summary>
        /// Dừng tác vụ RealTime với Timeout 3s an toàn, chống treo ứng dụng và khôi phục Live Timer.
        /// </summary>
        private async Task StopRealTimeAsync()
        {
            await _operationLock.WaitAsync();
            try
            {
                if (_realTimeState == RealTimeState.Stopped)
                {
                    return;
                }

                _realTimeState = RealTimeState.Stopping;

                if (btnStartOperation != null) btnStartOperation.IsEnabled = false;
                if (btnStopOperation != null) btnStopOperation.IsEnabled = false;
                if (btnRealTime != null) btnRealTime.IsEnabled = false;

                SetStatusBanner("HỆ THỐNG: ĐANG DỪNG REALTIME...", "Warning");
                UpdateHeaderIndicators();

                AppendLog("Đang dừng nhận diện Real Time...");

                // Hủy CancellationTokenSource
                try
                {
                    _realTimeCts?.Cancel();
                }
                catch { }

                // Chờ task hoàn tất bất đồng bộ tối đa 3 giây
                if (_realTimeTask != null)
                {
                    try
                    {
                        await Task.WhenAny(_realTimeTask, Task.Delay(3000));
                    }
                    catch { }
                }

                // Dọn dẹp tài nguyên Realtime
                _realTimeState = RealTimeState.Stopped;
                
                _renderOwner = 0;

                if (btnRealTime != null)
                {
                    btnRealTime.IsEnabled = true;
                    btnRealTime.Content = "REAL TIME DETECTION";
                    btnRealTime.Background = new SolidColorBrush(Color.FromRgb(232, 244, 232));
                }

                if (btnDetect != null)
                {
                    btnDetect.IsEnabled = true;
                }

                txtDetectionInfo.Text = "AI: Đã dừng Real Time";
                AppendLog("Đã dừng nhận diện Real Time thành công.");

                // Reset chu kỳ RCS khi dừng Real Time
                _rcsCycleManager?.ResetCycle();
                UpdateRcsIdleStatusText();

                // Bật lại Live Timer thông thường nếu camera còn đang live
                if (_cameraManager.IsLiveRunning && !_isClosing)
                {
                    _liveTimer.Start();
                }

                // Thẩm định lại hệ thống để cập nhật trạng thái READY
                EvaluateSystemReadiness();
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi khi dừng Real Time: " + ex.Message);
                _realTimeState = RealTimeState.Stopped;
                
                _renderOwner = 0;
                EvaluateSystemReadiness();
            }
            finally
            {
                _operationLock.Release();
            }
        }

        #endregion

        #region Admin Authentication & Configuration Management

        private void btnAdminLogin_Click(object sender, RoutedEventArgs e)
        {
            string inputPwd = txtAdminPassword.Password;
            if (_adminAuthConfig == null)
            {
                _adminAuthConfig = AdminAuthConfig.LoadOrCreateDefault();
            }

            if (_adminAuthConfig.VerifyPassword(inputPwd))
            {
                _isAdminLoggedIn = true;
                pnlAdminConfig.IsEnabled = true;
                btnAdminLogin.IsEnabled = false;
                btnAdminLogout.IsEnabled = true;
                txtAdminPassword.Clear();

                txtAdminStatus.Text = "Trạng thái: Đã xác thực Quản trị viên (Được phép cấu hình)";
                txtAdminStatus.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
                AppendLog("Quản trị viên đã đăng nhập thành công.");
            }
            else
            {
                txtAdminStatus.Text = "Trạng thái: Sai mật khẩu quản trị!";
                txtAdminStatus.Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47));
                MessageBox.Show("Mật khẩu quản trị viên không chính xác!", "Xác Thực Quản Trị", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnAdminLogout_Click(object sender, RoutedEventArgs e)
        {
            _isAdminLoggedIn = false;
            pnlAdminConfig.IsEnabled = false;
            btnAdminLogin.IsEnabled = true;
            btnAdminLogout.IsEnabled = false;
            txtAdminPassword.Clear();

            txtAdminStatus.Text = "Trạng thái: Chưa xác thực (Khóa cấu hình)";
            txtAdminStatus.Foreground = new SolidColorBrush(Color.FromRgb(211, 47, 47));
            AppendLog("Quản trị viên đã đăng xuất.");
        }

        private void btnSaveCameraConfig_Click(object sender, RoutedEventArgs e)
        {
            if (!_isAdminLoggedIn)
            {
                MessageBox.Show("Vui lòng đăng nhập Quản trị viên để thực hiện thao tác này.", "Bảo Mật", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string ip = txtIP.Text.Trim();
            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;

            ushort port;
            if (!ushort.TryParse(txtPort.Text.Trim(), out port))
            {
                MessageBox.Show("Cổng (Port) không hợp lệ. Vui lòng nhập số hợp lệ.", "Lỗi Cấu Hình", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Địa chỉ IP không được để trống.", "Lỗi Cấu Hình", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_cameraConfig == null)
            {
                _cameraConfig = new CameraConfig();
            }

            _cameraConfig.IP = ip;
            _cameraConfig.Port = port;
            _cameraConfig.Username = username;
            _cameraConfig.SetPassword(password);
            _cameraConfig.SaveToFile();

            AppendLog($"Đã lưu cấu hình Camera ({ip}:{port}) vào camera_config.json (DPAPI mã hoá).");
            MessageBox.Show("Đã lưu cấu hình Camera và mã hóa mật khẩu thành công!", "Lưu Cấu Hình", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region ROI Setup

        private void btnDrawRoi_Click(object sender, RoutedEventArgs e)
        {
            _isRoiDrawingMode = true;
            canvasRoi.Cursor = Cursors.Cross;
            txtRoiStatus.Text = "ROI: Đang vẽ vùng quan sát...";
            AppendLog("Bắt đầu chế độ vẽ ROI trên màn hình.");
        }

        private void btnClearRoi_Click(object sender, RoutedEventArgs e)
        {
            _roiImageRect = null;
            _drawingRoiShape = null;
            canvasRoi.Children.Clear();
            txtRoiStatus.Text = "ROI: Toàn khung hình";
            if (txtOpRoiInfo != null)
            {
                txtOpRoiInfo.Text = "ROI: Toàn khung hình";
            }
            AppendLog("Đã xóa ROI. Nhận diện trên toàn khung hình.");
        }

        private void canvasRoi_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isRoiDrawingMode)
                return;

            _isMouseDown = true;
            _startMousePoint = e.GetPosition(canvasRoi);

            canvasRoi.Children.Clear();

            _drawingRoiShape = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0, 180, 255)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 4, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(40, 0, 180, 255))
            };

            Canvas.SetLeft(_drawingRoiShape, _startMousePoint.X);
            Canvas.SetTop(_drawingRoiShape, _startMousePoint.Y);
            _drawingRoiShape.Width = 0;
            _drawingRoiShape.Height = 0;

            canvasRoi.Children.Add(_drawingRoiShape);
        }

        private void canvasRoi_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRoiDrawingMode || !_isMouseDown || _drawingRoiShape == null)
                return;

            Point currentPoint = e.GetPosition(canvasRoi);

            double x = Math.Min(_startMousePoint.X, currentPoint.X);
            double y = Math.Min(_startMousePoint.Y, currentPoint.Y);
            double w = Math.Abs(currentPoint.X - _startMousePoint.X);
            double h = Math.Abs(currentPoint.Y - _startMousePoint.Y);

            Canvas.SetLeft(_drawingRoiShape, x);
            Canvas.SetTop(_drawingRoiShape, y);
            _drawingRoiShape.Width = w;
            _drawingRoiShape.Height = h;
        }

        private void canvasRoi_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRoiDrawingMode || !_isMouseDown)
                return;

            _isMouseDown = false;
            _isRoiDrawingMode = false;
            canvasRoi.Cursor = Cursors.Arrow;

            if (_drawingRoiShape == null || _drawingRoiShape.Width < 10 || _drawingRoiShape.Height < 10)
            {
                canvasRoi.Children.Clear();
                _drawingRoiShape = null;
                _roiImageRect = null;
                txtRoiStatus.Text = "ROI: Toàn khung hình (vùng vẽ quá nhỏ)";
                return;
            }

            double canvasX = Canvas.GetLeft(_drawingRoiShape);
            double canvasY = Canvas.GetTop(_drawingRoiShape);
            double canvasW = _drawingRoiShape.Width;
            double canvasH = _drawingRoiShape.Height;

            Rect canvasRect = new Rect(canvasX, canvasY, canvasW, canvasH);
            _roiImageRect = CanvasToImageRect(canvasRect);

            if (_roiImageRect.HasValue)
            {
                var r = _roiImageRect.Value;
                txtRoiStatus.Text = string.Format(
                    "ROI: X={0:F0}, Y={1:F0}, W={2:F0}, H={3:F0}",
                    r.X, r.Y, r.Width, r.Height);

                if (txtOpRoiInfo != null)
                {
                    txtOpRoiInfo.Text = string.Format(
                        "ROI: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                        r.X, r.Y, r.Width, r.Height);
                }

                AppendLog(string.Format(
                    "Đã thiết lập ROI: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                    r.X, r.Y, r.Width, r.Height));

                canvasRoi.Children.Clear();
                _drawingRoiShape = null;
            }
            else
            {
                txtRoiStatus.Text = "ROI: Toàn khung hình";
            }
        }

        private void gridLiveContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawRoiOverlay();
        }

        private void RedrawRoiOverlay()
        {
            // Theo yêu cầu giao diện: không hiển thị vẽ ROI lên Live view
            canvasRoi.Children.Clear();
            _drawingRoiShape = null;
        }

        private Rect? CanvasToImageRect(Rect canvasRect)
        {
            if (imgLive.Source == null)
                return null;

            double imageW = imgLive.Source.Width;
            double imageH = imgLive.Source.Height;

            double controlW = imgLive.ActualWidth;
            double controlH = imgLive.ActualHeight;

            if (controlW <= 0 || controlH <= 0 || imageW <= 0 || imageH <= 0)
                return null;

            double scaleX = imageW / controlW;
            double scaleY = imageH / controlH;
            double scale = Math.Max(scaleX, scaleY);

            double renderW = imageW / scale;
            double renderH = imageH / scale;

            double offsetX = (controlW - renderW) / 2.0;
            double offsetY = (controlH - renderH) / 2.0;

            double imgX = (canvasRect.X - offsetX) * scale;
            double imgY = (canvasRect.Y - offsetY) * scale;
            double imgW = canvasRect.Width * scale;
            double imgH = canvasRect.Height * scale;

            imgX = Math.Max(0, Math.Min(imgX, imageW));
            imgY = Math.Max(0, Math.Min(imgY, imageH));
            imgW = Math.Max(0, Math.Min(imgW, imageW - imgX));
            imgH = Math.Max(0, Math.Min(imgH, imageH - imgY));

            if (imgW <= 0 || imgH <= 0)
                return null;

            return new Rect(imgX, imgY, imgW, imgH);
        }

        #endregion

        #region ROI Persistence

        private void AutoLoadSavedRoi()
        {
            try
            {
                var roiConfig = PalletDetection.Models.RoiConfig.LoadFromFile();
                if (roiConfig != null && roiConfig.HasRoi)
                {
                    _roiImageRect = roiConfig.ToRect();
                    if (_roiImageRect.HasValue)
                    {
                        var r = _roiImageRect.Value;
                        txtRoiStatus.Text = string.Format(
                            "ROI: X={0:F0}, Y={1:F0}, W={2:F0}, H={3:F0}",
                            r.X, r.Y, r.Width, r.Height);

                        if (txtOpRoiInfo != null)
                        {
                            txtOpRoiInfo.Text = string.Format(
                                "ROI: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                                r.X, r.Y, r.Width, r.Height);
                        }

                        AppendLog(string.Format(
                            "Tự động nạp ROI đã lưu: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                            r.X, r.Y, r.Width, r.Height));

                        RedrawRoiOverlay();
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("Lỗi tự động nạp ROI đã lưu: " + ex.Message);
            }
        }

        private void btnSaveRoi_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var roiConfig = PalletDetection.Models.RoiConfig.FromRect(_roiImageRect);
                roiConfig.SaveToFile();

                if (_roiImageRect.HasValue)
                {
                    var r = _roiImageRect.Value;
                    AppendLog(string.Format(
                        "Đã lưu cấu hình ROI xuống đĩa: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                        r.X, r.Y, r.Width, r.Height));

                    MessageBox.Show(
                        string.Format(
                            "Đã lưu cấu hình ROI thành công!\nVùng ROI: [{0:F0}, {1:F0}, {2:F0}, {3:F0}]",
                            r.X, r.Y, r.Width, r.Height),
                        "Lưu ROI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    AppendLog("Đã lưu cấu hình ROI xuống đĩa: Toàn khung hình.");
                    MessageBox.Show(
                        "Đã lưu cấu hình ROI (Toàn khung hình) thành công!",
                        "Lưu ROI",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khi lưu cấu hình ROI: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region RCS Task Configuration

        private void InitRcsTaskConfigUi()
        {
            try
            {
                if (_rcsCycleManager?.Config != null)
                {
                    if (txtRcsUrl != null)
                    {
                        try
                        {
                            txtRcsUrl.Text = _rcsCycleManager.Config.GetFullEndpointUrl();
                        }
                        catch
                        {
                            txtRcsUrl.Text = PalletDetection.Rcs.Config.RcsApiConfig.DefaultBaseUrl + PalletDetection.Rcs.Config.RcsApiConfig.DefaultEndpointPath;
                        }
                    }
                    if (txtTaskType != null && !string.IsNullOrWhiteSpace(_rcsCycleManager.Config.TaskType))
                    {
                        txtTaskType.Text = _rcsCycleManager.Config.TaskType;
                    }
                    if (txtRobotCode != null)
                    {
                        txtRobotCode.Text = _rcsCycleManager.Config.RobotCode ?? string.Empty;
                    }
                    if (txtPickupPosition != null && !string.IsNullOrWhiteSpace(_rcsCycleManager.Config.PickupPosition))
                    {
                        txtPickupPosition.Text = _rcsCycleManager.Config.PickupPosition;
                    }
                    if (txtDropoffPosition != null && !string.IsNullOrWhiteSpace(_rcsCycleManager.Config.DropoffPosition))
                    {
                        txtDropoffPosition.Text = _rcsCycleManager.Config.DropoffPosition;
                    }
                }
            }
            catch { }
        }

        private void btnSaveRcsConfig_Click(object sender, RoutedEventArgs e)
        {
            string rcsUrl = txtRcsUrl?.Text?.Trim();
            string taskType = txtTaskType?.Text?.Trim();
            string robotCode = txtRobotCode?.Text?.Trim() ?? string.Empty;
            string pickup = txtPickupPosition?.Text?.Trim();
            string dropoff = txtDropoffPosition?.Text?.Trim();

            Uri parsedUri;
            if (string.IsNullOrWhiteSpace(rcsUrl) ||
                !Uri.TryCreate(rcsUrl, UriKind.Absolute, out parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("Vui lòng nhập URL RCS hợp lệ (bắt đầu bằng http:// hoặc https://).", "Cấu hình RCS", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtRcsUrl?.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(taskType) || taskType.Length > 32)
            {
                MessageBox.Show("Vui lòng nhập Task Type hợp lệ (1–32 ký tự).", "Cấu hình RCS", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTaskType?.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(pickup))
            {
                MessageBox.Show("Vui lòng nhập mã vị trí Pick-up Position.", "Cấu hình RCS", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtPickupPosition?.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(dropoff))
            {
                MessageBox.Show("Vui lòng nhập mã vị trí Drop-off Position.", "Cấu hình RCS", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtDropoffPosition?.Focus();
                return;
            }

            if (_rcsCycleManager?.Config != null)
            {
                _rcsCycleManager.UpdateTaskConfig(rcsUrl, taskType, robotCode, pickup, dropoff);
                _rcsCycleManager.Config.SaveToFile();
                string fullUrl = _rcsCycleManager.Config.GetFullEndpointUrl();
                if (txtRcsUrl != null) txtRcsUrl.Text = fullUrl;

                string robotDisplay = string.IsNullOrWhiteSpace(robotCode) ? "Tự chọn (RCS auto)" : robotCode;
                AppendLog($"RCS: Đã lưu cấu hình Task V4.2: URL={fullUrl}, TaskType={taskType}, RobotCode={robotDisplay}, Pick-up={pickup}, Drop-off={dropoff}");

                _isRcsReachable = null;
                _lastRcsCallFailed = false;
                _rcsHealthTooltip = "RCS: Đang kiểm tra kết nối...";
                _rcsHealthStatusText = "RCS: Đang kiểm tra kết nối...";
                UpdateHeaderIndicators();
                UpdateRcsIdleStatusText();
                _ = CheckRcsConnectivityAsync(logStateChange: true);

                MessageBox.Show($"Đã lưu cấu hình RCS Task (V4.2) thành công:\nURL: {fullUrl}\nTask Type: {taskType}\nRobot Code: {robotDisplay}\nPick-up Position: {pickup}\nDrop-off Position: {dropoff}", "Thành Công", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        #endregion

        #region Operation Logs & HMI Dashboard Updates

        private void AddLogEntry(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing || string.IsNullOrEmpty(line))
                    return;

                _operationLogs.Insert(0, line);
                if (_operationLogs.Count > 500)
                {
                    _operationLogs.RemoveAt(_operationLogs.Count - 1);
                }
            }), DispatcherPriority.Background);
        }

        private void AppendLog(string message)
        {
            string timeStamp = DateTime.Now.ToString("HH:mm:ss");
            AddLogEntry($"[{timeStamp}] {message}");
        }

        private void ClearOperationLogs()
        {
            _operationLogs.Clear();
        }

        private async Task CheckRcsConnectivityAsync(bool logStateChange)
        {
            if (_isClosing || _rcsCycleManager?.Config == null)
                return;

            if (Interlocked.CompareExchange(ref _isCheckingRcsHealth, 1, 0) != 0)
                return;

            try
            {
                string endpointUrl;
                try
                {
                    endpointUrl = _rcsCycleManager.Config.GetFullEndpointUrl();
                }
                catch (Exception ex)
                {
                    bool? prev = _isRcsReachable;
                    _isRcsReachable = false;
                    _rcsHealthTooltip = $"RCS: Cấu hình URL không hợp lệ ({ex.Message})";
                    _rcsHealthStatusText = "RCS: URL không hợp lệ";
                    if (logStateChange && prev != false)
                    {
                        AppendLog($"RCS: Cấu hình URL không hợp lệ — {ex.Message}");
                    }
                    UpdateHeaderIndicators();
                    UpdateRcsIdleStatusText();
                    return;
                }

                CancellationToken token = _rcsHealthCts != null ? _rcsHealthCts.Token : CancellationToken.None;
                RcsConnectionCheckResult probe = await RcsConnectionChecker.CheckTcpReachableAsync(endpointUrl, 2000, token);

                if (_isClosing)
                    return;

                bool? previous = _isRcsReachable;
                _isRcsReachable = probe.IsReachable;

                if (probe.IsReachable)
                {
                    if (previous == false)
                    {
                        _lastRcsCallFailed = false;
                    }
                    _rcsHealthTooltip = $"RCS: Đã kết nối {probe.EndpointDisplay} ({probe.DurationMs} ms)";
                    _rcsHealthStatusText = $"RCS: Đã kết nối ({probe.EndpointDisplay})";
                    if (logStateChange && previous != true)
                    {
                        AppendLog($"RCS: Kết nối TCP thành công tới {probe.EndpointDisplay} ({probe.DurationMs} ms).");
                    }
                }
                else
                {
                    string target = string.IsNullOrWhiteSpace(probe.EndpointDisplay) ? (endpointUrl ?? "N/A") : probe.EndpointDisplay;
                    _rcsHealthTooltip = $"RCS: Không kết nối được {target} — {probe.ErrorMessage}";
                    _rcsHealthStatusText = $"RCS: Mất kết nối ({target})";
                    if (logStateChange && previous != false)
                    {
                        AppendLog($"RCS: Không kết nối được tới {target} — {probe.ErrorMessage}");
                    }
                }

                UpdateHeaderIndicators();
                UpdateRcsIdleStatusText();
            }
            catch (Exception ex)
            {
                if (!_isClosing)
                {
                    bool? previous = _isRcsReachable;
                    _isRcsReachable = false;
                    _rcsHealthTooltip = $"RCS: Lỗi kiểm tra kết nối — {ex.Message}";
                    _rcsHealthStatusText = "RCS: Lỗi kiểm tra kết nối";
                    if (logStateChange && previous != false)
                    {
                        AppendLog($"RCS: Lỗi kiểm tra kết nối — {ex.Message}");
                    }
                    UpdateHeaderIndicators();
                    UpdateRcsIdleStatusText();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isCheckingRcsHealth, 0);
            }
        }

        private void UpdateRcsIdleStatusText()
        {
            if (_isClosing || _realTimeState == RealTimeState.Running || txtRcsCycleStatus == null)
                return;

            txtRcsCycleStatus.Text = _rcsHealthStatusText;
            if (_isRcsReachable == null)
            {
                txtRcsCycleStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
            else if (_isRcsReachable == true && !_lastRcsCallFailed)
            {
                txtRcsCycleStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
            }
            else
            {
                txtRcsCycleStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
            }
        }

        private void UpdateHeaderIndicators()
        {
            bool camReady = _cameraManager != null && _cameraManager.IsConnected && _cameraManager.IsLiveRunning;
            bool aiReady = _detector != null && _detector.IsLoaded;

            if (ellCameraDot != null)
                ellCameraDot.Fill = new SolidColorBrush(camReady ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));

            if (ellAiDot != null)
                ellAiDot.Fill = new SolidColorBrush(aiReady ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));

            if (ellRcsDot != null)
            {
                Color rcsColor;
                if (_isRcsReachable == null)
                    rcsColor = Color.FromRgb(148, 163, 184);
                else if (_isRcsReachable == true && !_lastRcsCallFailed)
                    rcsColor = Color.FromRgb(34, 197, 94);
                else
                    rcsColor = Color.FromRgb(239, 68, 68);

                ellRcsDot.Fill = new SolidColorBrush(rcsColor);
            }

            if (pnlRcsIndicator != null && !string.IsNullOrEmpty(_rcsHealthTooltip))
            {
                pnlRcsIndicator.ToolTip = _rcsHealthTooltip;
            }

            if (bdrModePill != null && txtModePill != null)
            {
                if (_realTimeState == RealTimeState.Running && _isAutoSendRcsTaskEnabled)
                {
                    txtModePill.Text = "AUTO";
                    bdrModePill.Background = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                }
                else if (_realTimeState == RealTimeState.Running)
                {
                    txtModePill.Text = "MANUAL";
                    bdrModePill.Background = new SolidColorBrush(Color.FromRgb(37, 99, 235));
                }
                else
                {
                    txtModePill.Text = "STANDBY";
                    bdrModePill.Background = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                }
            }
        }

        private void SetStatusBanner(string text, string severity)
        {
            if (txtSystemStatus != null)
                txtSystemStatus.Text = text;

            Color bg, border, fg;
            switch (severity)
            {
                case "Ready":
                    bg = Color.FromRgb(220, 252, 231);
                    border = Color.FromRgb(74, 222, 128);
                    fg = Color.FromRgb(22, 101, 52);
                    break;
                case "Working":
                    bg = Color.FromRgb(219, 234, 254);
                    border = Color.FromRgb(96, 165, 250);
                    fg = Color.FromRgb(30, 64, 175);
                    break;
                case "Warning":
                    bg = Color.FromRgb(254, 243, 199);
                    border = Color.FromRgb(251, 191, 36);
                    fg = Color.FromRgb(146, 64, 14);
                    break;
                case "Error":
                    bg = Color.FromRgb(254, 226, 226);
                    border = Color.FromRgb(248, 113, 113);
                    fg = Color.FromRgb(153, 27, 27);
                    break;
                default:
                    bg = Color.FromRgb(226, 232, 240);
                    border = Color.FromRgb(203, 213, 225);
                    fg = Color.FromRgb(51, 65, 85);
                    break;
            }

            if (bdrStatusBanner != null)
            {
                bdrStatusBanner.Background = new SolidColorBrush(bg);
                bdrStatusBanner.BorderBrush = new SolidColorBrush(border);
            }
            if (txtSystemStatus != null)
                txtSystemStatus.Foreground = new SolidColorBrush(fg);
        }

        private void OnRcsCycleStateChanged(RcsCycleState state, string statusMsg)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing || _realTimeState != RealTimeState.Running)
                    return;

                switch (state)
                {
                    case RcsCycleState.WaitingForBox:
                        SetStatusBanner("ĐANG VẬN HÀNH — CHỜ THÙNG HÀNG", "Ready");
                        break;
                    case RcsCycleState.BoxStabilizing:
                        SetStatusBanner("ĐANG XÁC NHẬN THÙNG ỔN ĐỊNH (2.0s)...", "Working");
                        break;
                    case RcsCycleState.WaitingForHumanDeparture:
                        if (!string.IsNullOrEmpty(statusMsg) && statusMsg.Contains("Có người"))
                            SetStatusBanner("PHÁT HIỆN NGƯỜI TRONG VÙNG — TẠM HOÃN GỬI TASK", "Warning");
                        else
                            SetStatusBanner("CHỜ XÁC NHẬN AN TOÀN KHÔNG NGƯỜI...", "Working");
                        break;
                    case RcsCycleState.TaskTriggered:
                    case RcsCycleState.WaitingForBoxClear:
                        if (_lastRcsCallFailed)
                            SetStatusBanner("LỖI GỬI TASK RCS — KHÔNG TỰ GỬI LẠI", "Error");
                        else
                        {
                            string taskCode = _rcsCycleManager?.LastTaskCode;
                            string label = string.IsNullOrEmpty(taskCode)
                                ? "ĐÃ PHÁT LỆNH RCS — CHỜ LẤY THÙNG"
                                : $"ĐÃ GỬI TASK RCS ({taskCode}) — CHỜ LẤY THÙNG";
                            SetStatusBanner(label, "Working");
                        }
                        break;
                }
            }), DispatcherPriority.Normal);
        }

        private void OnRcsTaskCompleted(RcsCallResult result, string pickPos, string dropPos)
        {
            if (result == null)
                return;

            DateTime completedAt = DateTime.Now;
            string route = string.IsNullOrWhiteSpace(pickPos) && string.IsNullOrWhiteSpace(dropPos)
                ? ""
                : $"{pickPos} → {dropPos}";

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosing)
                    return;

                _totalTaskCount++;
                if (result.IsSuccess) _okTaskCount++;
                else _ngTaskCount++;

                if (txtKpiTotal != null) txtKpiTotal.Text = _totalTaskCount.ToString("N0");
                if (txtKpiOk != null) txtKpiOk.Text = _okTaskCount.ToString("N0");
                if (txtKpiNg != null) txtKpiNg.Text = _ngTaskCount.ToString("N0");

                _lastRcsCallFailed = !result.IsSuccess;
                if (result.IsSuccess)
                {
                    _isRcsReachable = true;
                    _rcsHealthStatusText = "RCS: Đã kết nối";
                }
                else if (result.Status == RcsCallStatus.NetworkError || result.Status == RcsCallStatus.Timeout)
                {
                    _isRcsReachable = false;
                    _rcsHealthTooltip = $"RCS: Lỗi kết nối API — {result.ErrorMessage}";
                    _rcsHealthStatusText = "RCS: Mất kết nối";
                }
                UpdateHeaderIndicators();

                if (result.IsSuccess)
                {
                    if (ellResultCircle != null) ellResultCircle.Fill = new SolidColorBrush(Color.FromRgb(22, 163, 74));
                    if (pathResultOk != null) pathResultOk.Visibility = Visibility.Visible;
                    if (pathResultNg != null) pathResultNg.Visibility = Visibility.Collapsed;
                    if (ellResultIdle != null) ellResultIdle.Visibility = Visibility.Collapsed;

                    if (txtResultStatus != null)
                    {
                        txtResultStatus.Text = "OK";
                        txtResultStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    }
                    if (txtLastTaskCode != null) txtLastTaskCode.Text = string.IsNullOrWhiteSpace(result.CreatedTaskCode) ? "-" : result.CreatedTaskCode;
                    if (txtLastTaskTime != null) txtLastTaskTime.Text = string.IsNullOrEmpty(route) ? completedAt.ToString("HH:mm:ss") : $"{completedAt:HH:mm:ss} ({route})";
                    if (txtLastTaskDuration != null) txtLastTaskDuration.Text = result.DurationMs > 0 ? $"{result.DurationMs} ms" : "-";
                    if (txtRcsCycleStatus != null)
                    {
                        txtRcsCycleStatus.Text = "RCS: Đã tạo task thành công — Chờ lấy thùng";
                        txtRcsCycleStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    }
                }
                else
                {
                    if (ellResultCircle != null) ellResultCircle.Fill = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    if (pathResultOk != null) pathResultOk.Visibility = Visibility.Collapsed;
                    if (pathResultNg != null) pathResultNg.Visibility = Visibility.Visible;
                    if (ellResultIdle != null) ellResultIdle.Visibility = Visibility.Collapsed;

                    if (txtResultStatus != null)
                    {
                        txtResultStatus.Text = "NG";
                        txtResultStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    }
                    if (txtLastTaskCode != null) txtLastTaskCode.Text = "-";
                    if (txtLastTaskTime != null) txtLastTaskTime.Text = string.IsNullOrEmpty(route) ? completedAt.ToString("HH:mm:ss") : $"{completedAt:HH:mm:ss} ({route})";
                    if (txtLastTaskDuration != null) txtLastTaskDuration.Text = result.DurationMs > 0 ? $"{result.DurationMs} ms" : "-";
                    if (txtRcsCycleStatus != null)
                    {
                        txtRcsCycleStatus.Text = $"Lỗi: {result.ErrorMessage}";
                        txtRcsCycleStatus.Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                    }
                    if (_realTimeState == RealTimeState.Running)
                        SetStatusBanner("LỖI GỬI TASK RCS — KHÔNG TỰ GỬI LẠI", "Error");
                }

                string detailMsg = result.IsSuccess
                    ? $"Thành công · Mã task: {result.CreatedTaskCode}"
                    : result.ErrorMessage;

                _taskHistory.Insert(0, new TaskHistoryItem
                {
                    Time = completedAt,
                    TaskCode = result.CreatedTaskCode,
                    RouteText = route,
                    IsSuccess = result.IsSuccess,
                    DurationMs = result.DurationMs,
                    Message = detailMsg
                });

                if (_taskHistory.Count > 100)
                {
                    _taskHistory.RemoveAt(_taskHistory.Count - 1);
                }
            }), DispatcherPriority.Normal);
        }

        private void btnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (pnlMonitoringPanel != null) pnlMonitoringPanel.Visibility = Visibility.Collapsed;
            if (pnlSettingsPanel != null) pnlSettingsPanel.Visibility = Visibility.Visible;
        }

        private void btnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (pnlSettingsPanel != null) pnlSettingsPanel.Visibility = Visibility.Collapsed;
            if (pnlMonitoringPanel != null) pnlMonitoringPanel.Visibility = Visibility.Visible;
        }

        private void btnOpenHistory_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new HistoryLogWindow(_taskHistory, _operationLogs, ClearOperationLogs)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }

        #endregion

        #region Window Closing & Cleanup

        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isCleanupDone)
            {
                return; // Đã cleanup xong -> Cho phép hoàn tất đóng Window
            }

            if (_isCleaningUp)
            {
                e.Cancel = true; // Chặn đệ quy khi Close() được gọi trong quá trình dọn dẹp
                return;
            }

            // Hoãn đóng cửa sổ để hoàn tất dọn dẹp bất đồng bộ an toàn
            e.Cancel = true;
            _isCleaningUp = true;
            _isClosing = true;

            try
            {
                // 1. Dừng Live timer và bộ kiểm tra kết nối RCS
                _liveTimer?.Stop();
                _rcsHealthTimer?.Stop();
                try { _rcsHealthCts?.Cancel(); } catch { }
                try { _rcsHealthCts?.Dispose(); } catch { }

                // 2. Dừng Real Time an toàn nếu đang chạy
                if (_realTimeState != RealTimeState.Stopped && _realTimeCts != null)
                {
                    _realTimeState = RealTimeState.Stopping;
                    _realTimeCts.Cancel();

                    if (_realTimeTask != null)
                    {
                        try
                        {
                            await Task.WhenAny(_realTimeTask, Task.Delay(3000));
                        }
                        catch { }
                    }
                }

                // 3. Giải phóng tài nguyên camera, detector và rcs cycle manager
                try
                {
                    _cameraManager?.Dispose();
                }
                catch { }

                try
                {
                    _detector?.Dispose();
                }
                catch { }

                try
                {
                    _rcsCycleManager?.Dispose();
                }
                catch { }
            }
            catch { }
            finally
            {
                _isCleanupDone = true;
                _isCleaningUp = false;
                Close(); // Lần gọi Close() này sẽ thoát trực tiếp vì _isCleanupDone == true
            }
        }

        #endregion
    }
}
