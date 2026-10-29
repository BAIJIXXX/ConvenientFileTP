using System;
using System.Threading;

namespace ConvenientFileTP.Core
{
    /// <summary>
    /// One running instance per profile. Two profiles can run side by side (used for
    /// two-device end-to-end tests on the same machine).
    /// </summary>
    public static class SingleInstance
    {
        private static Mutex _mutex;

        public static bool TryAcquire(string profile)
        {
            string name = "ConvenientFileTP_" + (string.IsNullOrEmpty(profile) ? "default" : profile);
            try
            {
                bool created;
                _mutex = new Mutex(true, name, out created);
                if (!created)
                {
                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("单实例检测失败，继续启动", ex);
                return true;
            }
        }

        public static void Release()
        {
            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch { }
        }
    }
}
