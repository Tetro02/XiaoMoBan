using System;
using System.Reflection;
using System.Text;
using PenNoteBridge;

internal static class OnlineRendezvousSmoke
{
    private const string Lookup = "0f52cc0c123fdffc3e5da5178e44aa5e";
    private const string Blob = "AAECAwQFBgcICQoLDA0ODz62qcaFhkHfarQHdFV7jXUbgzJb6C8ZyyRFzbHaU8h51dmcvXlKe4IO21QlwyBt__w"
        + "ZWOj9cdjtO8yDXPuDNaMPhKAN3mvpn5ibxL5TUPQqRkW8-lfzQhei11YeTZHZxxRUCLeTUhxeNYgQdxnTfjG90"
        + "Om0H_aUusplERjOX_EckiPkmM_x_M6iZ3S8JlF-tA";

    private static int Main()
    {
        try
        {
            if (OnlineRendezvous.ValidateEndpoint(" https://EXAMPLE.ORG/relay/ ") != "https://example.org/relay")
                throw new InvalidOperationException("Unexpected endpoint normalization");
            foreach (string invalid in new[] { "http://example.org", "https://u@x.example.org", "https://example.org?a=1",
                "https://example.org#part", "https://example.org\\bad", "", "example.org" })
            {
                try { OnlineRendezvous.ValidateEndpoint(invalid); }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException("Invalid endpoint was accepted: " + invalid);
            }

            byte[] iv = new byte[16];
            for (int index = 0; index < iv.Length; index++) iv[index] = (byte)index;
            StringBuilder payload = new StringBuilder("PNB1|192.0.2.17|");
            for (int index = 0; index < 32; index++) payload.Append("01");
            payload.Append('|');
            for (int index = 0; index < 12; index++) payload.Append("ab");
            MethodInfo encrypt = typeof(OnlineRendezvous).GetMethod("Encrypt", BindingFlags.NonPublic | BindingFlags.Static);
            object[] arguments = { "001122AABB", Encoding.ASCII.GetBytes(payload.ToString()), iv, null };
            string blob = (string)encrypt.Invoke(null, arguments);
            if ((string)arguments[3] != Lookup || blob != Blob)
                throw new InvalidOperationException("Cryptographic vector did not match Node.js reference output");
            Console.WriteLine("OK endpoint validation and PBKDF2/AES/HMAC packet vector");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            if (error.InnerException != null) Console.Error.WriteLine(error.InnerException.Message);
            return 1;
        }
    }
}
