using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FocusKeep
{
    /// <summary>
    /// 屏幕常亮。SetThreadExecutionState 的 ES_CONTINUOUS 状态是**按线程**记录的，
    /// 所以固定用一个专用后台线程持有；线程退出时状态自动解除。
    /// </summary>
    internal sealed class PowerKeeper : IDisposable
    {
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly AutoResetEvent _kick = new AutoResetEvent(false);
        private readonly Thread _worker;

        private volatile bool _screenOn;
        private int _applyCount;
        private int _lastError;

        public PowerKeeper()
        {
            _worker = new Thread(Worker);
            _worker.IsBackground = true;
            _worker.Name = "FocusKeep.Power";
            _worker.Start();
        }

        public bool IsOn { get { return _screenOn; } }
        public int ApplyCount { get { return _applyCount; } }
        public int LastError { get { return _lastError; } }

        /// <summary>切换屏幕常亮；立即生效，并由后台线程每 20 秒重申一次（防止被别的程序重置）。</summary>
        public void Apply(bool screenOn)
        {
            _screenOn = screenOn;
            _kick.Set();
        }

        private void Worker()
        {
            try
            {
                while (true)
                {
                    int wait;
                    try { wait = _stop.WaitOne(0) ? 0 : 1; }
                    catch (ObjectDisposedException) { break; }
                    if (wait == 0) break;

                    uint flags = ES_CONTINUOUS;
                    if (_screenOn) flags |= ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;

                    if (SetThreadExecutionState(flags) == 0)
                        _lastError = Marshal.GetLastWin32Error();

                    Interlocked.Increment(ref _applyCount);

                    int idx;
                    try { idx = WaitHandle.WaitAny(new WaitHandle[] { _stop, _kick }, 20000); }
                    catch (ObjectDisposedException) { break; }

                    if (idx == 0) break;
                    if (idx == 1) _kick.Reset();
                }
            }
            catch (Exception ex)
            {
                // 后台线程的异常绝不能冒出去，否则会直接结束进程
                System.Diagnostics.Debug.WriteLine("PowerKeeper 线程异常：" + ex.Message);
            }
            finally
            {
                // 线程退出前主动释放，避免留下悬挂的电源请求
                try { SetThreadExecutionState(ES_CONTINUOUS); }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            bool joined = false;
            try
            {
                _stop.Set();
                joined = _worker.Join(2000);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("PowerKeeper 释放失败：" + ex.Message);
            }

            // 只有确认线程已经退出才关句柄：否则等待中的线程会踩到已释放对象。
            // 没等回来就交给进程退出时回收，电源请求随线程一起消失。
            if (joined)
            {
                try { _stop.Close(); _kick.Close(); }
                catch (Exception) { }
            }
        }
    }
}
