using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PenNoteBridge
{
    internal static class OnlineRendezvous
    {
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        private const int Iterations = 180000;
        private static readonly byte[] Salt = Encoding.ASCII.GetBytes("PenNoteBridge online v1");
        private static readonly Regex Fingerprint = new Regex(@"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Token = new Regex(@"\A[0-9a-fA-F]{24}\z", RegexOptions.CultureInvariant);

        internal static string ValidateEndpoint(string endpoint)
        {
            Uri uri;
            string value = (endpoint ?? "").Trim();
            if (value.Length == 0 || value.Length > 256 || value.IndexOf('\\') >= 0 ||
                !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("请输入 HTTPS 中转地址，可包含端口和部署路径。");
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        internal static string Create(string endpoint, string payload)
        {
            string relay = ValidateEndpoint(endpoint);
            byte[] plaintext = EncodePayload(payload);
            using (HttpClientHandler handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                SslProtocols = SslProtocols.Tls12,
                CheckCertificateRevocationList = true
            })
            using (HttpClient client = new HttpClient(handler))
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                client.Timeout = TimeSpan.FromSeconds(8);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string code = CreateCode(random);
                    byte[] iv = new byte[16];
                    random.GetBytes(iv);
                    string lookup;
                    string blob = Encrypt(code, plaintext, iv, out lookup);
                    using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, relay + "/v1/pair/" + lookup))
                    {
                        request.Headers.ExpectContinue = false;
                        request.Content = new StringContent(blob, Encoding.ASCII, "text/plain");
                        HttpResponseMessage response;
                        try { response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult(); }
                        catch (TaskCanceledException error) { throw new InvalidOperationException("等待中转服务超时。", error); }
                        catch (HttpRequestException error) { throw new InvalidOperationException("无法通过 HTTPS 连接中转服务。请检查地址和证书。", error); }
                        using (response)
                        {
                            if (response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.NoContent)
                                return code;
                            if (response.StatusCode == HttpStatusCode.Conflict) continue;
                            int status = (int)response.StatusCode;
                            if (status == 429 || status == 503) throw new InvalidOperationException("中转服务繁忙，请稍后重试。");
                            throw new InvalidOperationException("中转服务拒绝发布在线码（HTTP " + status.ToString(CultureInfo.InvariantCulture) + "）。");
                        }
                    }
                }
            }
            throw new InvalidOperationException("在线码发生冲突，请重新生成。");
        }

        private static byte[] EncodePayload(string payload)
        {
            string[] fields = (payload ?? "").Split('|');
            bool legacy = fields.Length == 4 && fields[0] == "PNB1";
            ushort port;
            bool portAware = fields.Length == 5 && fields[0] == "PNB2"
                && UInt16.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out port)
                && port != 0 && fields[2] == port.ToString(CultureInfo.InvariantCulture);
            int fingerprintIndex = portAware ? 3 : 2;
            IPAddress ip;
            if ((!legacy && !portAware) || !IPAddress.TryParse(fields[1], out ip) ||
                ip.AddressFamily != AddressFamily.InterNetwork || fields[1] != ip.ToString() ||
                !Fingerprint.IsMatch(fields[fingerprintIndex]) || !Token.IsMatch(fields[fingerprintIndex + 1]))
                throw new ArgumentException("配对信息无效。请选择可从平板访问的 IPv4 地址。");
            return Encoding.ASCII.GetBytes(payload);
        }

        private static string CreateCode(RandomNumberGenerator random)
        {
            byte[] bytes = new byte[10];
            char[] code = new char[bytes.Length];
            random.GetBytes(bytes);
            for (int i = 0; i < code.Length; i++) code[i] = Alphabet[bytes[i] & 31];
            return new string(code);
        }

        private static string Encrypt(string code, byte[] plaintext, byte[] iv, out string lookup)
        {
            using (Rfc2898DeriveBytes derivation = new Rfc2898DeriveBytes(code, Salt, Iterations, HashAlgorithmName.SHA256))
            using (Aes aes = Aes.Create())
            {
                lookup = BitConverter.ToString(derivation.GetBytes(16)).Replace("-", "").ToLowerInvariant();
                aes.KeySize = 256;
                aes.Key = derivation.GetBytes(32);
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                using (HMACSHA256 authenticate = new HMACSHA256(derivation.GetBytes(32)))
                {
                    byte[] ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
                    int authenticatedLength = iv.Length + ciphertext.Length;
                    byte[] packet = new byte[authenticatedLength + 32];
                    Buffer.BlockCopy(iv, 0, packet, 0, iv.Length);
                    Buffer.BlockCopy(ciphertext, 0, packet, iv.Length, ciphertext.Length);
                    byte[] tag = authenticate.ComputeHash(packet, 0, authenticatedLength);
                    Buffer.BlockCopy(tag, 0, packet, authenticatedLength, tag.Length);
                    return Convert.ToBase64String(packet).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                }
            }
        }
    }
}
