using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace PalletDetection.Vision
{
    /// <summary>
    /// Đọc luồng RTSP từ Camera Hikvision.
    /// </summary>
    public class RtspCamera : IDisposable
    {
        #region Fields

        private readonly VideoCapture _capture;
        private readonly FrameBuffer _frameBuffer;

        private Task _captureTask;
        private CancellationTokenSource _cts;

        private string _rtspUrl;

        #endregion

        #region Properties

        public bool IsRunning { get; private set; }

        public bool IsConnected
        {
            get
            {
                return _capture.IsOpened();
            }
        }

        #endregion

        #region Constructor

        public RtspCamera(FrameBuffer frameBuffer)
        {
            _capture = new VideoCapture();

            _frameBuffer = frameBuffer;
        }

        #endregion

        #region Public

        public bool Connect(
            string ip,
            string username,
            string password,
            int port = 554,
            int channel = 101)
        {
            _rtspUrl = string.Format(
                "rtsp://{0}:{1}@{2}:{3}/Streaming/Channels/{4}",
                username,
                password,
                ip,
                port,
                channel);

            return _capture.Open(_rtspUrl);
        }

        public void Start()
        {
            if (IsRunning)
                return;

            _cts = new CancellationTokenSource();

            IsRunning = true;

            _captureTask = Task.Run(() =>
            {
                CaptureLoop(_cts.Token);

            }, _cts.Token);
        }

        public void Stop()
        {
            if (!IsRunning)
                return;

            if (_cts != null)
            {
                _cts.Cancel();
            }

            if (_captureTask != null)
            {
                _captureTask.Wait();
            }

            IsRunning = false;
        }

        public void Disconnect()
        {
            Stop();

            if (_capture.IsOpened())
            {
                _capture.Release();
            }
        }

        #endregion

        #region Private

        private void CaptureLoop(
            CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_capture.IsOpened())
                    {
                        _capture.Open(_rtspUrl);

                        Thread.Sleep(1000);

                        continue;
                    }

                    using (Mat frame = new Mat())
                    {
                        bool ok = _capture.Read(frame);

                        if (!ok)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        if (frame.Empty())
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        _frameBuffer.Update(frame);
                    }
                }
                catch
                {
                    Thread.Sleep(1000);
                }
            }
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Disconnect();

            _capture.Dispose();

            if (_cts != null)
            {
                _cts.Dispose();
            }
        }

        #endregion
    }
}