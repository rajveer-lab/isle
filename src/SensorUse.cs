using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Isle
{
    /// <summary>
    /// Live "who is using the mic / camera right now", straight from the OS instead of the privacy registry
    /// (newer Windows builds stopped writing CapabilityAccessManager\ConsentStore, so that alone misses everything).
    ///  - microphone: active Core Audio capture sessions on every active input device, with their process ids
    ///  - camera:     Media Foundation's sensor activity monitor (the same feed the Windows camera indicator uses)
    /// </summary>
    static class SensorUse
    {
        // ================================ microphone ================================

        /// <summary>Process ids that are capturing from any microphone right now (Isle itself excluded).</summary>
        public static HashSet<uint> MicPids()
        {
            var pids = new HashSet<uint>();
            object enumObj = null, collObj = null;
            try
            {
                // (not a [ComImport] class: AudioService already maps this CLSID to its own type, and the CLR would hand that back)
                enumObj = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.EnumAudioEndpoints(1 /*eCapture*/, 1 /*DEVICE_STATE_ACTIVE*/, out var coll) != 0 || coll == null) return pids;
                collObj = coll;
                coll.GetCount(out uint n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice dev = null; object mgrObj = null, sesEnumObj = null;
                    try
                    {
                        if (coll.Item(i, out dev) != 0 || dev == null) continue;
                        var iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out mgrObj) != 0 || mgrObj == null) continue;
                        var mgr = (IAudioSessionManager2)mgrObj;
                        if (mgr.GetSessionEnumerator(out var sesEnum) != 0 || sesEnum == null) continue;
                        sesEnumObj = sesEnum;
                        sesEnum.GetCount(out int count);
                        for (int s = 0; s < count; s++)
                        {
                            object ctlObj = null;
                            try
                            {
                                if (sesEnum.GetSession(s, out ctlObj) != 0 || ctlObj == null) continue;
                                var ctl = (IAudioSessionControl2)ctlObj;
                                if (ctl.GetState(out int state) != 0 || state != 1 /*AudioSessionStateActive*/) continue;
                                if (ctl.IsSystemSoundsSession() == 0) continue;   // S_OK = it is the system sounds session
                                if (ctl.GetProcessId(out uint pid) != 0 || pid == 0 || pid == SelfPid) continue;
                                pids.Add(pid);
                            }
                            catch { }
                            finally { Release(ctlObj); }
                        }
                    }
                    catch { }
                    finally { Release(sesEnumObj); Release(mgrObj); Release(dev); }
                }
            }
            catch (Exception ex) { App.Log(ex); }
            finally { Release(collObj); Release(enumObj); }
            return pids;
        }

        static readonly uint SelfPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        static void Release(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); } catch { }
        }

        // ================================ camera ================================

        static readonly object _camLock = new object();
        static HashSet<uint> _camPids = new HashSet<uint>();
        static IMFSensorActivityMonitor _monitor;
        static CameraCallback _callback;

        /// <summary>Process ids streaming from any camera right now (as of the last report).</summary>
        public static HashSet<uint> CameraPids()
        {
            lock (_camLock) return new HashSet<uint>(_camPids);
        }

        /// <summary>Starts watching cameras; <paramref name="changed"/> runs (on a worker thread) whenever that changes.</summary>
        public static bool StartCameraWatch(Action changed)
        {
            try
            {
                MFStartup(0x00020070, 1 /*MFSTARTUP_LITE*/);
                _callback = new CameraCallback(changed);
                if (MFCreateSensorActivityMonitor(_callback, out _monitor) != 0 || _monitor == null) return false;
                return _monitor.Start() == 0;
            }
            catch (Exception ex) { App.Log(ex); return false; }
        }

        public static void StopCameraWatch()
        {
            try { _monitor?.Stop(); } catch { }
            Release(_monitor);
            _monitor = null;
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        sealed class CameraCallback : IMFSensorActivitiesReportCallback
        {
            readonly Action _changed;
            public CameraCallback(Action changed) { _changed = changed; }

            public int OnActivitiesReport(IMFSensorActivitiesReport report)
            {
                var now = new HashSet<uint>();
                try
                {
                    report.GetCount(out uint devices);
                    for (uint d = 0; d < devices; d++)
                    {
                        if (report.GetActivityReport(d, out var dev) != 0 || dev == null) continue;
                        try
                        {
                            dev.GetProcessCount(out uint procs);
                            for (uint p = 0; p < procs; p++)
                            {
                                if (dev.GetProcessActivity(p, out var act) != 0 || act == null) continue;
                                try
                                {
                                    act.GetStreamingState(out int streaming);
                                    act.GetProcessId(out uint pid);
                                    if (streaming != 0 && pid != 0 && pid != SelfPid) now.Add(pid);
                                }
                                finally { Release(act); }
                            }
                        }
                        finally { Release(dev); }
                    }
                }
                catch (Exception ex) { App.Log(ex); }
                finally { Release(report); }

                bool diff;
                lock (_camLock) { diff = !now.SetEquals(_camPids); _camPids = now; }
                if (diff) { try { _changed?.Invoke(); } catch { } }
                return 0;
            }
        }

        // ================================ processes ================================

        /// <summary>Full exe path of a process (works for packaged / other-user-session apps where Process.MainModule fails).</summary>
        public static string ExePath(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // ================================ interop ================================

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfsensorgroup.dll")] static extern int MFCreateSensorActivityMonitor(IMFSensorActivitiesReportCallback callback, out IMFSensorActivityMonitor monitor);

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr ctx);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr ctx);
            [PreserveSig] int GetGroupingParam(out Guid group);
            [PreserveSig] int SetGroupingParam(IntPtr group, IntPtr ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
        }

        [ComImport, Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSensorActivityMonitor
        {
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
        }

        [ComImport, Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSensorActivitiesReportCallback
        {
            [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
        }

        [ComImport, Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSensorActivitiesReport
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetActivityReport(uint index, out IMFSensorActivityReport report);
        }

        [ComImport, Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSensorActivityReport
        {
            [PreserveSig] int GetFriendlyName(IntPtr name, uint cch, out uint written);
            [PreserveSig] int GetSymbolicLink(IntPtr link, uint cch, out uint written);
            [PreserveSig] int GetProcessCount(out uint count);
            [PreserveSig] int GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
        }

        [ComImport, Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMFSensorProcessActivity
        {
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int GetStreamingState(out int streaming);
        }
    }
}
