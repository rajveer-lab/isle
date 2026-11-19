using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace Isle
{
    /// <summary>Default speaker volume / mute / peak level and default microphone mute, via Windows Core Audio.</summary>
    public sealed class AudioService : IDisposable
    {
        static readonly Guid SelfContext = new Guid("6f1e3c7a-2b8d-4b8e-9b55-2d6a3f0e9c11");
        readonly Dispatcher _ui;
        readonly IMMDeviceEnumerator _enum;
        readonly VolumeCallback _volumeCallback, _micCallback;
        readonly DeviceCallback _deviceCallback;
        IAudioEndpointVolume _vol, _mic;
        IAudioMeterInformation _meter;

        public float Volume { get; private set; }
        public bool Muted { get; private set; }
        public bool Available => _vol != null;
        public bool MicMuted { get; private set; }
        public bool MicAvailable => _mic != null;
        public string DeviceName { get; private set; } = "";

        /// <summary>UI thread. Argument: true when something other than Isle changed the volume (keys, flyout...).</summary>
        public event Action<bool> VolumeChanged;
        /// <summary>UI thread. Argument: true when the change did not come from Isle (e.g. the laptop's mic key).</summary>
        public event Action<bool> MicChanged;
        /// <summary>UI thread. The default output switched (AirPods/headphones connected, etc.). Argument: short device name.</summary>
        public event Action<string> OutputDeviceSwitched;

        public AudioService(Dispatcher ui)
        {
            _ui = ui;
            _volumeCallback = new VolumeCallback(this, false);
            _micCallback = new VolumeCallback(this, true);
            _deviceCallback = new DeviceCallback(this);
            try
            {
                _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                _enum.RegisterEndpointNotificationCallback(_deviceCallback);
                Bind();
                BindMic();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        // ---------------- speakers ----------------

        void Bind()
        {
            Unbind();
            if (_enum == null || _enum.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out IMMDevice dev) != 0 || dev == null) return;
            try
            {
                DeviceName = ShortName(FriendlyName(dev));
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) == 0) _vol = (IAudioEndpointVolume)o;
                iid = typeof(IAudioMeterInformation).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) _meter = (IAudioMeterInformation)o;
            }
            finally { Marshal.ReleaseComObject(dev); }
            if (_vol != null)
            {
                _vol.RegisterControlChangeNotify(_volumeCallback);
                if (_vol.GetMasterVolumeLevelScalar(out float v) == 0) Volume = v;
                if (_vol.GetMute(out bool m) == 0) Muted = m;
            }
        }

        void Unbind()
        {
            if (_vol != null)
            {
                try { _vol.UnregisterControlChangeNotify(_volumeCallback); } catch { }
                Marshal.ReleaseComObject(_vol);
                _vol = null;
            }
            if (_meter != null) { Marshal.ReleaseComObject(_meter); _meter = null; }
        }

        public float Peak()
        {
            if (_meter == null) return 0;
            try { return _meter.GetPeakValue(out float p) == 0 ? p : 0; }
            catch { return 0; }
        }

        public void SetVolume(float v)
        {
            if (_vol == null) return;
            v = Math.Max(0f, Math.Min(1f, v));
            Guid ctx = SelfContext;
            _vol.SetMasterVolumeLevelScalar(v, ref ctx);
            if (Muted && v > 0) _vol.SetMute(false, ref ctx);
            if (_vol.GetMasterVolumeLevelScalar(out float nv) == 0) Volume = nv;
            if (_vol.GetMute(out bool m) == 0) Muted = m;
            VolumeChanged?.Invoke(false);
        }

        public void Nudge(double delta) => SetVolume((float)Math.Round(Volume + delta, 2));

        public void ToggleMute()
        {
            if (_vol == null) return;
            Guid ctx = SelfContext;
            _vol.SetMute(!Muted, ref ctx);
            if (_vol.GetMute(out bool m) == 0) Muted = m;
            VolumeChanged?.Invoke(false);
        }

        // ---------------- microphone ----------------

        void BindMic()
        {
            UnbindMic();
            if (_enum == null || _enum.GetDefaultAudioEndpoint(1 /*eCapture*/, 2 /*eCommunications*/, out IMMDevice dev) != 0 || dev == null) return;
            try
            {
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out object o) == 0) _mic = (IAudioEndpointVolume)o;
            }
            finally { Marshal.ReleaseComObject(dev); }
            if (_mic != null)
            {
                _mic.RegisterControlChangeNotify(_micCallback);
                if (_mic.GetMute(out bool m) == 0) MicMuted = m;
            }
        }

        void UnbindMic()
        {
            if (_mic == null) return;
            try { _mic.UnregisterControlChangeNotify(_micCallback); } catch { }
            Marshal.ReleaseComObject(_mic);
            _mic = null;
        }

        /// <summary>Mutes the default microphone for every app (works for any call app).</summary>
        public void ToggleMicMute()
        {
            if (_mic == null) return;
            Guid ctx = SelfContext;
            _mic.SetMute(!MicMuted, ref ctx);
            if (_mic.GetMute(out bool m) == 0) MicMuted = m;
            MicChanged?.Invoke(false);
        }

        // ---------------- notifications ----------------

        internal void OnNotify(bool isMic, Guid ctx, bool muted, float volume)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                if (isMic)
                {
                    bool changed = muted != MicMuted;
                    MicMuted = muted;
                    if (changed) MicChanged?.Invoke(ctx != SelfContext);
                    return;
                }
                Volume = volume;
                Muted = muted;
                VolumeChanged?.Invoke(ctx != SelfContext);
            }));
        }

        internal void OnDefaultDeviceChanged(int flow)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (flow == 0)
                    {
                        string before = DeviceName;
                        Bind();
                        VolumeChanged?.Invoke(false);
                        if (DeviceName.Length > 0 && DeviceName != before) OutputDeviceSwitched?.Invoke(DeviceName);
                    }
                    else
                    {
                        BindMic();
                        MicChanged?.Invoke(false);
                    }
                }
                catch (Exception ex) { App.Log(ex); }
            }));
        }

        static string FriendlyName(IMMDevice dev)
        {
            try
            {
                if (dev.OpenPropertyStore(0 /*STGM_READ*/, out IPropertyStore store) != 0 || store == null) return "";
                try
                {
                    var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
                    if (store.GetValue(ref key, out PropVariant pv) != 0) return "";
                    try { return pv.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(pv.pointer) ?? "" : ""; }
                    finally { PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return ""; }
        }

        /// <summary>"Headphones (AirPods Pro)" -> "AirPods Pro", "Speakers (Realtek Audio)" -> "Speakers".</summary>
        static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var m = Regex.Match(name, @"^(Headphones|Headset|Earphones|Headset Earphone|Hands-Free.*?)\s*\((.+)\)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[2].Value.Trim();
            m = Regex.Match(name, @"^(Speakers?)\s*\(", RegexOptions.IgnoreCase);
            if (m.Success) return "Speakers";
            return name;
        }

        public void Dispose()
        {
            try
            {
                Unbind();
                UnbindMic();
                if (_enum != null)
                {
                    _enum.UnregisterEndpointNotificationCallback(_deviceCallback);
                    Marshal.ReleaseComObject(_enum);
                }
            }
            catch { }
        }

        // ---------------- COM interop ----------------

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointer;
            [FieldOffset(8)] public long low;
            [FieldOffset(16)] public long high;
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
            [PreserveSig] int VolumeStepUp(ref Guid ctx);
            [PreserveSig] int VolumeStepDown(ref Guid ctx);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
            [PreserveSig] int GetVolumeRange(out float min, out float max, out float increment);
        }

        [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioEndpointVolumeCallback
        {
            [PreserveSig] int OnNotify(IntPtr notifyData);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
            [PreserveSig] int GetMeteringChannelCount(out uint count);
            [PreserveSig] int GetChannelsPeakValues(uint count, [Out, MarshalAs(UnmanagedType.LPArray)] float[] peaks);
            [PreserveSig] int QueryHardwareSupport(out uint mask);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PropertyKey { public Guid FormatId; public int PropertyId; }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMNotificationClient
        {
            [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
            [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
            [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class VolumeCallback : IAudioEndpointVolumeCallback
        {
            readonly AudioService _owner;
            readonly bool _isMic;
            internal VolumeCallback(AudioService owner, bool isMic) { _owner = owner; _isMic = isMic; }

            public int OnNotify(IntPtr data)
            {
                try
                {
                    // AUDIO_VOLUME_NOTIFICATION_DATA { GUID guidEventContext; BOOL bMuted; float fMasterVolume; ... }
                    var ctx = (Guid)Marshal.PtrToStructure(data, typeof(Guid));
                    bool muted = Marshal.ReadInt32(data, 16) != 0;
                    float vol = BitConverter.ToSingle(BitConverter.GetBytes(Marshal.ReadInt32(data, 20)), 0);
                    _owner.OnNotify(_isMic, ctx, muted, vol);
                }
                catch { }
                return 0;
            }
        }

        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        public sealed class DeviceCallback : IMMNotificationClient
        {
            readonly AudioService _owner;
            internal DeviceCallback(AudioService owner) { _owner = owner; }

            public int OnDeviceStateChanged(string deviceId, int newState) => 0;
            public int OnDeviceAdded(string deviceId) => 0;
            public int OnDeviceRemoved(string deviceId) => 0;
            public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;

            public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
            {
                if ((flow == 0 && role == 1) || (flow == 1 && role == 2)) _owner.OnDefaultDeviceChanged(flow);
                return 0;
            }
        }
    }
}
