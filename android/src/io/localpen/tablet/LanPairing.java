package io.localpen.tablet;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.math.BigInteger;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketException;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.util.Arrays;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;

final class LanPairing {
    static final int PORT = 57213;
    private static final String N_HEX =
            "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050" +
            "A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50" +
            "E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B8" +
            "55F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773B" +
            "CA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748" +
            "544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6" +
            "AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB6" +
            "94B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73";
    private static final BigInteger N = new BigInteger(N_HEX, 16);
    private static final BigInteger G = BigInteger.valueOf(2);
    private static final byte[] LEGACY_MAGIC = ascii("PNB1");
    private static final byte[] PORT_MAGIC = ascii("PNB2");
    private static final byte[] ID = ascii("PenNoteBridge");
    private static final SecureRandom RANDOM = new SecureRandom();

    static BridgeProfile pair(String host, int port, String code) throws IOException, GeneralSecurityException {
        return pair(host, port, code, false);
    }

    static BridgeProfile pair(String host, int port, String code, boolean requirePort)
            throws IOException, GeneralSecurityException {
        if (!code.matches("[0-9]{6}")) throw new IllegalArgumentException("请输入电脑显示的六位配对码");
        try {
            return exchange(host, port, code, PORT_MAGIC);
        } catch (NoChallenge error) {
            if (requirePort) throw error;
            return exchange(host, port, code, LEGACY_MAGIC);
        }
    }

    private static BridgeProfile exchange(String host, int port, String code, byte[] magic)
            throws IOException, GeneralSecurityException {
        BigInteger a;
        do { a = new BigInteger(256, RANDOM); } while (a.signum() == 0);
        byte[] clientPublic = pad(G.modPow(a, N));
        try (Socket socket = new Socket()) {
            socket.connect(new InetSocketAddress(host, port), 4500);
            socket.setSoTimeout(5000);
            socket.setTcpNoDelay(true);
            OutputStream output = socket.getOutputStream();
            byte[] hello = new byte[magic.length + clientPublic.length];
            System.arraycopy(magic, 0, hello, 0, magic.length);
            System.arraycopy(clientPublic, 0, hello, magic.length, clientPublic.length);
            output.write(hello);
            output.flush();
            DataInputStream input = new DataInputStream(socket.getInputStream());
            byte[] salt = readSalt(input);
            byte[] serverPublic = read(input, 256);
            BigInteger bPublic = unsigned(serverPublic);
            if (bPublic.signum() == 0 || bPublic.compareTo(N) >= 0) throw new IOException("配对回复无效");
            BigInteger k = unsigned(hash(pad(N), pad(G)));
            BigInteger u = unsigned(hash(clientPublic, serverPublic));
            if (u.signum() == 0) throw new IOException("配对回复无效");
            BigInteger x = unsigned(hash(salt, hash(ID, ascii(":"), ascii(code))));
            BigInteger base = bPublic.subtract(k.multiply(G.modPow(x, N))).mod(N);
            if (base.signum() == 0) throw new IOException("配对回复无效");
            byte[] key = hash(minimal(base.modPow(a.add(u.multiply(x)), N)));
            byte[] group = hash(pad(N));
            byte[] generator = hash(pad(G));
            for (int i = 0; i < group.length; i++) group[i] ^= generator[i];
            byte[] clientMinimal = minimal(unsigned(clientPublic));
            byte[] proof = hash(group, hash(ID), salt, clientMinimal, minimal(bPublic), key);
            output.write(proof);
            output.flush();
            byte[] serverProof = read(input, 32);
            if (!MessageDigest.isEqual(serverProof, hash(clientMinimal, proof, key))) {
                throw new IOException("电脑未能验证配对码");
            }
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            byte[] nonce = read(input, 12);
            cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(hash(ascii("PNB1 credential encryption"), key), "AES"),
                    new GCMParameterSpec(128, nonce));
            cipher.updateAAD(magic);
            boolean hasPort = magic == PORT_MAGIC;
            byte[] record = cipher.doFinal(read(input, hasPort ? 106 : 104));
            String credentials = new String(record, 0, 88, StandardCharsets.US_ASCII);
            if (!credentials.matches("[0-9A-Fa-f]{88}")) throw new IOException("配对资料无效");
            int lanPort = hasPort ? ((record[88] & 255) << 8) | (record[89] & 255) : BridgeProfile.LAN_PORT;
            if (lanPort == 0) throw new IOException("电脑返回的局域网端口无效");
            return new BridgeProfile(host, credentials.substring(0, 64), credentials.substring(64), lanPort);
        }
    }

    private static final class NoChallenge extends IOException {
        NoChallenge(Throwable cause) { super("电脑未返回配对资料", cause); }
    }

    private static byte[] readSalt(DataInputStream input) throws IOException {
        // Legacy receivers close the connection on an unknown magic without sending a challenge.
        int first;
        try {
            first = input.read();
        } catch (SocketException error) {
            throw new NoChallenge(error);
        }
        if (first == -1) throw new NoChallenge(null);
        byte[] salt = new byte[16];
        salt[0] = (byte) first;
        input.readFully(salt, 1, salt.length - 1);
        return salt;
    }

    private static byte[] ascii(String value) { return value.getBytes(StandardCharsets.US_ASCII); }

    private static byte[] hash(byte[]... fields) throws GeneralSecurityException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        for (byte[] field : fields) digest.update(field);
        return digest.digest();
    }

    private static BigInteger unsigned(byte[] value) { return new BigInteger(1, value); }

    private static byte[] minimal(BigInteger value) {
        byte[] bytes = value.toByteArray();
        return bytes.length > 1 && bytes[0] == 0 ? Arrays.copyOfRange(bytes, 1, bytes.length) : bytes;
    }

    private static byte[] pad(BigInteger value) {
        byte[] raw = minimal(value);
        byte[] padded = new byte[256];
        System.arraycopy(raw, 0, padded, padded.length - raw.length, raw.length);
        return padded;
    }

    private static byte[] read(DataInputStream input, int count) throws IOException {
        byte[] bytes = new byte[count];
        input.readFully(bytes);
        return bytes;
    }
}
