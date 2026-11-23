using CapabilityCSharpDemo;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PalletDetection.Camera
{
    public class HikvisionCamera
    {
        private int _userId = -1;

        private bool _initialized = false;
        public bool Initialize()
        {
            bool result = CHCNetSDK.NET_DVR_Init();

            _initialized = result;

            return result;
        }
        public void Cleanup()
        {
            CHCNetSDK.NET_DVR_Cleanup();

            _initialized = false;
        }
        public bool Login(
    string ip,
    ushort port,
    string username,
    string password)
        {
            CHCNetSDK.NET_DVR_DEVICEINFO_V30 deviceInfo =
                new CHCNetSDK.NET_DVR_DEVICEINFO_V30();

            _userId = CHCNetSDK.NET_DVR_Login_V30(
                ip,
                port,
                username,
                password,
                ref deviceInfo);

            return _userId >= 0;
        }
        public void Logout()
        {
            if (_userId >= 0)
            {
                CHCNetSDK.NET_DVR_Logout(_userId);

                _userId = -1;
            }
        }
        public uint GetLastError()
        {
            return CHCNetSDK.NET_DVR_GetLastError();
        }
        public int UserID
        {
            get
            {
                return _userId;
            }
        }
    }

}
