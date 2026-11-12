package io.localpen.tablet;

import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.Locale;
import javax.crypto.Cipher;
import javax.crypto.Mac;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.PBEKeySpec;
import javax.crypto.spec.SecretKeySpec;
import javax.net.ssl.HttpsURLConnection;

final class OnlinePairing {
    static String endpoint(String value) {
        URI uri = URI.create(value.trim());
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getHost() == null
                || uri.getUserInfo() != null || uri.getRawQuery() != null || uri.getRawFragment() != null) {
            throw new IllegalArgumentException("请输入 HTTPS 配对服务地址");
        }
        return uri.toASCIIString().replaceAll("/+$", "");
    }

    static BridgeProfile resolve(String endpoint, String code) throws Exception {
        String normalized = code.replace("-", "").replace(" ", "").toUpperCase(Locale.ROOT);
        if (!normalized.matches("[0-9ABCDEFGHJKMNPQRSTVWXYZ]{10}"))
            throw new IllegalArgumentException("请输入电脑显示的十位在线码");
        PBEKeySpec spec = new PBEKeySpec(normalized.toCharArray(),
                "PenNoteBridge online v1".getBytes(StandardCharsets.US_ASCII), 180000, 640);
        byte[] keys;
        try { keys = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).getEncoded(); }
        finally { spec.clearPassword(); }
        StringBuilder lookup = new StringBuilder(32);
        for (int i = 0; i < 16; i++) lookup.append(String.format(Locale.ROOT, "%02x", keys[i] & 255));
        URI url = URI.create(endpoint(endpoint) + "/v1/pair/" + lookup);
        HttpsURLConnection connection = (HttpsURLConnection) url.toURL().openConnection();
        byte[] record;
        try {
            connection.setInstanceFollowRedirects(false);
            connection.setConnectTimeout(6000);
            connection.setReadTimeout(6000);
            if (connection.getResponseCode() != 200) throw new IOException("在线码已失效或尚未生成");
            try (InputStream input = connection.getInputStream()) {
                ByteArrayOutputStream bytes = new ByteArrayOutputStream();
                byte[] chunk = new byte[256];
                for (int count; (count = input.read(chunk)) != -1;) {
                    if (bytes.size() + count > 512) throw new IOException("在线配对回复过长");
                    bytes.write(chunk, 0, count);
                }
                record = Base64.decode(bytes.toString("US-ASCII").trim(), Base64.URL_SAFE | Base64.NO_WRAP);
            }
        } finally { connection.disconnect(); }
        int macStart = record.length - 32;
        if (record.length < 64 || macStart % 16 != 0) throw new IOException("在线配对记录格式无效");
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(keys, 48, 32, "HmacSHA256"));
        mac.update(record, 0, macStart);
        if (!MessageDigest.isEqual(mac.doFinal(), Arrays.copyOfRange(record, macStart, record.length)))
            throw new IOException("在线配对记录校验失败");
        Cipher cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
        cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(keys, 16, 32, "AES"), new IvParameterSpec(record, 0, 16));
        BridgeProfile profile = BridgeProfile.parse(new String(cipher.doFinal(record, 16, macStart - 16),
                StandardCharsets.US_ASCII));
        Arrays.fill(keys, (byte) 0);
        if (profile == null) throw new IOException("在线配对资料无效");
        return profile;
    }
}
