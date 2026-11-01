# R7 vivo 原图差异与媒体边界（施工中，非通过报告）

2026-10-27，LOCAL_OWNER。原始17文件与缓存哈希一致，11个单文件来源+3组成对来源，共14个语义组。R5正式TRX有14组来源→Neutral执行，但不代表新vivo目标成功。本轮复跑P6-S06通过；独立核查证明其旧视频解耦覆盖不足，不能继承为本轮完整N1通过。

## 真实原图确认

根层 vivo.jpg 的Exif为vivo / vivo X300 Ultra，SHA256 631CF8DAC983A9F58FC1D8CB63AAF8CC0569C45B80F046F1B995936D608320B9。合法JPEG主图2001908B、GainMap477680B、视频5790734B，XMP顺序Primary/GainMap/MotionPhoto，目录和实际边界一致。MPF主项旧长度2001308B短600B；Aux项范围正确。现有Neutral重建后MPF范围一致，不放宽目标MPF规则。

590B UserComment为裸UTF8、10个LF、尾NUL，无ASCII8B前缀。原文档关于GainMap必需/SDR两段存在直接冲突；整段相机状态必须复刻没有当前设备因果证据。新实验候选仅写最小live marker，不编造相机天气、温度、处理时间、camera-direct Source或私有MediaKit历史；已有合法raw相机comment保留，其他comment冲突拒绝。HDR保留原有资产，SDR不造GainMap。以上仅实验结构，设备NotRun。

## 视频实际结构与缺口

视频为HEVC Main10/HLG/BT.2020，62个视频sample；meta/mett轨有60个sample，MIME application/livePhoto-meta，实际59693B私有二进制内容；第三soun轨含8B空stbl，无stsd及任何音频sample table条目。离线主图和GainMap解码成功；全音视频解码因空soun轨无法选择音频decoder而失败。没有证据声称音频样本丢失，也不能声称完整音频解码成功。

尾部vivoMediaExtInfo UUID把livephoto、imageTime、livePhoto.firstFrame、重复footer配对ID与xdr.meta、faceInfo及相机状态混装。旧modern Cleaner只清图片XMP后原样复制视频；不能整删UUID。vivoMediaEStream UUID约1.32MB亦默认保留。元数据轨全部内容尚未分类，不依据MIME名字整轨删除。

R7范围内工程修正：新增只读结构观察与有精确selector/semantic/fingerprint/REBUILD_CONTAINER的modern清理动作；只移除已证实的三个live字段及重复配对ID，其他JSON成员raw字节、XDR、faceInfo、EStream及未知timed样本保持。Neutral内部footer空ID，更新长度，保留其framing；不声称该Neutral本身为vivo设备成品。目标门禁独立验证无live残留，不能绕过。

## 需要用户决定的媒体修复边界

原图空soun轨不是一个具有可验证编码/样本表的有效音轨。若保持原样，严格通用媒体验收不能称整视频有效；若修改其轨道语义，则涉及实际媒体结构修复，而P10 Repair尚未授权。

建议的有限方案：仅在合法moov/trak层级独立证明该soun轨没有stsd/stbl、没有音频sample/外部引用且无其他不明成员时，把该占位trak标为同长free，原字节其余部分保持；不重编码视频、不生成静音、不删metadata轨、不删XDR/EStream。这样该占位不再冒充可播放音轨，原占位数据仍留在文件中。任何非空、歧义、有关联或不符合精确条件的音轨fail closed。此举是有界媒体修复，须当前用户明确授权后才能实施，不能默认为普通Writer行为。

另一选择：保持源字节并明确拒绝此输入的目标发布，R7原始核心样本生命周期保持未完成，不能据此缩小验收范围而关R7。或者提供另一份直接拍摄、含有效音频表的>=X300原文件以补证；现有原图缺口仍保留。

详细逐样本、结构、命令、失败与候选在 `.ai-tmp/workspace/P9/target-writers/R7/`。当前未完成新Writer原图生命周期、独立保留、正式run、fresh候选Review/另一Verify、设备/切换、R7提交推送。旧sealed证据未改，未进入R8/P10。

## 2026-10-27 当前用户决定
当前用户明确选择“授权这项有界修复，继续 R7”。只授权上述严格无样本/无引用/无未知成员的空音轨同长度free转换；原字节其余部分保留，不扩展P10其他Repair，不授权删除非空音轨或metadata/HDR。新生产修复必须独立校验条件、输入身份与实际mutation site权限，失败关闭。

结构术语校正：第三音轨的stbl容器本身存在但为空（8B），并非整个stbl盒缺失；没有编码描述/样本条目的事实和用户已授权的无样本占位修复边界不变。生产条件只接受没有stbl或严格空stbl，不能把任意损坏/非空样本表当空轨。
