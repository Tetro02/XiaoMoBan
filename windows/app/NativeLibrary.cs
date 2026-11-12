using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PenNoteBridge
{
    internal sealed class NativeLibrary
    {
        const uint LoadLibrarySearchDllLoadDir = 0x100;
        const uint LoadLibrarySearchSystem32 = 0x800;
        const string ReceiverResource = "PenNoteBridge.NativeReceiver";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        readonly IntPtr module;

        public NativeLibrary(string profile)
        {
            string receiver = Materialize(profile);
            module = LoadLibraryEx(receiver, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
            if (module == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载内置接收器");
        }

        public T Export<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "接收器缺少接口 " + name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        static string Materialize(string profile)
        {
            byte[] binary;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ReceiverResource))
            {
                if (resource == null) throw new InvalidOperationException("安装包中缺少内置接收器");
                using (var output = new MemoryStream())
                {
                    resource.CopyTo(output);
                    binary = output.ToArray();
                }
            }
            using (SHA256 hash = SHA256.Create())
            {
                string digest = Hex(hash.ComputeHash(binary));
                string folder = Path.Combine(profile, "native", digest);
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder, "pennote_receiver.dll");
                if (Matches(hash, target, digest)) return target;
                string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.WriteAllBytes(temporary, binary);
                    if (File.Exists(target))
                        File.Replace(temporary, target, null);
                    else
                        File.Move(temporary, target);
                }
                catch (IOException)
                {
                    if (!Matches(hash, target, digest)) throw;
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                if (!Matches(hash, target, digest))
                    throw new InvalidDataException("内置接收器校验失败");
                return target;
            }
        }

        static bool Matches(SHA256 hash, string path, string expected)
        {
            if (!File.Exists(path)) return false;
            using (FileStream file = File.OpenRead(path))
                return String.Equals(Hex(hash.ComputeHash(file)), expected, StringComparison.Ordinal);
        }

        static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static byte[] Encode(string value)
        {
            return Encoding.UTF8.GetBytes(value + "\0");
        }

        public static string Decode(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("接收器未返回结果");
            const int limit = 2 * 1024 * 1024;
            int length = 0;
            while (length < limit && Marshal.ReadByte(pointer, length) != 0) length++;
            if (length == limit) throw new InvalidDataException("接收器返回的数据过长");
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
