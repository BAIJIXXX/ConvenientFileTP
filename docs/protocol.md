# ConvenientFileTP 传输协议 v1

控制通道为一条 TCP 连接（发送端 → 接收端，端口来自设备发现报文的 `port` 字段）。
控制消息为 **UTF-8 JSON + LF**，一行一条；文件内容紧跟在 `file` 消息之后，按 `size` 字节原样传输。
所有消息都包含 `t`；除 `accept` 外都带 `session`（GUID 字符串）。

## 握手

1. 发送端发送：

```json
{"t":"offer","app":"ConvenientFileTP","v":1,"session":"<guid>","fromId":"<设备id>","fromName":"<设备名>","files":[{"name":"a.zip","size":123456}]}
```

2. 接收端回复：

```json
{"t":"accept","session":"<guid>","accepted":true}
```

拒绝：`{"t":"accept","session":"<guid>","accepted":false,"reason":"..."}`，随后关闭连接。

## 传文件（按 files 顺序逐个）

发送端：

```json
{"t":"file","session":"<guid>","index":0,"name":"a.zip","size":123456}
```

紧接 `size` 字节原始数据。接收端保存完成后回复：

```json
{"t":"file-result","session":"<guid>","index":0,"ok":true,"saved":"C:\\...\\a.zip"}
```

失败：`ok:false,"error":"..."`。发送端收到 `ok:false` 后终止会话。

## 结束

```json
{"t":"done","session":"<guid>","ok":true}
```

接收端回 `{"t":"done","session":"<guid>","ok":true}` 后双方关闭连接。

任意时刻可发送 `{"t":"error","session":"<guid>","message":"..."}` 并关闭连接。

## 约束

- 分块大小 128 KiB，读写缓冲 64 KiB；`size` 必须精确匹配，多/少字节均判失败。
- 握手行读取超时 20s；传输空闲超时 45s；断线/取消即失败。
- 接收端并发连接上限 4，超出拒绝；文件保存到下载目录，使用 `Protocol.SafeFileName` 与 `Protocol.MakeUniquePath`。
- 进度事件节流约 100ms；`TransferProgress.Percent` 以所有文件总字节数为分母。
- 发送端校验每个 `saved` 路径只用于展示，不得写回本地。
- 局域网、热点（192.168.137.x / 192.168.43.x / 172.20.10.x）均为普通 TCP，无需特殊处理。
