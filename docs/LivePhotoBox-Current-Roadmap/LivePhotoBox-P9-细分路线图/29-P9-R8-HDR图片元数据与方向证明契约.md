# P9-R8 HDR 图片元数据与方向证明契约

本单元落实[27 的当前用户决定](27-P9-R8-图片换格式与实际方向决策.md)与[28 的兼容图片证明方案](28-P9-R8-普通图片转换与方向证明契约.md)：转换应实际转正，保留能够正确承接的拍摄、颜色、HDR 和附加图片，并明确实际重新压缩。没有增加丢失数据、降 SDR、旧运行库回退、生产依赖或正式支持声明的许可。

初始有限范围是 Native 真实绑定的 JPEG ISO 主图和唯一 GainMap 转 HEIC。其他辅助内容、无法分类的图片尾部或不明/冲突的方向继续拒绝；拒绝不代表相应来源已完成。HEIC 反向、同格式自动转正和其他协议仍须单独验收。

## 兼容接口与作用范围

新增 `lpb_get_hdr_image_conversion_proof_v1(context, transaction_token, lpb_image_conversion_proof_v1*)`，复用28中精确240B POD布局。它只对调用context中仍有效、未提交、未释放的自有HDR stage签发 **kind2/flags1**。未知、跨context、过期、已提交、已释放、实际对象/hash变化、缺少完整对应关系或布局不适用均失败。输出容量的有限清零与size/version规则沿用28；仅精确240B/version1可成功。

此kind2属于新getter。旧 `lpb_hdr_gainmap_metadata_proof_v2` 的kind2/3、旧80B布局、普通 `lpb_convert_image_v2` 的kind1及其最多4项的含义全部独立，不互相冒充。旧入口保留；旧运行库缺少新证明时不能用旧证明代签完整HDR图片metadata保留。

HDR ISO数值、GainMap语义/范围/像素hash与实际aux图关系继续由已绑定HDR转换结果及实际重观测核对。新证明负责完整适用的图片metadata映射、物理方向及来源/实际stage对象绑定，不能单凭它宣称HDR像素未重编码或显示效果已经通过。

## 八项 metadata ledger

expected-mapped-source和actual-output摘要按固定顺序编码，沿用 `kind:u8 + length:u64LE + bytes`，即使不存在也编码零长度：

| kind | 内容与实际owner |
|---|---|
| 1 | 主图完整mapped Exif/TIFF |
| 2 | 主图完整mapped标准XML |
| 3 | 主图完整declared ICC |
| 4 | GainMap完整mapped Exif/TIFF |
| 5 | GainMap完整mapped标准XML |
| 6 | GainMap完整declared ICC |
| 7 | 一个完整IMv1原JPEG上下文记录 |
| 8 | 合法、已明确归属的普通SEF原始完整块 |

`metadata_count`为上述非空项数，最多8。metadata status：非空项存在且全部核对为1，全部证明不存在才可为2。ICC status聚合主/辅两个owner：任一有ICC则两者分别与来源正确对应后为1；两个都证明不存在才为2。不能只核对主图ICC就签聚合成功。

首次签发kind2之前的布局清点确认，三星JPEG主图与GainMap之间还承载普通SEF，不能当作无意义padding删除。因此在未产生任何kind2成功证据时，将初稿7项扩为8项并新增该必需对应关系。SEF须经合法目录、普通内容和独立owner核对，映射为HEIF末尾合法sefd内的原始完整块并逐字节重读；任意不明gap不适用。此修订增加保留检查，不改变旧证明含义，也不放宽已有验收。

来源/产物whole SHA256和长度、actual创建对象identity、source stored primary宽高、output物理宽高、source direction1–8、output direction1、operation3实际重编码、orientation status1均绑定真实对象。主/辅图的实际存储坐标关系须先由Native完整绑定证明，才能施加同一物理变换；辅图自有Exif/XML方向冲突或不可验证时拒绝。不得仅凭名称叫GainMap推断坐标关系。

IMv1仍使用28的原JPEG上下文模型：来源是同一精确完整JPEG artifact，只有已绑定主/辅真实codestream的marker取得合法global offset；按实际范围排序、无重叠并绑定全文件hash/length。原MPF/APP/COM可作为历史字节承接，但不成为当前HEIF item、路径、辅助图、live或mutation权限。embedded与materialized表示须由同一Native关系去重，不能重复封装；detached且无法绑定完整源JPEG的文件不得编造offset。

### 原始编码填充的独立保留检查

八项metadata ledger的顺序、编码、计数和含义保持不变。完整MPF关系内的GainMap实际JPEG EOI至该图范围末尾，如果还有最多64KiB的全零编码填充，须另外原样承接；非零、越界或无法证明真实EOI时拒绝。来源位置必须来自Native实际JPEG/MPF结构，不能由调用方提供或搜索任意零串。

填充用合法顶层UUID `LivePhotoBoxJPv1`保存：16B固定namespace、4B BE version1、4B零reserved、8B来源whole长度、32B来源SHA256、8B来源EOI全局offset、8B填充长度及完整原始零字节。该offset只说明历史来源上下文，不提供当前HEIF item/extent、live或mutation权限。记录唯一，重复、截断、错版本/reserved、长度溢出、非零payload或不匹配来源均拒绝。没有来源填充时产物也不得多出此记录。

kind2签发的必需条件是八项metadata核对成功，**且**映射前预期填充记录与实际合法HEIF顶层UUID逐字节重读相等。whole output hash用于把已经核实的字节绑定到stage及后续getter，不能代替上述前后保留检查。旧无填充正例不追溯称为已验证本新增承接。

## 目录、普通字段和实际owner

JPEG的Primary+GainMap目录描述旧JPEG布局，不能把旧Mime/Length原样放进HEIF再报完整保留。仅在闭合、唯一、完整核对的已知item形状下，将该布局表达映射为HEIF Primary-only XML；完整GainMap像素、ISO数值和关系由实际HEIF auxC/auxl和辅图owner XML表达。只可改已冻结的MIME、padding、方向、尺寸和已知GainMap布局项。所有将被移除的item属性、子节点和原始间隙内容均须已知；未知内容、subject、关系不得随item删除。

主/辅Exif、XML、ICC分别挂到实际owner并逐项重读；其他普通XML与Exif字节在明确映射范围之外保持。不能映射的缩略图、活动坐标或编辑配方继续拒绝。携带私有数据不等于其他品牌相册能使用原厂编辑功能，该功能仍以实际设备结果为准。

## 事务与交接

新证明的stage在整个有效期保留输入叶文件及祖先目录租约、来源identity/hash和自有输出stage。Getter在返回前核对实际stage identity/hash与原创建记录、完整八项对应关系及有效状态。Stage的identity在无覆盖发布时由同一个文件对象改名保持；commit前再次核对来源租约/身份和自有stage，commit后由Native发布创建回执及Core实际对象核对身份连续性。路径相等不能代替对象证明。

Core使用独立的HDR metadata evidence和校验器，不复用普通kind1的计数界限，也不放入名为ordinary的结果。新证明完整匹配实际source、stage和已绑定HDR结果时，才能据此核对Strict组件；发布交接保持输出租约，失败按实际creator authority清理。签发失败、取消或回滚不能留下半发布产物或虚假success。

本契约冻结实现边界，不代表已实现或验收通过。须完成实际Native正反例、四份当前来源的正式CLI、独立主/辅图与HDR数学重建的既定完整图/四象限门槛、对象生命周期、真实文件锁、fresh Review和另一Verify。全部来源/适用格式、GUI、设备两轮及完整取件包仍是协议与R8完成的必要条件。
