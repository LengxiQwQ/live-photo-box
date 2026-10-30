# P9-R8 HEIF 历史 Original 文件资源契约

本有限单元接通当前已授权的全部来源、附加图像保留及兼容图片转换范围，不增加丢失数据、生产依赖或相册编辑功能承诺。当前缺口：改过封面的照片带有一个完整历史 Original JPEG 文件；它在 Neutral 中已有独立身份、完整文件范围和 `Original` 语义，HEIC Writer 尚不能保存该 materialized 文件。此缺口仍是必需正向覆盖，不能以 Unsupported 关闭。

## 保留对象和外层表示

Original 是完整历史图像文件，不是当前封面、当前 GainMap 或实况视频权限。它可能本身是包含多个 JPEG、MPF、拍摄和编辑信息的复合文件。保留其全部字节、内部历史关系和已有不一致，不截至首个 EOI，不重建内部 MPF，不把历史内容提升为当前主图的权限。

普通 coded `jpeg` image item 不能未经论证就代指这个完整复合文件。因此采用合法 HEIF `uri ` 文件资源 item：

| 字段 | 固定规则 |
|---|---|
| item type | `uri `，真实 `infe` 中读取 |
| item URI type | `urn:livephotobox:asset:original-jpeg:1`，精确匹配；同命名空间未知版本拒绝 |
| item name | `LivePhotoBox Original JPEG File`，只作辅助核对，不单凭名称识别 |
| protection / storage | 未保护；内部 `mdat`，单个合法完整连续 extent，最多32MiB |
| owner | 唯一 `cdsc` 指向实际 primary；不允许共享、重复、反向引用或其他关系 |
| payload | 一个完整 Original JPEG 文件，通过现有有限 Original 观察器，全部字节原样保留 |

这不是 coded auxiliary image，没有 `auxC/auxl`、当前封面或显示替代权限。URI 是固定数据类型标识，不是可访问路径或可执行地址。外层 item selector/range 描述存储位置；现有 Neutral 的 JPEG container/codec 描述 payload 文件内容，不能据此把外层 `uri ` 说成 `jpeg` coded item。

依据：[HEIF 技术资料的 item、metadata 与 cdsc 关系说明](https://nokiatech.github.io/heif/technical.html)及[当前使用的 libheif v1.23.4 的 typed URI/MIME item API](https://github.com/strukturag/libheif/blob/v1.23.4/libheif/api/libheif/heif_items.h)。使用上述合法承载结构不等于其他手机相册认识本应用的 Original 资源；具体语义映射是本应用有限格式扩展，不能当作厂商原生编辑功能支持。

## Reader、Writer、Validator 的闭合边界

1. Writer 在已核对的输入 artifact identity/hash/长度、完整 Original 结构与不重复证明之后添加资源；来源保留租约与既有 plan/transaction gate 不得绕过。
2. 新 item 使用空闲唯一ID，完整更新 `iinf/iloc/iref`；旧 coded image、GainMap、Exif/XML/ICC、普通数据的 item ID、关系和内容保持。不能把新内容追加到隐式 box 内，不能丢掉末尾普通 SEF。
3. Reader 只按合法 `infe/iloc/cdsc` 图解析资源，精确类型、版本、owner、内部范围与完整 payload 全部成立后，映射到已有的 `Original` 文件资产语义。未知URI版本、受保护、外部引用、缺 owner、额外关系、循环、范围别名、重复位置或截断一律拒绝。
4. 当前 coded image/GainMap 图仍由原观察器单独核对；Original 文件资源使用独立资源观察器与计数，不能塞进 libheif 的 coded auxiliary inventory，不能漏出总 Neutral/Target 资产覆盖。
5. 提取必须恢复同一完整 JPEG 文件；最终 Target Validator 独立核对 Original 资源的真实 item/owner/range/hash、全部原字节及来源 descriptor 身份。历史资源内部的元数据和图像关系不成为外层 primary 的 binding。
6. 旧接口、既有 role/codec/selector 的含义保持。若实现检查发现某现有公开字段明确只容许 coded image item，则先记录该实际冲突并取得必要的兼容接口决定，不能只因二进制布局可容纳而冒用。

## 验证与未完成状态

### 兼容运行库能力声明

沿用用户已批准的附加拍摄/编辑数据兼容接口范围，在既有 runtime capability mask 增加低层 `HEIF_ORIGINAL_FILE_RESOURCE` bit23。它只声明完整 Original 文件资源的严格读取、完整资产计数和受权限约束的写入，不声明某目标协议或设备相册已经验收。旧位、接口和 POD 布局不变。新的 C# HEIF source inspection 在消费成功 facts 前必须确认该位；旧运行库缺少该位时明确失败，不能因为旧 Reader 忽略 URI 文件而报告已完整保留。直接 Native HDR HEIC→JPEG 入口遇到该文件资源也必须拒绝，直到完整回写路径实现。无 fallback、无新生产依赖。

须包含实际完整 Original 的独立结构与全部字节对照，提取后完整 primary/内部 GainMap 解码；错误URI/版本、伪装coded jpeg、错 primary、重复/共享/反向引用、外部/重叠extent、protected item、payload截断或内部 GainMap丢失反例。四份当前 HDR 来源和当前目标全部来源/变体须按实际正式 CLI 重跑受影响部分，并进行 fresh Review、另一 Verify、GUI、设备两轮。

本契约是待实现的有限表示，不是当前已支持声明。S08 的 source Original 结构详细证据、既有失败及执行记录保留在 R8 工作根 `documentation/s2-original-jpeg-structure-v1.json` 与同任务 CLI 证据；不改写协议事实文档来将私有资源称为厂商原生协议。跨品牌相册的“恢复原图/封面编辑历史”仍待设备反馈，不由本应用自行签署。
