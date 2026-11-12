using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace PenNoteBridge
{
    internal sealed class Receiver : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr CreateCall(byte[] folder, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr StringCall(IntPtr handle, byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr SimpleCall(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr CaptureCall(IntPtr handle, [In] IntPtr[] windows, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate uint GestureCall(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte NavigationCall(IntPtr handle, out CanvasNavigation motion);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate byte EraserIntentCall(IntPtr handle, IntPtr window, int x, int y, uint messageTime);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void VoidCall(IntPtr value);

        readonly StringCall start;
        readonly StringCall configure;
        readonly StringCall disconnect;
        readonly StringCall pairing;
        readonly SimpleCall stop;
        readonly SimpleCall snapshot;
        readonly SimpleCall reset;
        readonly SimpleCall renew;
        readonly CaptureCall annotationCapture;
        readonly GestureCall takeBarrelGesture;
        readonly NavigationCall takeNavigation;
        readonly EraserIntentCall takeEraserIntent;
        readonly VoidCall free;
        readonly VoidCall destroy;
        IntPtr handle;

        public Receiver(string profile)
        {
            var library = new NativeLibrary(profile);
            free = library.Export<VoidCall>("pnb_receiver_free_string");
            destroy = library.Export<VoidCall>("pnb_receiver_destroy");
            start = library.Export<StringCall>("pnb_receiver_start");
            configure = library.Export<StringCall>("pnb_receiver_configure");
            disconnect = library.Export<StringCall>("pnb_receiver_disconnect");
            pairing = library.Export<StringCall>("pnb_receiver_pairing");
            stop = library.Export<SimpleCall>("pnb_receiver_stop");
            snapshot = library.Export<SimpleCall>("pnb_receiver_snapshot");
            reset = library.Export<SimpleCall>("pnb_receiver_reset_diagnostics");
            renew = library.Export<SimpleCall>("pnb_receiver_renew_pairing");
            annotationCapture = library.Export<CaptureCall>("pnb_receiver_annotation_capture");
            takeBarrelGesture = library.Export<GestureCall>("pnb_receiver_take_barrel_gestures");
            takeNavigation = library.Export<NavigationCall>("pnb_receiver_take_canvas_navigation");
            takeEraserIntent = library.Export<EraserIntentCall>("pnb_receiver_take_eraser_intent");
            Check(library.Export<CreateCall>("pnb_receiver_create")(NativeLibrary.Encode(profile), out handle));
            if (handle == IntPtr.Zero) throw new InvalidOperationException("接收器未能初始化");
        }

        static JavaScriptSerializer Serializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
        }

        public void Start(IDictionary<string, object> config) { Check(start(handle, NativeLibrary.Encode(Serializer().Serialize(config)))); }
        public void Configure(IDictionary<string, object> config) { Check(configure(handle, NativeLibrary.Encode(Serializer().Serialize(config)))); }
        public void Stop() { Check(stop(handle)); }
        public void Disconnect(string connectionId) { Check(disconnect(handle, NativeLibrary.Encode(connectionId))); }
        public void ResetTest() { Check(reset(handle)); }
        public void RenewPairing() { Check(renew(handle)); }
        public void SetAnnotationCapture(IntPtr[] windows) { Check(annotationCapture(handle, windows, (uint)windows.Length)); }
        public uint TakeBarrelGesture() { return takeBarrelGesture(handle); }
        public bool TakeNavigation(out CanvasNavigation motion) { return takeNavigation(handle, out motion) != 0; }
        public bool TakeEraserIntent(IntPtr window, int x, int y, uint messageTime)
        {
            return takeEraserIntent(handle, window, x, y, messageTime) != 0;
        }

        public IDictionary<string, object> Snapshot() { return Parse(snapshot(handle)); }
        public IDictionary<string, object> Pairing(string address) { return Parse(pairing(handle, NativeLibrary.Encode(address))); }

        IDictionary<string, object> Parse(IntPtr result)
        {
            string value = Read(result);
            var parsed = Serializer().DeserializeObject(value) as IDictionary<string, object>;
            if (parsed == null) throw new InvalidOperationException("接收器返回了无法读取的结果");
            object error;
            if (parsed.TryGetValue("error", out error)) throw new InvalidOperationException(Convert.ToString(error));
            return parsed;
        }

        void Check(IntPtr error)
        {
            if (error != IntPtr.Zero) throw new InvalidOperationException(Read(error));
        }

        string Read(IntPtr result)
        {
            try { return NativeLibrary.Decode(result); }
            finally { if (result != IntPtr.Zero) free(result); }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            destroy(handle);
            handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CanvasNavigation
    {
        internal float PanX, PanY, Scale, FocusX, FocusY;
        internal uint Action;
    }
}
