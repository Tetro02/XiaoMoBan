# 小墨板在线连接码中转

Windows 和 Android 填写同一个 HTTPS 中转地址。Windows 用 10 位在线码加密所选 IPv4、接收器证书指纹和配对令牌，并将加密数据发布到中转。Android 输入在线码后领取并解密，再直连 Windows 接收器。中转不转发笔迹，也不提供 NAT 穿透。平板仍需能通过局域网或已有的路由访问 Windows 接收器。

在线码发布后有效 5 分钟，只能领取一次。重新生成会产生新码。请像分享临时配对凭据一样分享在线码。

## 部署

需要 Python 3.8 或更新版本，运行服务只使用 Python 标准库。两台设备都要信任中转地址的 HTTPS 证书。

已有域名证书时，可以让 Python 直接提供 HTTPS。以下命令假设 `fullchain.pem` 和 `privkey.pem` 已由管理员配置。

```sh
python relay.py --bind 0.0.0.0 --port 8765 --cert fullchain.pem --key privkey.pem
```

两台设备填 `https://relay.example.com:8765`，使用实际域名和端口。

已有 HTTPS 反向代理时，Python 仅绑定本机。

```sh
python relay.py --bind 127.0.0.1 --port 8765 --behind-proxy
```

在现有 Nginx HTTPS 虚拟主机内添加以下配置。此例的中转地址为 `https://relay.example.com/tablet`。

```nginx
location /tablet/ {
    access_log off;
    client_max_body_size 512;
    proxy_cache off;
    proxy_next_upstream off;
    proxy_set_header X-Real-IP $remote_addr;
    proxy_set_header X-Forwarded-For "";
    proxy_pass http://127.0.0.1:8765/;
}
```

中转只在内存保留待领取记录。服务重启后，旧码失效。请使用单个服务进程，反向代理应关闭该路径的缓存和请求日志。代理会覆盖 `X-Real-IP`，以便中转按客户端地址限流。

## 接口与检查

- `PUT /v1/pair/{lookup}` 接收最多 512 字节的无填充 Base64url，成功返回 `201`。重复键在原有有效期内返回 `409`。
- `GET /v1/pair/{lookup}` 首次领取返回 `200` 和加密包。未找到、过期和已领取均返回 `404`。
- `lookup` 是 PBKDF2-HMAC-SHA256 结果的前 16 字节，用小写十六进制表示。盐为 ASCII `PenNoteBridge online v1`，迭代 180000 次，共派生 80 字节。后续两个 32 字节段分别用于 AES-256-CBC 和 HMAC-SHA256。加密包由 16 字节 IV、带 PKCS#7 填充的密文和 32 字节 HMAC 组成，HMAC 覆盖 IV 与密文。
- `python -B test_relay.py -v` 可验证过期、并发领取和请求限制。有 OpenSSL 命令时，测试还会生成临时证书并验证本地 HTTPS。

Windows 的确定性加密测试需同 `../windows/app/OnlineRendezvous.cs` 一起编译，并引用 `System.Net.Http.dll`。测试向量中的在线码 `001122AABB` 产生查找键 `0f52cc0c123fdffc3e5da5178e44aa5e`，固定 IV 和完整密文在 `OnlineRendezvousSmoke.cs` 中。
