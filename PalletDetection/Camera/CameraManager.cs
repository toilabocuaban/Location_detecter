using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using PalletDetection.Vision;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace PalletDetection.Camera
{
    /// <summary>
    /// Quản lý Camera Hikvision và RTSP.
    /// UI chỉ làm việc với class này.
    /// </summary>
    public class CameraManager : IDisposable
    {
        #region Fields

        private readonly HikvisionCamera _hikvisionCamera;

        private readonly FrameBuffer _frameBuffer;

        private readonly RtspCamera _rtspCamera;

        #endregion

        #region Constructor

        public CameraManager()
        {
            _hikvisionCamera = new HikvisionCamera();

            _frameBuffer = new FrameBuffer();

            _rtspCamera = new RtspCamera(_frameBuffer);
        }

        #endregion

        #region SDK

        public bool Initialize()
        {
            return _hikvisionCamera.Initialize();
        }

        public bool Login(
            string ip,
            ushort port,
            string username,
            string password)
        {
            return _hikvisionCamera.Login(
                ip,
                port,
                username,
                password);
        }

        public void Logout()
        {
            _hikvisionCamera.Logout();
        }

        public uint GetLastError()
        {
            return _hikvisionCamera.GetLastError();
        }

        public void Cleanup()
        {
            _hikvisionCamera.Cleanup();
        }

        #endregion

        #region RTSP

        public bool ConnectRtsp(
            string ip,
            string username,
            string password)
        {
            return _rtspCamera.Connect(
                ip,
                username,
                password);
        }

        public void StartLive()
        {
            _rtspCamera.Start();
        }

        public void StopLive()
        {
            _rtspCamera.Stop();
        }

        public bool IsLiveRunning => _rtspCamera != null && _rtspCamera.IsRunning;

        public bool IsConnected => _rtspCamera != null && _rtspCamera.IsConnected;

        #endregion

        #region Image

        /// <summary>
        /// Lấy BitmapSource mới nhất để hiển thị.
        /// </summary>
        public BitmapSource GetCurrentImage()
        {
            Mat frame = _frameBuffer.GetLatestFrame();

            if (frame == null)
                return null;

            try
            {
                return ImageConverter.ToBitmapSource(frame);
            }
            finally
            {
                frame.Dispose();
            }
        }

        /// <summary>
        /// Lấy bản sao Mat của frame mới nhất.
        /// Người gọi phải Dispose() sau khi dùng xong.
        /// </summary>
        public Mat GetCurrentMat()
        {
            return _frameBuffer.GetLatestFrame();
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            StopLive();

            Logout();

            Cleanup();

            _frameBuffer.Dispose();

            _rtspCamera.Dispose();
        }

        #endregion
    }
}